using TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Memory from <c>/proc/meminfo</c>. Instantaneous (no delta needed).
/// <c>MemAvailable</c> is the honest "free" figure — it accounts for reclaimable
/// caches/buffers — so used = total − available.
/// </summary>
public static class MemorySource
{
    public static MemoryMetrics Read() => Parse(File.ReadAllText("/proc/meminfo"));

    /// <summary>Pure parse of <c>/proc/meminfo</c> contents — golden-file testable.</summary>
    internal static MemoryMetrics Parse(string content)
    {
        long total = 0, avail = 0, swapTotal = 0, swapFree = 0, cached = 0, buffers = 0;

        foreach (var line in content.Split('\n'))
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
            // Cached/Buffers are mapped verbatim — SReclaimable is deliberately NOT folded
            // into Cached (some tools do; we report the raw /proc/meminfo values).
            else if (key.SequenceEqual("Cached")) cached = val;
            else if (key.SequenceEqual("Buffers")) buffers = val;
        }

        long used = total - avail;
        double usedPct = total > 0 ? Math.Round(100.0 * used / total, 1) : 0.0;
        long swapUsed = swapTotal - swapFree;
        return new MemoryMetrics(total, avail, used, usedPct, swapTotal, swapUsed, cached, buffers);
    }
}
