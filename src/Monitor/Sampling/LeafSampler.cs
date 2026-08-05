using System.Diagnostics;
using System.Text.Json;

using TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Owns the per-leaf watch-list and turns it into <see cref="LeafMetrics"/> — the ecosystem's own daemons
/// (monitor, watchdog, api, assistant, bot, scheduler, firewall) measured the same way the game servers are.
/// <para>
/// Two cadences, the same split <see cref="ServerSampler"/> uses and for the same reason:
/// <list type="bullet">
/// <item><b>Resolve (slow, off the metrics tick):</b> discover which leaves exist, then find each one's
/// cgroup — a <c>systemctl show</c> spawn, the exact cost the metrics path avoids.</item>
/// <item><b>Sample (fast, the host tick):</b> read each resolved cgroup's counter files. No spawn, no lock.</item>
/// </list>
/// </para>
/// <para>
/// <b>Discovery is the descriptor directory, not a list held here.</b> Every leaf installs
/// <c>/var/lib/kgsm/leaves/&lt;id&gt;.json</c> declaring its id and unit — the same directory kgsm-api scans
/// to render the configuration page. Reading it rather than hard-coding a catalog means a leaf that joins
/// the ecosystem later is measured with no rebuild of this daemon, and that a leaf which was never deployed
/// here is simply absent.
/// </para>
/// <para>
/// <b>Resolution targets the main process's cgroup, not the unit's.</b> cgroup v2 counters are recursive, so
/// sampling the unit cgroup would charge a supervisor for everything it supervises: <c>kgsm-watchdog</c> runs
/// itself in a <c>supervisor</c> child and spawns each game server into a sibling, so its unit cgroup reads
/// as the servers' memory rather than the daemon's. <c>/proc/&lt;MainPID&gt;/cgroup</c> names the leaf-most
/// cgroup the daemon actually lives in; descendants of <em>that</em> stay counted, which is the boundary
/// that means "this leaf's own work".
/// </para>
/// <para>
/// This needs no privilege, no KGSM and no other leaf: <c>systemctl show</c> is an unprivileged read and the
/// kernel files are world-readable. It is independent of <see cref="MonitorOptions.KgsmEnabled"/> — a host
/// running leaves but no game servers still gets this.
/// </para>
/// </summary>
public sealed class LeafSampler(ILogger<LeafSampler> logger, MonitorOptions options) : BackgroundService
{
    /// <summary>A leaf resolved to a live cgroup: what to read, and what to call it.</summary>
    /// <param name="Id">The leaf id from its config descriptor.</param>
    /// <param name="Unit">The systemd unit it runs as.</param>
    /// <param name="CgroupDir">Absolute path under <c>/sys/fs/cgroup</c>, resolved from the main pid.</param>
    internal sealed record Target(string Id, string Unit, string CgroupDir);

    private sealed class Prev
    {
        public long UsageUsec;
        public long IoReadBytes;
        public long IoWriteBytes;
        public bool HasIo;
    }

    private const string CgroupRoot = "/sys/fs/cgroup";
    private static readonly TimeSpan ShowTimeout = TimeSpan.FromSeconds(5);

    // Rate state, keyed by leaf id. Mutated only on the sampling thread (MetricsSampler calls Sample()
    // single-threaded), so no lock — the same contract CgroupSampler relies on.
    private readonly Dictionary<string, Prev> _prev = new(StringComparer.Ordinal);
    private long _prevTicks;

    // Swapped wholesale by the resolve loop, read by the sampling tick. Reference assignment is atomic,
    // so a reader never sees a half-built list.
    private volatile IReadOnlyList<Target> _targets = [];

    // Coalescing resolve signal, single-writer drain loop — same shape as ServerSampler's resync. Capped
    // at 1, so a vanished cgroup nudging mid-resolve collapses into the pending one rather than queueing
    // a spawn storm.
    private readonly SemaphoreSlim _resolveSignal = new(0, 1);

    /// <summary>
    /// Read every resolved leaf's counters. A leaf whose cgroup has gone (restarted, stopped, or the
    /// socket-activated one going idle) is skipped for this frame and nudges a re-resolve, so it comes back
    /// on its own rather than waiting out the full period. Returns an empty array until the first resolve
    /// lands. Called on the host sampling thread.
    /// </summary>
    public LeafMetrics[] Sample()
    {
        IReadOnlyList<Target> targets = _targets;
        if (targets.Count == 0)
            return [];

        long now = Environment.TickCount64;
        double dt = _prevTicks == 0 ? 1.0 : Math.Max(1, now - _prevTicks) / 1000.0;

        var result = new List<LeafMetrics>(targets.Count);
        var seen = new HashSet<string>(targets.Count, StringComparer.Ordinal);
        bool stale = false;

        foreach (Target t in targets)
        {
            // cpu.stat is the rate anchor and the liveness check in one: if it's gone, the cgroup was torn
            // down under us and every other read this tick would be junk.
            if (!TryReadText(Path.Combine(t.CgroupDir, "cpu.stat"), out string cpuStat))
            {
                stale = true;
                continue;
            }
            long usageUsec = CgroupSampler.ParseCpuUsageUsec(cpuStat);
            if (usageUsec < 0)
                continue;

            long memBytes = TryReadText(Path.Combine(t.CgroupDir, "memory.current"), out string memTxt)
                ? CgroupSampler.ParseCounter(memTxt) : 0;
            int pids = TryReadText(Path.Combine(t.CgroupDir, "pids.current"), out string pidTxt)
                ? (int)CgroupSampler.ParseCounter(pidTxt) : 0;
            bool hasIo = TryReadText(Path.Combine(t.CgroupDir, "io.stat"), out string ioTxt);
            (long ioRead, long ioWrite) = hasIo ? CgroupSampler.ParseIoStat(ioTxt) : (0, 0);

            seen.Add(t.Id);
            _prev.TryGetValue(t.Id, out Prev? prev);

            double cpuPctCore = 0;
            long? ioReadBps = null, ioWriteBps = null;

            if (prev is not null && _prevTicks != 0)
            {
                cpuPctCore = CgroupSampler.ComputeCpuPctCore(prev.UsageUsec, usageUsec, dt);
                if (hasIo && prev.HasIo)
                {
                    ioReadBps = Math.Max(0, (long)((ioRead - prev.IoReadBytes) / dt));
                    ioWriteBps = Math.Max(0, (long)((ioWrite - prev.IoWriteBytes) / dt));
                }
            }
            else if (hasIo)
            {
                // First observation: the counters are known but a rate needs two samples. 0 here means
                // measured-and-idle, which is a different claim from the null an unaccounted io controller
                // gets — so it is only reported for a source actually present this tick.
                ioReadBps = 0;
                ioWriteBps = 0;
            }

            _prev[t.Id] = new Prev
            {
                UsageUsec = usageUsec,
                IoReadBytes = ioRead,
                IoWriteBytes = ioWrite,
                HasIo = hasIo,
            };

            result.Add(new LeafMetrics(
                Id: t.Id,
                Unit: t.Unit,
                CpuPctCore: Math.Round(cpuPctCore, 1),
                MemBytes: memBytes,
                IoReadBps: ioReadBps,
                IoWriteBps: ioWriteBps,
                Pids: pids));
        }

        // Drop rate-state for leaves that vanished, so a restarted leaf starts fresh rather than deriving
        // its first rate against counters from a dead cgroup.
        if (_prev.Count > seen.Count)
        {
            foreach (string key in _prev.Keys.Where(k => !seen.Contains(k)).ToList())
                _prev.Remove(key);
        }

        _prevTicks = now;
        if (stale)
            RequestResolve();

        return [.. result];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Resolve();   // prime, so the opening frames already carry leaves

        Task periodic = RunPeriodicNudgeAsync(stoppingToken);

        try
        {
            // Single-writer drain loop: the only place _targets is rewritten.
            while (true)
            {
                await _resolveSignal.WaitAsync(stoppingToken).ConfigureAwait(false);
                Resolve();
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }

        await periodic.ConfigureAwait(false);
    }

    private async Task RunPeriodicNudgeAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.LeafResolveMs));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                RequestResolve();
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private void RequestResolve()
    {
        try
        {
            _resolveSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // a resolve is already pending; coalesce
        }
    }

    /// <summary>
    /// Rebuild the target list: read the descriptor directory, ask systemd for each unit's main pid, and
    /// map each pid to the cgroup it lives in. A leaf that is not running, or whose cgroup can't be read,
    /// is simply left out — absent is the honest report, and it reappears on the next resolve.
    /// </summary>
    private void Resolve()
    {
        try
        {
            IReadOnlyList<(string Id, string Unit)> declared = ReadDescriptors(options.LeafDescriptorDir);
            if (declared.Count == 0)
            {
                _targets = [];
                logger.LogDebug("leaf resolve: no descriptors under {Dir}", options.LeafDescriptorDir);
                return;
            }

            IReadOnlyDictionary<string, int> pids = ReadMainPids([.. declared.Select(d => d.Unit)]);

            var targets = new List<Target>(declared.Count);
            foreach ((string id, string unit) in declared)
            {
                if (!pids.TryGetValue(unit, out int pid) || pid <= 0)
                    continue;                                   // not running — no cgroup to read
                string? dir = ResolveCgroupDir(pid);
                if (dir is null)
                    continue;                                   // exited between the two reads, or not cgroup v2
                targets.Add(new Target(id, unit, dir));
            }

            _targets = targets;
            logger.LogDebug("leaf resolve: {Live}/{Declared} leaf/leaves running", targets.Count, declared.Count);
        }
        catch (Exception ex)
        {
            // Keep the previous targets rather than blanking every leaf on a transient hiccup — the same
            // choice ServerSampler makes on a failed resync.
            logger.LogWarning(ex, "leaf resolve failed; keeping previous targets");
        }
    }

    /// <summary>
    /// The <c>(id, unit)</c> pair out of every leaf config descriptor in <paramref name="dir"/>. Parsed with
    /// <see cref="JsonDocument"/> — reflection-free (so it costs the AOT publish nothing) and tolerant, since
    /// this daemon cares about two fields of a document whose remaining shape belongs to the Control Panel.
    /// A malformed or unreadable file drops that one leaf rather than the scan.
    /// </summary>
    internal static IReadOnlyList<(string Id, string Unit)> ReadDescriptors(string dir)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(dir, "*.json");
        }
        catch
        {
            return [];   // directory absent (no leaf has deployed here yet) or unreadable
        }

        var found = new List<(string, string)>(files.Length);
        foreach (string file in files.Order(StringComparer.Ordinal))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(file));
                if (!doc.RootElement.TryGetProperty("id", out JsonElement idEl) ||
                    !doc.RootElement.TryGetProperty("unit", out JsonElement unitEl))
                    continue;
                string? id = idEl.GetString();
                string? unit = unitEl.GetString();
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(unit))
                    found.Add((id.Trim(), unit.Trim()));
            }
            catch
            {
                // Not a descriptor, or half-written by a deploy running right now. Skip this file only.
            }
        }
        return found;
    }

    /// <summary>
    /// Each unit's <c>MainPID</c> from one <c>systemctl show</c> call. Multiple units emit blank-line
    /// separated blocks each carrying <c>Id=</c>, so one spawn covers every leaf. A unit with no main
    /// process is absent from the result rather than mapped to 0.
    /// </summary>
    private IReadOnlyDictionary<string, int> ReadMainPids(IReadOnlyList<string> units)
    {
        var psi = new ProcessStartInfo(options.SystemctlPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("show");
        psi.ArgumentList.Add("--property=Id,MainPID");
        foreach (string u in units)
            psi.ArgumentList.Add(u);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
            return new Dictionary<string, int>(StringComparer.Ordinal);

        string stdout;
        try
        {
            stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit((int)ShowTimeout.TotalMilliseconds))
                throw new TimeoutException($"systemctl show did not exit within {ShowTimeout.TotalSeconds}s");
        }
        finally
        {
            if (!proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* race: exited */ }
            }
        }

        return ParseMainPids(stdout);
    }

    /// <summary>
    /// Map <c>systemctl show --property=Id,MainPID</c> output (blank-line separated blocks) to unit → pid.
    /// <c>MainPID=0</c> means "no main process" and is left out entirely, so a caller cannot mistake a
    /// stopped unit for one running as pid 0. Pure, so the parser is testable without a process.
    /// </summary>
    internal static IReadOnlyDictionary<string, int> ParseMainPids(string stdout)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        string? id = null;
        int pid = 0;

        void Flush()
        {
            if (id is { Length: > 0 } && pid > 0)
                result[id] = pid;
            id = null;
            pid = 0;
        }

        foreach (string rawLine in stdout.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                Flush();
                continue;
            }
            if (line.StartsWith("Id=", StringComparison.Ordinal))
                id = line[3..];
            else if (line.StartsWith("MainPID=", StringComparison.Ordinal))
                pid = int.TryParse(line[8..], out int p) ? p : 0;
        }
        Flush();
        return result;
    }

    /// <summary>
    /// The absolute cgroup directory a pid lives in, from its <c>/proc/&lt;pid&gt;/cgroup</c> unified line.
    /// Null when the process is gone, the host isn't cgroup v2, or the pid sits in the root cgroup (which
    /// exposes no counters worth reading).
    /// </summary>
    internal static string? ResolveCgroupDir(int pid, string procRoot = "/proc", string cgroupRoot = CgroupRoot)
    {
        if (!TryReadText(Path.Combine(procRoot, pid.ToString(), "cgroup"), out string content))
            return null;
        string? relative = ParseUnifiedPath(content);
        return relative is null ? null : Path.Combine(cgroupRoot, relative);
    }

    /// <summary>
    /// The cgroup v2 path from a <c>/proc/&lt;pid&gt;/cgroup</c> body, relative to the cgroup root (no
    /// leading slash — <see cref="Path.Combine(string, string)"/> would discard the root otherwise). Only
    /// the unified <c>0::</c> line counts: a v1 controller line addresses a different hierarchy whose
    /// numbers are not these. Null for the root cgroup, which has no counters to read.
    /// </summary>
    internal static string? ParseUnifiedPath(string content)
    {
        foreach (string line in content.Split('\n'))
        {
            if (!line.StartsWith("0::", StringComparison.Ordinal))
                continue;
            string path = line[3..].TrimEnd('\r').Trim();
            return path.Length <= 1 ? null : path.TrimStart('/');
        }
        return null;
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
            // The cgroup file may vanish (teardown race) or the controller may be absent (io.stat).
            content = string.Empty;
            return false;
        }
    }

    public override void Dispose()
    {
        _resolveSignal.Dispose();
        base.Dispose();
    }
}
