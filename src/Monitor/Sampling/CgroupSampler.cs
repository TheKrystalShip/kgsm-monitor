using System.Globalization;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Samples per-server resource usage from cgroup v2 counters. Given the current
/// watch-list, it resolves each instance to a candidate cgroup (via
/// <see cref="ServerCgroupResolver"/>), stats it, and — if present — reads
/// <c>cpu.stat</c>, <c>memory.current</c>, <c>pids.current</c>, the memory sizing
/// counters (<see cref="ServerMemory"/>) and (optionally) <c>io.stat</c>. A server
/// whose cgroup is absent is silently skipped (stopped, native-standalone, or an
/// unmatched container path).
/// <para>
/// CPU is a rate (<c>usage_usec</c> is cumulative) so this is stateful: it keeps
/// the previous counters per server id, mutated only on the sampling thread (no
/// lock needed — the host <see cref="MetricsSampler"/> calls <see cref="Sample"/>
/// single-threaded). State for a server that disappears is dropped on the next
/// tick. Parse and rate math are pure static helpers (golden-file testable).
/// </para>
/// <para>
/// <b>io is opt-in:</b> <c>io.stat</c> only exists when the io controller is
/// accounted for the cgroup (systemd <c>IOAccounting=yes</c>). When absent, the io
/// rates are null rather than zero — "not measured" is not "no I/O". Per-server network
/// follows the same null-not-zero rule (native via the eBPF meter, container via its netns).
/// </para>
/// </summary>
internal sealed class CgroupSampler
{
    private sealed class Prev
    {
        public long UsageUsec;
        public long IoReadBytes;
        public long IoWriteBytes;
        public bool HasIo;
        public long RxBytes;
        public long TxBytes;
        public bool HasNet;
    }

    private readonly Dictionary<string, Prev> _prev = new();
    private long _prevTicks;

    // Per-server network bytes from the pinned eBPF cgroup/skb meter (Phase 1). Cumulative
    // totals here → RxBps/TxBps rates below, exactly like io. Returns null (honest "unavailable")
    // when the meter isn't set up or the cgroup is outside kgsm.slice; never a fabricated 0.
    private readonly NetworkCgroupSource _net;

    internal CgroupSampler(ILogger? logger = null) => _net = new NetworkCgroupSource(logger);

    /// <summary>
    /// Whether the eBPF per-server network meter is readable right now.
    /// </summary>
    /// <remarks>
    /// Exposed so the sampler above can report the transition rather than only logging it once. The
    /// pin is re-probed every tick, so this genuinely flips both ways — a meter attached after the
    /// monitor started is picked up without a restart.
    /// </remarks>
    internal bool NetworkMeterAvailable => _net.Available;

    /// <summary>
    /// Produce one <see cref="ServerMetrics"/> per addressable, running server in
    /// <paramref name="instances"/> (the resync snapshot). Non-running / non-cgroup
    /// servers are absent from the result, not zero-valued.
    /// </summary>
    public ServerMetrics[] Sample(IReadOnlyDictionary<string, Instance> instances)
    {
        long now = Environment.TickCount64;
        double dt = _prevTicks == 0 ? 1.0 : Math.Max(1, now - _prevTicks) / 1000.0;

        var result = new List<ServerMetrics>(instances.Count);
        var seen = new HashSet<string>(instances.Count);

        foreach (var (id, instance) in instances)
        {
            var target = ServerCgroupResolver.Resolve(instance);
            string? dir = ServerCgroupResolver.FirstExisting(target.Candidates);
            if (dir is null)
                continue; // stopped / native with no live cgroup / unmatched container path

            // cpu.stat is the one read that must succeed — it's the rate anchor. If the
            // cgroup is torn down mid-read, skip the server this tick rather than emit junk.
            if (!TryReadText(Path.Combine(dir, "cpu.stat"), out string cpuStat))
                continue;
            long usageUsec = ParseCpuUsageUsec(cpuStat);
            if (usageUsec < 0)
                continue;

            long memBytes = TryReadText(Path.Combine(dir, "memory.current"), out string memTxt)
                ? ParseCounter(memTxt) : 0;
            ServerMemory? memory = ReadMemory(dir);
            int pids = TryReadText(Path.Combine(dir, "pids.current"), out string pidTxt)
                ? (int)ParseCounter(pidTxt) : 0;
            bool hasIo = TryReadText(Path.Combine(dir, "io.stat"), out string ioTxt);
            (long ioRead, long ioWrite) = hasIo ? ParseIoStat(ioTxt) : (0, 0);

            // Per-server network, by kind. NATIVE (under kgsm.slice): the eBPF cgroup/skb meter
            // (Phase 1) — null when the meter isn't set up, the cgroup is outside kgsm.slice, or
            // no traffic has crossed yet. CONTAINER (own netns on Docker's bridge, invisible to the
            // kgsm.slice meter): read its netns rx/tx from /proc/<pid>/net/dev (Phase 5). Either
            // way: cumulative bytes → RxBps/TxBps below; null is honest "unavailable", never a 0.
            (long RxBytes, long TxBytes)? net = target.Kind == "container"
                ? NetworkContainerSource.TryRead(dir)
                : _net.TryRead(dir);

            seen.Add(id);
            _prev.TryGetValue(id, out var prev);

            double cpuPctCore = 0;
            long? ioReadBps = null, ioWriteBps = null;
            long? rxBps = null, txBps = null;

            if (prev is not null && _prevTicks != 0)
            {
                cpuPctCore = ComputeCpuPctCore(prev.UsageUsec, usageUsec, dt);
                if (hasIo && prev.HasIo)
                {
                    ioReadBps = Math.Max(0, (long)((ioRead - prev.IoReadBytes) / dt));
                    ioWriteBps = Math.Max(0, (long)((ioWrite - prev.IoWriteBytes) / dt));
                }
                if (net is { } n && prev.HasNet)
                {
                    rxBps = Math.Max(0, (long)((n.RxBytes - prev.RxBytes) / dt));
                    txBps = Math.Max(0, (long)((n.TxBytes - prev.TxBytes) / dt));
                }
            }
            else
            {
                // First observation of this server: counters are known but a rate needs two
                // samples. Report 0 (measured-but-idle) rather than null (not-measured), but only
                // for the sources actually present this tick.
                if (hasIo)
                {
                    ioReadBps = 0;
                    ioWriteBps = 0;
                }
                if (net is not null)
                {
                    rxBps = 0;
                    txBps = 0;
                }
            }

            _prev[id] = new Prev
            {
                UsageUsec = usageUsec,
                IoReadBytes = ioRead,
                IoWriteBytes = ioWrite,
                HasIo = hasIo,
                RxBytes = net?.RxBytes ?? 0,
                TxBytes = net?.TxBytes ?? 0,
                HasNet = net is not null,
            };

            result.Add(new ServerMetrics(
                Id: id,
                Name: instance.Name.Length > 0 ? instance.Name : id,
                Kind: target.Kind,
                CpuPctCore: Math.Round(cpuPctCore, 1),
                MemBytes: memBytes,
                IoReadBps: ioReadBps,
                IoWriteBps: ioWriteBps,
                Pids: pids,
                DiskBytes: null, // merged from DiskUsageSampler in ServerSampler.Sample()
                RxBps: rxBps,
                TxBps: txBps,
                Memory: memory));
        }

        // Drop rate-state for servers that vanished this tick (stopped/removed) so the
        // dictionary tracks only live servers and a restarted server starts fresh.
        if (_prev.Count > seen.Count)
        {
            foreach (var key in _prev.Keys.Where(k => !seen.Contains(k)).ToList())
                _prev.Remove(key);
        }

        _prevTicks = now;
        return [.. result];
    }

    private static bool TryReadText(string path, out string content)
    {
        try
        {
            content = File.ReadAllText(path);
            return true;
        }
        catch
        {
            // cgroup file may vanish (teardown race) or lack the controller (io.stat).
            content = string.Empty;
            return false;
        }
    }

    // ---- pure helpers (golden-file testable) ----

    /// <summary>
    /// Cumulative CPU time (microseconds) from a <c>cpu.stat</c> body's
    /// <c>usage_usec</c> line. Returns -1 when the field is absent (cgroup without
    /// the cpu controller still exposes basic stats, but guard anyway).
    /// </summary>
    internal static long ParseCpuUsageUsec(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            if (!line.StartsWith("usage_usec", StringComparison.Ordinal))
                continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], out long v))
                return v;
        }
        return -1;
    }

    /// <summary>
    /// Sum of <c>rbytes</c>/<c>wbytes</c> across every device line of an
    /// <c>io.stat</c> body. cgroup io accounting is per block device; the snapshot
    /// reports the whole-server aggregate.
    /// </summary>
    internal static (long ReadBytes, long WriteBytes) ParseIoStat(string content)
    {
        long read = 0, write = 0;
        foreach (var line in content.Split('\n'))
        {
            if (line.Length == 0)
                continue;
            foreach (var tok in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (tok.StartsWith("rbytes=", StringComparison.Ordinal) && long.TryParse(tok.AsSpan(7), out long r))
                    read += r;
                else if (tok.StartsWith("wbytes=", StringComparison.Ordinal) && long.TryParse(tok.AsSpan(7), out long w))
                    write += w;
            }
        }
        return (read, write);
    }

    /// <summary>Single-integer cgroup file (<c>memory.current</c>, <c>pids.current</c>); 0 if non-numeric.</summary>
    internal static long ParseCounter(string content)
        => long.TryParse(content.AsSpan().Trim(), out long v) ? v : 0;

    /// <summary>
    /// The four sizing terms, read from one cgroup directory.
    /// </summary>
    /// <remarks>
    /// Five small reads on top of the four this sampler already makes, all from the same directory
    /// that has just been resolved and stat'd. <c>memory.max</c> is deliberately not among them: what
    /// the ceiling is set to is the watchdog's fact, and reading it here would let a consumer mistake
    /// this daemon for its author.
    /// <para>
    /// A cgroup without the memory controller exposes none of these files, and every field is
    /// independently nullable — a kernel that reports <c>memory.stat</c> but no PSI produces a working
    /// set and a null stall, rather than the whole record being dropped. All-null returns null: the
    /// record would otherwise assert that memory was measured and found to be nothing.
    /// </para>
    /// </remarks>
    private static ServerMemory? ReadMemory(string dir)
    {
        long? anon = TryReadText(Path.Combine(dir, "memory.stat"), out string statTxt)
            ? ParseMemStatAnon(statTxt) : null;
        long? swap = TryReadText(Path.Combine(dir, "memory.swap.current"), out string swapTxt)
            ? ParseCounter(swapTxt) : null;
        long? peak = TryReadText(Path.Combine(dir, "memory.peak"), out string peakTxt)
            ? ParseCounter(peakTxt) : null;

        long? oomKills = null, maxEvents = null;
        if (TryReadText(Path.Combine(dir, "memory.events"), out string evTxt))
            (oomKills, maxEvents) = ParseMemEvents(evTxt);

        double? stallPct = null;
        long? stallTotal = null;
        if (TryReadText(Path.Combine(dir, "memory.pressure"), out string psiTxt))
            (stallPct, stallTotal) = ParseMemPressureFull(psiTxt);

        if (anon is null && swap is null && peak is null && oomKills is null && stallPct is null)
            return null;

        return new ServerMemory(anon, swap, peak, oomKills, maxEvents, stallPct, stallTotal);
    }

    /// <summary>
    /// Anonymous bytes from a <c>memory.stat</c> body. Null when the field is absent — the file exists
    /// on every memory-controlled cgroup, so a missing <c>anon</c> means a kernel that does not report
    /// it rather than a workload holding nothing.
    /// </summary>
    internal static long? ParseMemStatAnon(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            if (!line.StartsWith("anon ", StringComparison.Ordinal))
                continue;
            return long.TryParse(line.AsSpan(5).Trim(), out long v) ? v : null;
        }
        return null;
    }

    /// <summary>
    /// <c>oom_kill</c> and <c>max</c> from a <c>memory.events</c> body — the two counters that say
    /// the workload was refused memory it asked for.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>oom_kill</c> is matched exactly, not by prefix: <c>oom_group_kill</c> is a different
    /// counter for a different thing, and a <c>StartsWith</c> would fold one into the other.
    /// </remarks>
    internal static (long? OomKills, long? MaxEvents) ParseMemEvents(string content)
    {
        long? oomKill = null, max = null;
        foreach (var line in content.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !long.TryParse(parts[1], out long v))
                continue;
            if (parts[0] == "oom_kill")
                oomKill = v;
            else if (parts[0] == "max")
                max = v;
        }
        return (oomKill, max);
    }

    /// <summary>
    /// The <c>full</c> line of a PSI <c>memory.pressure</c> body: <c>avg60</c> as a percentage and the
    /// cumulative stall total in microseconds.
    /// </summary>
    /// <remarks>
    /// <b><c>full</c>, not <c>some</c>.</b> <c>some</c> counts a window in which any task stalled,
    /// which a healthy server touches constantly; <c>full</c> counts one in which every task did, and
    /// is the line that means the workload was not running for want of memory.
    /// </remarks>
    internal static (double? AvgPct, long? TotalUsec) ParseMemPressureFull(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            if (!line.StartsWith("full ", StringComparison.Ordinal))
                continue;

            double? avg = null;
            long? total = null;
            foreach (var tok in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (tok.StartsWith("avg60=", StringComparison.Ordinal)
                    && double.TryParse(tok.AsSpan(6), NumberStyles.Float, CultureInfo.InvariantCulture, out double a))
                    avg = a;
                else if (tok.StartsWith("total=", StringComparison.Ordinal)
                    && long.TryParse(tok.AsSpan(6), out long t))
                    total = t;
            }
            return (avg, total);
        }
        return (null, null);
    }

    /// <summary>
    /// CPU usage as a percentage of <em>one</em> core over the elapsed window. A
    /// multi-core server can exceed 100 (htop per-process convention) — deliberately
    /// a different unit from host <c>cpu.totalPct</c>. Clamped at 0 to absorb counter
    /// resets on restart.
    /// </summary>
    internal static double ComputeCpuPctCore(long prevUsec, long curUsec, double dt)
    {
        if (dt <= 0)
            return 0;
        double pct = (curUsec - prevUsec) / (dt * 1_000_000.0) * 100.0;
        return pct < 0 ? 0 : pct;
    }
}
