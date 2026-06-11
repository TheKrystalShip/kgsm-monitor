using System.Globalization;
using TheKrystalShip.KGSM.Monitor.Model;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Cheap instantaneous extras htop shows: load averages (<c>/proc/loadavg</c>),
/// uptime (<c>/proc/uptime</c>), and the hostname.
/// </summary>
public static class SystemSource
{
    public static (LoadAvg Load, long UptimeSec, string Host) Read()
    {
        var la = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var load = new LoadAvg(D(la, 0), D(la, 1), D(la, 2));

        long uptime = 0;
        var up = File.ReadAllText("/proc/uptime").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (up.Length > 0 && double.TryParse(up[0], CultureInfo.InvariantCulture, out double u))
            uptime = (long)u;

        return (load, uptime, Environment.MachineName);

        static double D(string[] a, int i) =>
            i < a.Length && double.TryParse(a[i], CultureInfo.InvariantCulture, out double v) ? v : 0.0;
    }
}
