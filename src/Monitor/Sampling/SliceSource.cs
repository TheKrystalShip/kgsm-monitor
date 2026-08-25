using TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// The KGSM parent cgroup's aggregate — <c>kgsm.slice</c> read as one cgroup, the way
/// <see cref="CgroupSampler"/> reads each instance's. cgroup v2 counters are recursive, so the slice's
/// own <c>cpu.stat</c> and <c>memory.current</c> already sum every descendant, present and mid-teardown
/// alike; measuring here rather than summing per-server rows is what makes the figure the honest
/// "everything the game servers collectively cost" number.
/// </summary>
/// <remarks>
/// Stateful for the same reason CgroupSampler is: CPU is a rate over a cumulative counter, so the first
/// observation carries a null <c>CpuPctCore</c> rather than a fabricated one. State is mutated only on
/// the sampling thread (<see cref="MetricsSampler"/> calls <see cref="Sample"/> single-threaded). A
/// missing slice resets the state and reads null — a host with no watchdog is an ordinary host, and the
/// rate anchor must not survive the slice being torn down and recreated. The directory is injectable so
/// the walk is golden-file testable against a synthetic tree.
/// </remarks>
public sealed class SliceSource(string sliceDir = "/sys/fs/cgroup/kgsm.slice")
{
    private readonly string _dir = sliceDir;

    private long _prevUsageUsec;
    private long _prevTicks;
    private bool _hasPrev;

    public SliceMetrics? Sample()
    {
        SliceMetrics? m = Read();
        if (m is null)
        {
            _hasPrev = false;
            _prevTicks = 0;
        }
        return m;
    }

    private SliceMetrics? Read()
    {
        if (!Directory.Exists(_dir))
            return null;

        // cpu.stat is the rate anchor, exactly as it is per-server: a slice directory torn down
        // mid-read yields null for this frame rather than junk.
        if (!TryReadText(Path.Combine(_dir, "cpu.stat"), out string cpuStat))
            return null;
        long usageUsec = CgroupSampler.ParseCpuUsageUsec(cpuStat);
        if (usageUsec < 0)
            return null;

        long now = Environment.TickCount64;
        double? cpuPctCore = null;
        if (_hasPrev && _prevTicks != 0)
        {
            double dt = Math.Max(1, now - _prevTicks) / 1000.0;
            cpuPctCore = Math.Round(CgroupSampler.ComputeCpuPctCore(_prevUsageUsec, usageUsec, dt), 1);
        }
        _prevUsageUsec = usageUsec;
        _prevTicks = now;
        _hasPrev = true;

        long? memBytes = TryReadText(Path.Combine(_dir, "memory.current"), out string memTxt)
            ? CgroupSampler.ParseCounter(memTxt) : null;
        int? pids = TryReadText(Path.Combine(_dir, "pids.current"), out string pidTxt)
            ? (int)CgroupSampler.ParseCounter(pidTxt) : null;

        return new SliceMetrics(cpuPctCore, memBytes, pids);
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
            // vanished mid-read, or the controller isn't enabled on this cgroup
            content = string.Empty;
            return false;
        }
    }
}
