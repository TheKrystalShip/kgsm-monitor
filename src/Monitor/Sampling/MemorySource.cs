namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Memory from <c>/proc/meminfo</c>. Instantaneous (no delta needed).
/// <c>MemAvailable</c> is the honest "free" figure — it accounts for reclaimable
/// caches/buffers — so used = total − available.
/// </summary>
public static class MemorySource
{
    public static Model.MemoryMetrics Read()
    {
        long total = 0, avail = 0, swapTotal = 0, swapFree = 0;

        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            int colon = line.IndexOf(':');
            if (colon < 0)
                continue;

            ReadOnlySpan<char> key = line.AsSpan(0, colon);
            ReadOnlySpan<char> rest = line.AsSpan(colon + 1).Trim();
            int sp = rest.IndexOf(' ');
            ReadOnlySpan<char> num = sp < 0 ? rest : rest[..sp];
            if (!long.TryParse(num, out long val))
                continue;

            if (key.SequenceEqual("MemTotal")) total = val;
            else if (key.SequenceEqual("MemAvailable")) avail = val;
            else if (key.SequenceEqual("SwapTotal")) swapTotal = val;
            else if (key.SequenceEqual("SwapFree")) swapFree = val;
        }

        long used = total - avail;
        double usedPct = total > 0 ? Math.Round(100.0 * used / total, 1) : 0.0;
        long swapUsed = swapTotal - swapFree;
        return new Model.MemoryMetrics(total, avail, used, usedPct, swapTotal, swapUsed);
    }
}
