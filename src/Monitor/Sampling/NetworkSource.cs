using TheKrystalShip.KGSM.Monitor.Model;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Per-interface throughput from <c>/proc/net/dev</c> (cumulative bytes/packets),
/// turned into per-second rates against the previous sample. The loopback
/// interface is excluded. Host-level only — per-server network is intentionally
/// out of scope (needs eBPF; see PLAN.md).
/// </summary>
public sealed class NetworkSource
{
    // name -> [rxBytes, rxPackets, txBytes, txPackets]
    private readonly Dictionary<string, long[]> _prev = new();
    private long _prevTicks;

    public NetworkMetrics Sample()
    {
        long now = Environment.TickCount64;
        double dt = _prevTicks == 0 ? 1.0 : Math.Max(1, now - _prevTicks) / 1000.0;

        var ifaces = new List<InterfaceRate>();
        var cur = new Dictionary<string, long[]>();

        foreach (var line in File.ReadLines("/proc/net/dev"))
        {
            int colon = line.IndexOf(':');
            if (colon < 0)
                continue; // the two header rows have no colon

            string name = line.AsSpan(0, colon).Trim().ToString();
            var f = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 16)
                continue;

            long rxB = long.Parse(f[0]), rxP = long.Parse(f[1]);
            long txB = long.Parse(f[8]), txP = long.Parse(f[9]);
            cur[name] = [rxB, rxP, txB, txP];

            if (name == "lo")
                continue;

            if (_prev.TryGetValue(name, out var p))
            {
                ifaces.Add(new InterfaceRate(
                    name,
                    RxBps: (long)((rxB - p[0]) / dt),
                    TxBps: (long)((txB - p[2]) / dt),
                    RxPps: (long)((rxP - p[1]) / dt),
                    TxPps: (long)((txP - p[3]) / dt)));
            }
        }

        foreach (var kv in cur)
            _prev[kv.Key] = kv.Value;
        _prevTicks = now;

        return new NetworkMetrics([.. ifaces]);
    }
}
