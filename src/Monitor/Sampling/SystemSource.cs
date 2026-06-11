using System.Globalization;
using TheKrystalShip.KGSM.Monitor.Model;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Cheap instantaneous extras htop shows: load averages (<c>/proc/loadavg</c>),
/// uptime (<c>/proc/uptime</c>), and the hostname. Parsing is split into pure
/// static helpers for golden-file tests.
/// </summary>
public static class SystemSource
{
    public static (LoadAvg Load, long UptimeSec, string Host) Read()
    {
        var load = ParseLoadAvg(File.ReadAllText("/proc/loadavg"));
        long uptime = ParseUptime(File.ReadAllText("/proc/uptime"));
        return (load, uptime, Environment.MachineName);
    }

    internal static LoadAvg ParseLoadAvg(string content)
    {
        var la = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new LoadAvg(D(la, 0), D(la, 1), D(la, 2));
    }

    internal static long ParseUptime(string content)
    {
        var up = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return up.Length > 0 && double.TryParse(up[0], CultureInfo.InvariantCulture, out double u)
            ? (long)u
            : 0;
    }

    private static double D(string[] a, int i) =>
        i < a.Length && double.TryParse(a[i], CultureInfo.InvariantCulture, out double v) ? v : 0.0;
}
