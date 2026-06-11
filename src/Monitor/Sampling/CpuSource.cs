namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// CPU utilisation from <c>/proc/stat</c>. The kernel reports cumulative jiffies
/// per state; utilisation is the busy fraction of the delta between two reads
/// (exactly what top/htop do). Index 0 is the aggregate <c>cpu</c> line, the rest
/// are per-core <c>cpuN</c> lines. Parsing and the rate math are split into pure
/// static helpers so they can be golden-file tested without touching <c>/proc</c>.
/// </summary>
public sealed class CpuSource
{
    private long[] _prevIdle = [];
    private long[] _prevTotal = [];

    /// <returns>Aggregate busy percent and the per-core busy percents.</returns>
    public (double TotalPct, double[] PerCore) Sample()
    {
        var (idle, total) = Parse(File.ReadAllText("/proc/stat"));
        var result = ComputeRates(_prevIdle, _prevTotal, idle, total);
        _prevIdle = idle;
        _prevTotal = total;
        return result;
    }

    /// <summary>Parse the <c>cpu</c>/<c>cpuN</c> lines into idle and total jiffie counters.</summary>
    internal static (long[] Idle, long[] Total) Parse(string content)
    {
        var idle = new List<long>();
        var total = new List<long>();

        foreach (var line in content.Split('\n'))
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

        return ([.. idle], [.. total]);
    }

    /// <summary>
    /// Busy fraction of the delta: <c>(totalΔ − idleΔ) / totalΔ</c>. The interval cancels
    /// out, so this needs no wall-clock — only the two counter sets. Returns zeros if the
    /// shape changed (e.g. the very first sample, before priming).
    /// </summary>
    internal static (double TotalPct, double[] PerCore) ComputeRates(
        long[] prevIdle, long[] prevTotal, long[] idle, long[] total)
    {
        var pct = new double[idle.Length];
        if (prevTotal.Length == idle.Length)
        {
            for (int i = 0; i < idle.Length; i++)
            {
                long dt = total[i] - prevTotal[i];
                long di = idle[i] - prevIdle[i];
                pct[i] = dt > 0 ? Math.Round(100.0 * (dt - di) / dt, 1) : 0.0;
            }
        }

        double totalPct = pct.Length > 0 ? pct[0] : 0.0;
        double[] perCore = pct.Length > 1 ? pct[1..] : [];
        return (totalPct, perCore);
    }
}
