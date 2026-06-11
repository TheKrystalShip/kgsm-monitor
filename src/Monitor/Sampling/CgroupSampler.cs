using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Monitor.Model;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Samples per-server resource usage from cgroup v2 counters. Given the current
/// watch-list, it resolves each instance to a candidate cgroup (via
/// <see cref="ServerCgroupResolver"/>), stats it, and — if present — reads
/// <c>cpu.stat</c>, <c>memory.current</c>, <c>pids.current</c> and (optionally)
/// <c>io.stat</c>. A server whose cgroup is absent is silently skipped (stopped,
/// native-standalone, or an unmatched container path).
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
/// rates are null rather than zero — "not measured" is not "no I/O". This parallels
/// the host-only / no-per-server-network scope cut.
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
    }

    private readonly Dictionary<string, Prev> _prev = new();
    private long _prevTicks;

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
            string? dir = FirstExisting(target.Candidates);
            if (dir is null)
                continue; // stopped / native-standalone / unmatched container path

            // cpu.stat is the one read that must succeed — it's the rate anchor. If the
            // cgroup is torn down mid-read, skip the server this tick rather than emit junk.
            if (!TryReadText(Path.Combine(dir, "cpu.stat"), out string cpuStat))
                continue;
            long usageUsec = ParseCpuUsageUsec(cpuStat);
            if (usageUsec < 0)
                continue;

            long memBytes = TryReadText(Path.Combine(dir, "memory.current"), out string memTxt)
                ? ParseCounter(memTxt) : 0;
            int pids = TryReadText(Path.Combine(dir, "pids.current"), out string pidTxt)
                ? (int)ParseCounter(pidTxt) : 0;
            bool hasIo = TryReadText(Path.Combine(dir, "io.stat"), out string ioTxt);
            (long ioRead, long ioWrite) = hasIo ? ParseIoStat(ioTxt) : (0, 0);

            seen.Add(id);
            _prev.TryGetValue(id, out var prev);

            double cpuPctCore = 0;
            long? ioReadBps = null, ioWriteBps = null;

            if (prev is not null && _prevTicks != 0)
            {
                cpuPctCore = ComputeCpuPctCore(prev.UsageUsec, usageUsec, dt);
                if (hasIo && prev.HasIo)
                {
                    ioReadBps = Math.Max(0, (long)((ioRead - prev.IoReadBytes) / dt));
                    ioWriteBps = Math.Max(0, (long)((ioWrite - prev.IoWriteBytes) / dt));
                }
            }
            else if (hasIo)
            {
                // First observation of this server: counters are known but a rate needs
                // two samples. Report 0 (measured-but-idle) rather than null (not-measured).
                ioReadBps = 0;
                ioWriteBps = 0;
            }

            _prev[id] = new Prev
            {
                UsageUsec = usageUsec,
                IoReadBytes = ioRead,
                IoWriteBytes = ioWrite,
                HasIo = hasIo,
            };

            result.Add(new ServerMetrics(
                Id: id,
                Name: instance.Name.Length > 0 ? instance.Name : id,
                Kind: target.Kind,
                CpuPctCore: Math.Round(cpuPctCore, 1),
                MemBytes: memBytes,
                IoReadBps: ioReadBps,
                IoWriteBps: ioWriteBps,
                Pids: pids));
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

    private static string? FirstExisting(IReadOnlyList<string> candidates)
    {
        for (int i = 0; i < candidates.Count; i++)
            if (Directory.Exists(candidates[i]))
                return candidates[i];
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
