using System.Globalization;
using System.Runtime.InteropServices;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Monitor.Model;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Slice 3 — the <b>native fallback</b>. A server KGSM runs as a plain process (native:
/// no <c>compose_file</c>) has no dedicated cgroup the resolver knows, so the cgroup path
/// (<see cref="CgroupSampler"/>) cannot see it. This sampler reads the process tree
/// straight from <c>/proc</c> instead: the instance <c>.pid</c> file gives a root PID, we
/// invert <c>/proc</c>'s <c>ppid</c> links to collect that PID's whole subtree, and sum each
/// process's CPU/RSS/IO.
/// <para>
/// <b>Accuracy vs cgroups (honest limitation, not a bug):</b> a cgroup CPU counter is
/// cumulative, so it captures CPU burned by children that have since exited. A live
/// process-tree sum structurally cannot — when a helper process in the tree exits between
/// two ticks its <c>utime</c>/<c>stime</c> drops out of the current sum, producing a
/// negative delta that we clamp to 0. So a server that churns short-lived children reads
/// lumpy and biased slightly low. We keep the live-tree sum anyway: <c>cutime</c>/<c>cstime</c>
/// (<c>/proc/&lt;pid&gt;/stat</c> fields 16/17) count only <em>reaped</em> children and invite
/// double-counting. Likewise RSS is summed per-process, which double-counts pages shared
/// across the tree (an overcount vs the cgroup's <c>memory.current</c>). Both are
/// measured-and-labeled, never fabricated — the price of having no kernel aggregator.
/// </para>
/// <para>
/// <b>Cost:</b> inverting the tree needs <c>ppid</c>, which lives only in
/// <c>/proc/&lt;pid&gt;/stat</c>, so the scan reads <c>stat</c> for <em>every</em> process on
/// the host — it scales with host process count, not server count. It is therefore
/// <b>gated</b>: when the watch-list has no native server the scan is skipped entirely.
/// <c>statm</c> (RSS) and <c>io</c> are read only for PIDs in a tracked subtree — a tiny set —
/// never for every host process.
/// </para>
/// <para>
/// State (previous CPU/IO counters and each root's <c>starttime</c>) is mutated only on the
/// single host sampling thread (<see cref="MetricsSampler"/> calls <see cref="Sample"/> once
/// per tick), so no lock is needed. State for a server that vanishes is dropped next tick.
/// </para>
/// <para>
/// <b>Identity is the <c>.pid</c> file — KGSM's, not ours.</b> We trust the number in the
/// instance <c>.pid</c> exactly as KGSM's own native status handler does (it <c>cat</c>s the
/// file and <c>ps</c>-es that pid with no further check). KGSM removes the file on a clean stop,
/// so a stopped server reads as absent here. The residual gap is a crash/SIGKILL that bypasses
/// KGSM's cleanup, leaving a stale <c>.pid</c> whose number the kernel later reuses: on the
/// <em>first</em> observation we have no prior <c>starttime</c> to compare, so we would attribute
/// the reusing process's metrics to the server — the same mis-attribution <c>kgsm instances
/// status</c> would make. The <c>starttime</c> pin only catches a change <em>across</em> ticks
/// (suppressing a cross-process rate), not a foreigner adopted cold. We deliberately do not add
/// a stronger identity heuristic (e.g. matching <c>/proc/&lt;pid&gt;/cwd</c>) because that would
/// disagree with the instance authority and risk hiding a real server; the proper fix is KGSM
/// recording process identity alongside the pid. See PLAN §12.
/// </para>
/// </summary>
internal sealed partial class ProcTreeSampler
{
    /// <summary>The fields of <c>/proc/&lt;pid&gt;/stat</c> needed to invert the tree and sum CPU.</summary>
    internal readonly record struct ProcInfo(int Pid, int Ppid, long CpuTicks, long StartTime);

    private sealed class Prev
    {
        public long CpuTicks;
        public long IoRead;
        public long IoWrite;
        public bool HasIo;
        public long StartTime; // root PID's starttime — identity pin against PID recycling
    }

    private readonly string _procRoot;
    private readonly long _clockHz;
    private readonly Dictionary<string, Prev> _prev = new();
    private long _prevTicks;

    public ProcTreeSampler(string procRoot = "/proc")
    {
        _procRoot = procRoot;
        long hz = ClockTicksPerSecond();
        // USER_HZ fallback (100 on essentially all Linux) so a wrong _SC_CLK_TCK constant or a
        // failed sysconf degrades to the correct value rather than fabricating CPU%.
        _clockHz = hz > 0 ? hz : 100;
    }

    /// <summary>
    /// Produce one <see cref="ServerMetrics"/> per running native-standalone server in
    /// <paramref name="instances"/> (the resync snapshot). Stopped / non-native servers are
    /// absent from the result, not zero-valued.
    /// </summary>
    public ServerMetrics[] Sample(IReadOnlyDictionary<string, Instance> instances)
    {
        long now = Environment.TickCount64;
        double dt = _prevTicks == 0 ? 1.0 : Math.Max(1, now - _prevTicks) / 1000.0;

        // The only kind this sampler owns is native-standalone. Key on the resolver's
        // classification (single source of truth) so a broken container — Kind "container",
        // unaddressable — is NOT misrouted onto the process-tree path.
        var natives = new List<KeyValuePair<string, Instance>>();
        foreach (var kv in instances)
            if (ServerCgroupResolver.Resolve(kv.Value).Kind == "native")
                natives.Add(kv);

        var seen = new HashSet<string>(natives.Count);
        var result = new List<ServerMetrics>(natives.Count);

        // Cost gate: skip ONLY the expensive whole-/proc scan when there is no native server.
        // The _prev cleanup and _prevTicks update below still run, so a native instance
        // reconfigured back in later re-primes cleanly instead of smearing a stale rate.
        if (natives.Count > 0)
        {
            var procs = ScanProcStats();          // pid -> ProcInfo for every host process
            var children = BuildChildren(procs);  // ppid -> child pids

            foreach (var (id, instance) in natives)
            {
                int rootPid = ReadPid(instance.PidFile);
                if (rootPid <= 0 || !procs.TryGetValue(rootPid, out var root))
                    continue; // stopped / stale .pid / not yet started -> absent (stat-and-skip parity)

                // PID-recycle guard: the .pid file holds a number the kernel reuses. If we have
                // a prior starttime for this server and the PID's starttime changed, the file now
                // points at an unrelated process — skip this tick and let it re-prime next tick
                // (its stale _prev entry is dropped below, since it is never added to `seen`).
                if (_prev.TryGetValue(id, out var pinned) && pinned.StartTime != 0 && pinned.StartTime != root.StartTime)
                    continue;

                long cpuTicks = 0, rss = 0, ioRead = 0, ioWrite = 0;
                int pidCount = 0;
                bool hasIo = false;

                // statm + io are read here — for tree members only, never for every host process.
                foreach (int pid in CollectTree(rootPid, children))
                {
                    if (procs.TryGetValue(pid, out var pi))
                        cpuTicks += pi.CpuTicks;
                    rss += ReadRssBytes(pid);
                    if (TryReadIo(pid, out long r, out long w))
                    {
                        ioRead += r;
                        ioWrite += w;
                        hasIo = true;
                    }
                    pidCount++;
                }

                _prev.TryGetValue(id, out var prev);

                double cpuPctCore = 0;
                long? ioReadBps = null, ioWriteBps = null;

                if (prev is not null && _prevTicks != 0)
                {
                    cpuPctCore = ComputeCpuPctCore(prev.CpuTicks, cpuTicks, _clockHz, dt);
                    if (hasIo && prev.HasIo)
                    {
                        ioReadBps = Math.Max(0, (long)((ioRead - prev.IoRead) / dt));
                        ioWriteBps = Math.Max(0, (long)((ioWrite - prev.IoWrite) / dt));
                    }
                }
                else if (hasIo)
                {
                    // First observation: counters known but a rate needs two samples. Report 0
                    // (measured-but-idle) rather than null. Native io is read straight from
                    // /proc as root, so unlike the cgroup path it needs no IOAccounting opt-in.
                    ioReadBps = 0;
                    ioWriteBps = 0;
                }

                seen.Add(id);
                _prev[id] = new Prev
                {
                    CpuTicks = cpuTicks,
                    IoRead = ioRead,
                    IoWrite = ioWrite,
                    HasIo = hasIo,
                    StartTime = root.StartTime,
                };

                result.Add(new ServerMetrics(
                    Id: id,
                    Name: instance.Name.Length > 0 ? instance.Name : id,
                    Kind: "native",
                    CpuPctCore: Math.Round(cpuPctCore, 1),
                    MemBytes: rss,
                    IoReadBps: ioReadBps,
                    IoWriteBps: ioWriteBps,
                    Pids: pidCount));
            }
        }

        // Drop rate-state for servers that vanished this tick (stopped/removed/recycled) so a
        // restarted server starts fresh. Runs regardless of the cost gate above.
        if (_prev.Count > seen.Count)
        {
            foreach (var key in _prev.Keys.Where(k => !seen.Contains(k)).ToList())
                _prev.Remove(key);
        }

        _prevTicks = now;
        return [.. result];
    }

    // ---- /proc I/O ----

    /// <summary>Read <c>stat</c> for every numeric <c>/proc</c> entry into a pid-keyed map.</summary>
    private Dictionary<int, ProcInfo> ScanProcStats()
    {
        var procs = new Dictionary<int, ProcInfo>();
        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(_procRoot);
        }
        catch
        {
            return procs;
        }

        foreach (var dir in dirs)
        {
            // Only numeric entries are PIDs (skips `self`, `sys`, `net`, ...).
            if (!IsAllDigits(Path.GetFileName(dir)))
                continue;
            if (TryReadText(Path.Combine(dir, "stat"), out string content) && ParseProcStat(content) is { } info)
                procs[info.Pid] = info;
        }

        return procs;
    }

    private long ReadRssBytes(int pid)
    {
        if (TryReadText(Path.Combine(_procRoot, pid.ToString(CultureInfo.InvariantCulture), "statm"), out string s))
            return ParseStatmRssPages(s) * Environment.SystemPageSize;
        return 0;
    }

    private bool TryReadIo(int pid, out long read, out long write)
    {
        if (TryReadText(Path.Combine(_procRoot, pid.ToString(CultureInfo.InvariantCulture), "io"), out string s))
        {
            (read, write) = ParseProcIo(s);
            return true;
        }

        read = write = 0;
        return false;
    }

    /// <summary>Root PID from the instance <c>.pid</c> file, or -1 when absent/non-numeric.</summary>
    private static int ReadPid(string pidFile)
        => int.TryParse(ServerCgroupResolver.ReadFirstLine(pidFile), out int pid) ? pid : -1;

    private static bool TryReadText(string path, out string content)
    {
        try
        {
            content = File.ReadAllText(path);
            return true;
        }
        catch
        {
            // The process may exit mid-scan (its /proc dir vanishes) — skip it.
            content = string.Empty;
            return false;
        }
    }

    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0)
            return false;
        foreach (char c in s)
            if (c is < '0' or > '9')
                return false;
        return true;
    }

    // ---- pure helpers (golden-file / hand-built testable) ----

    /// <summary>
    /// Parse the fields we need from a <c>/proc/&lt;pid&gt;/stat</c> body. Field 2
    /// (<c>comm</c>) is wrapped in parentheses and may itself contain spaces <em>and</em>
    /// parentheses (e.g. <c>(My Game (x64))</c>), so a naive whitespace split is wrong: the
    /// pid is everything before the first <c>(</c>, and fields 3.. are everything after the
    /// <em>last</em> <c>)</c>. Index into that tail is <c>field - 3</c>, so ppid=1, utime=11,
    /// stime=12, starttime=19. Returns null on a malformed line.
    /// </summary>
    internal static ProcInfo? ParseProcStat(string content)
    {
        int open = content.IndexOf('(');
        int close = content.LastIndexOf(')');
        if (open < 0 || close <= open)
            return null;
        if (!int.TryParse(content.AsSpan(0, open).Trim(), out int pid))
            return null;

        string[] f = content[(close + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        const int ppidIdx = 1, utimeIdx = 11, stimeIdx = 12, startIdx = 19;
        if (f.Length <= startIdx || !int.TryParse(f[ppidIdx], out int ppid))
            return null;

        long cpuTicks = ParseLong(f[utimeIdx]) + ParseLong(f[stimeIdx]);
        return new ProcInfo(pid, ppid, cpuTicks, ParseLong(f[startIdx]));
    }

    /// <summary>Resident set size in <em>pages</em> from a <c>/proc/&lt;pid&gt;/statm</c> body (field 2).</summary>
    internal static long ParseStatmRssPages(string content)
    {
        string[] parts = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out long pages) ? pages : 0;
    }

    /// <summary>
    /// <c>read_bytes</c>/<c>write_bytes</c> from a <c>/proc/&lt;pid&gt;/io</c> body — the
    /// storage-layer counters (post-page-cache), matching the cgroup <c>io.stat</c> unit
    /// rather than the syscall-layer <c>rchar</c>/<c>wchar</c>.
    /// </summary>
    internal static (long Read, long Write) ParseProcIo(string content)
    {
        long read = 0, write = 0;
        foreach (var line in content.Split('\n'))
        {
            if (line.StartsWith("read_bytes:", StringComparison.Ordinal))
                read = ParseLong(line.AsSpan("read_bytes:".Length).Trim());
            else if (line.StartsWith("write_bytes:", StringComparison.Ordinal))
                write = ParseLong(line.AsSpan("write_bytes:".Length).Trim());
        }
        return (read, write);
    }

    /// <summary>Invert the flat process map into a <c>ppid -&gt; child pids</c> adjacency.</summary>
    internal static Dictionary<int, List<int>> BuildChildren(IReadOnlyDictionary<int, ProcInfo> procs)
    {
        var children = new Dictionary<int, List<int>>();
        foreach (var p in procs.Values)
        {
            if (!children.TryGetValue(p.Ppid, out var list))
                children[p.Ppid] = list = new List<int>();
            list.Add(p.Pid);
        }
        return children;
    }

    /// <summary>
    /// Breadth-first collection of <paramref name="rootPid"/> and all its descendants. A
    /// <c>visited</c> set guards against a <c>ppid</c> cycle (which a post-recycle race could
    /// briefly create) so the walk always terminates.
    /// </summary>
    internal static List<int> CollectTree(int rootPid, IReadOnlyDictionary<int, List<int>> children)
    {
        var result = new List<int>();
        var visited = new HashSet<int> { rootPid };
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);

        while (queue.Count > 0)
        {
            int pid = queue.Dequeue();
            result.Add(pid);
            if (children.TryGetValue(pid, out var kids))
                foreach (int kid in kids)
                    if (visited.Add(kid))
                        queue.Enqueue(kid);
        }

        return result;
    }

    /// <summary>
    /// CPU usage as a percentage of <em>one</em> core over the elapsed window (htop per-process
    /// convention; a multi-core server can exceed 100). <c>utime+stime</c> are in clock ticks,
    /// so the divisor is <paramref name="clockHz"/> (USER_HZ). Clamped at 0 — a negative delta
    /// is almost always a tree child exiting between ticks (see the class remarks), not a real
    /// reduction.
    /// </summary>
    internal static double ComputeCpuPctCore(long prevTicks, long curTicks, long clockHz, double dt)
    {
        if (dt <= 0 || clockHz <= 0)
            return 0;
        double coreSeconds = (curTicks - prevTicks) / (double)clockHz;
        double pct = coreSeconds / dt * 100.0;
        return pct < 0 ? 0 : pct;
    }

    private static long ParseLong(ReadOnlySpan<char> s) => long.TryParse(s, out long v) ? v : 0;

    // ---- libc ----

    // USER_HZ (clock ticks per second) for converting /proc utime+stime to seconds. No managed
    // API exposes it; sysconf(_SC_CLK_TCK) is the canonical source. _SC_CLK_TCK == 2 is the
    // glibc/musl enum value on Linux (verified against bits/confname.h on the target). Blittable
    // signature -> AOT-clean; guarded by a 100 fallback in the constructor.
    private const int SC_CLK_TCK = 2;

    [LibraryImport("libc")]
    private static partial long sysconf(int name);

    private static long ClockTicksPerSecond()
    {
        try
        {
            return sysconf(SC_CLK_TCK);
        }
        catch
        {
            return 0;
        }
    }
}
