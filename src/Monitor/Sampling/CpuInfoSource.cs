using System.Globalization;
using TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Static CPU identity from <c>/proc/cpuinfo</c> (model / cores / threads) and
/// <c>/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq</c> (stable max clock).
/// Unlike the per-tick sources this is read <em>once</em> at startup and reused on every
/// frame — the values don't change at runtime, so re-parsing the 16-block cpuinfo each
/// tick would be waste. Parsing is a pure static helper for golden-file tests.
/// </summary>
/// <remarks>
/// Honesty: every field is the measured value or <c>null</c> — never guessed. The
/// <c>maxFreq</c> comes from the stable <c>cpuinfo_max_freq</c> file, deliberately not the
/// jittery instantaneous <c>cpu MHz</c> line.
///
/// Single-socket simplification: <c>Cores</c> is the <em>first</em> socket's <c>cpu cores</c>
/// value. This host (and the common case) has one <c>physical id</c>; on a true multi-socket
/// box this under-reports the box-total core count, while <c>Threads</c> (the count of
/// <c>processor</c> lines) stays correct. Folding cores across sockets is deferred until a
/// real multi-socket host exists — guessing a sum would risk fabricating a number.
/// </remarks>
public static class CpuInfoSource
{
    private const string CpuFreqMaxPath =
        "/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq";

    /// <summary>Read the static CPU identity from the live host.</summary>
    public static CpuInfo Read()
    {
        string cpuinfo = TryReadAllText("/proc/cpuinfo") ?? string.Empty;
        double? maxGhz = ParseMaxFreqGhz(TryReadAllText(CpuFreqMaxPath));
        return Parse(cpuinfo, maxGhz);
    }

    /// <summary>
    /// Pure parse: <c>model name</c>, <c>cpu cores</c> (first socket), and the <c>processor</c>-line
    /// count (= threads). <paramref name="maxFreqGhz"/> is supplied by the caller (it comes from a
    /// separate <c>/sys</c> file) so this helper is fully golden-file testable.
    /// </summary>
    internal static CpuInfo Parse(string cpuinfo, double? maxFreqGhz)
    {
        string? model = null;
        int? cores = null;
        int threads = 0;

        foreach (var line in cpuinfo.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0)
                continue;

            ReadOnlySpan<char> key = line.AsSpan(0, colon).Trim();
            ReadOnlySpan<char> val = line.AsSpan(colon + 1).Trim();

            if (key.SequenceEqual("processor"))
                threads++;
            else if (model is null && key.SequenceEqual("model name"))
                model = val.ToString();
            // First socket's "cpu cores" only — the file repeats it per logical CPU and
            // (on a multi-socket host) per socket; we deliberately take the first.
            else if (cores is null && key.SequenceEqual("cpu cores") && int.TryParse(val, out int c))
                cores = c;
        }

        return new CpuInfo(
            Model: model,
            Cores: cores,
            Threads: threads > 0 ? threads : null,
            MaxFreqGhz: maxFreqGhz);
    }

    /// <summary>kHz (the <c>cpuinfo_max_freq</c> unit) → GHz; <c>null</c> when cpufreq is absent/unreadable.</summary>
    internal static double? ParseMaxFreqGhz(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) ||
            !long.TryParse(content.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long khz) ||
            khz <= 0)
            return null;

        return Math.Round(khz / 1_000_000.0, 2);
    }

    private static string? TryReadAllText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }
}
