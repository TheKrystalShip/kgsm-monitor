namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// CPU utilisation from <c>/proc/stat</c>. The kernel reports cumulative jiffies
/// per state; utilisation is the busy fraction of the delta between two reads
/// (exactly what top/htop do). Index 0 is the aggregate <c>cpu</c> line, the rest
/// are per-core <c>cpuN</c> lines.
/// </summary>
public sealed class CpuSource
{
    private long[] _prevIdle = [];
    private long[] _prevTotal = [];

    /// <returns>Aggregate busy percent and the per-core busy percents.</returns>
    public (double TotalPct, double[] PerCore) Sample()
    {
        var idle = new List<long>();
        var total = new List<long>();

        foreach (var line in File.ReadLines("/proc/stat"))
        {
            if (!line.StartsWith("cpu", StringComparison.Ordinal))
                break; // cpu lines are first; stop at intr/ctxt/...

            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            long sum = 0;
            for (int i = 1; i < f.Length; i++)
                sum += long.Parse(f[i]);

            // idle + iowait are the "not busy" buckets.
            long idleAll = 0;
            if (f.Length > 4) idleAll += long.Parse(f[4]);
            if (f.Length > 5) idleAll += long.Parse(f[5]);

            idle.Add(idleAll);
            total.Add(sum);
        }

        var pct = new double[idle.Count];
        if (_prevTotal.Length == idle.Count)
        {
            for (int i = 0; i < idle.Count; i++)
            {
                long dt = total[i] - _prevTotal[i];
                long di = idle[i] - _prevIdle[i];
                pct[i] = dt > 0 ? Math.Round(100.0 * (dt - di) / dt, 1) : 0.0;
            }
        }

        _prevIdle = [.. idle];
        _prevTotal = [.. total];

        double totalPct = pct.Length > 0 ? pct[0] : 0.0;
        double[] perCore = pct.Length > 1 ? pct[1..] : [];
        return (totalPct, perCore);
    }
}
