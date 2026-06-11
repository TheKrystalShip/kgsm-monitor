namespace TheKrystalShip.KGSM.Monitor.Model;

/// <summary>
/// One host metrics frame. Produced by the sampler once per tick and served
/// verbatim from <c>GET /metrics</c>. Rates (cpu %, net bps, disk bps) are
/// computed from the delta against the previous sample — which is why the
/// sampler is stateful and self-ticking rather than sampling on request.
/// </summary>
public sealed record Snapshot(
    long Ts,                 // unix epoch ms
    int IntervalMs,          // nominal sampling interval
    string Hostname,
    long UptimeSec,
    CpuMetrics Cpu,
    MemoryMetrics Mem,
    DiskMetrics Disk,
    NetworkMetrics Net);

public sealed record CpuMetrics(double TotalPct, double[] PerCore, LoadAvg Load);

public sealed record LoadAvg(double One, double Five, double Fifteen);

public sealed record MemoryMetrics(
    long TotalKb,
    long AvailableKb,
    long UsedKb,
    double UsedPct,
    long SwapTotalKb,
    long SwapUsedKb);

public sealed record DiskMetrics(MountUsage[] Mounts, DiskIo Io);

public sealed record MountUsage(string Mount, string Fs, long TotalBytes, long UsedBytes, double UsedPct);

public sealed record DiskIo(long ReadBps, long WriteBps);

public sealed record NetworkMetrics(InterfaceRate[] Ifaces);

public sealed record InterfaceRate(string Name, long RxBps, long TxBps, long RxPps, long TxPps);
