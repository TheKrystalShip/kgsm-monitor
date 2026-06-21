using TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Per-interface throughput from <c>/proc/net/dev</c> (cumulative bytes/packets),
/// turned into per-second rates against the previous sample. Loopback is always
/// excluded; <paramref name="denyPrefixes"/> drops additional virtual interfaces
/// (e.g. <c>veth</c>). Host-level only — per-server network is intentionally out of
/// scope (needs eBPF; see PLAN.md). Parse and rate math are pure static helpers.
/// </summary>
public sealed class NetworkSource(IReadOnlyList<string> denyPrefixes, string netRoot = "/sys/class/net")
{
    private readonly IReadOnlyList<string> _deny = denyPrefixes;
    private readonly string _netRoot = netRoot;

    // name -> [rxBytes, rxPackets, txBytes, txPackets]
    private Dictionary<string, long[]> _prev = new();
    private long _prevTicks;

    public NetworkMetrics Sample()
    {
        long now = Environment.TickCount64;
        double dt = _prevTicks == 0 ? 1.0 : Math.Max(1, now - _prevTicks) / 1000.0;

        var cur = Parse(File.ReadAllText("/proc/net/dev"));
        var metrics = ComputeRates(_prev, cur, dt, _deny);

        // Enrich each surviving interface with its static MAC + cumulative error count
        // from /sys/class/net/<if>/ — kept out of the pure rate math (it reads the
        // filesystem, not the counter snapshots).
        var enriched = Array.ConvertAll(metrics.Ifaces, i => i with
        {
            Mac = ReadMac(_netRoot, i.Name),
            Errors = ReadErrors(_netRoot, i.Name),
        });

        _prev = cur;
        _prevTicks = now;
        return new NetworkMetrics(enriched);
    }

    /// <summary>Parse <c>/proc/net/dev</c> into <c>name -> [rxBytes, rxPackets, txBytes, txPackets]</c>.</summary>
    internal static Dictionary<string, long[]> Parse(string content)
    {
        var cur = new Dictionary<string, long[]>();

        foreach (var line in content.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0)
                continue; // the two header rows have no colon

            string name = line.AsSpan(0, colon).Trim().ToString();
            var f = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 16)
                continue;

            cur[name] = [long.Parse(f[0]), long.Parse(f[1]), long.Parse(f[8]), long.Parse(f[9])];
        }

        return cur;
    }

    /// <summary>Per-second rates from two counter snapshots and the elapsed seconds.</summary>
    internal static NetworkMetrics ComputeRates(
        Dictionary<string, long[]> prev, Dictionary<string, long[]> cur,
        double dt, IReadOnlyList<string> denyPrefixes)
    {
        var ifaces = new List<InterfaceRate>();

        foreach (var kv in cur)
        {
            string name = kv.Key;
            if (name == "lo" || IsDenied(name, denyPrefixes))
                continue;

            if (prev.TryGetValue(name, out var p))
            {
                var c = kv.Value;
                // Mac/Errors are enriched in Sample() (filesystem reads, not counter math),
                // so the pure rate helper leaves them null.
                ifaces.Add(new InterfaceRate(
                    name,
                    RxBps: (long)((c[0] - p[0]) / dt),
                    TxBps: (long)((c[2] - p[2]) / dt),
                    RxPps: (long)((c[1] - p[1]) / dt),
                    TxPps: (long)((c[3] - p[3]) / dt),
                    Mac: null,
                    Errors: null));
            }
        }

        return new NetworkMetrics([.. ifaces]);
    }

    private static bool IsDenied(string name, IReadOnlyList<string> prefixes)
    {
        for (int i = 0; i < prefixes.Count; i++)
            if (name.StartsWith(prefixes[i], StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>MAC from <c>/sys/class/net/&lt;if&gt;/address</c>; <c>null</c> when unreadable.</summary>
    internal static string? ReadMac(string netRoot, string iface)
    {
        string? mac = ReadTrimmed(Path.Combine(netRoot, iface, "address"));
        return string.IsNullOrEmpty(mac) ? null : mac;
    }

    /// <summary>
    /// Total link errors = <c>statistics/rx_errors</c> + <c>tx_errors</c>. <c>null</c> only when
    /// <em>neither</em> file reads — a single readable side still yields a measured total, and a
    /// genuine zero stays 0 (never conflated with "unknown").
    /// </summary>
    internal static long? ReadErrors(string netRoot, string iface)
    {
        long? rx = ReadLong(Path.Combine(netRoot, iface, "statistics", "rx_errors"));
        long? tx = ReadLong(Path.Combine(netRoot, iface, "statistics", "tx_errors"));
        if (rx is null && tx is null)
            return null;
        return (rx ?? 0) + (tx ?? 0);
    }

    private static long? ReadLong(string path)
    {
        string? s = ReadTrimmed(path);
        return long.TryParse(s, out long v) ? v : null;
    }

    private static string? ReadTrimmed(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }
}
