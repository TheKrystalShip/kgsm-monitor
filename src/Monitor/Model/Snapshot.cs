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
    NetworkMetrics Net,
    ServerMetrics[] Servers); // per-KGSM-server cgroup metrics (empty when none running)

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

/// <summary>
/// Per-game-server resource usage, read from the server's cgroup v2 counters.
/// </summary>
/// <param name="Id">Stable instance name (KGSM instance identifier).</param>
/// <param name="Name">Display name (same as <paramref name="Id"/> today; kept distinct for future blueprint/alias labels).</param>
/// <param name="Kind">Resolution path that located the cgroup: <c>systemd</c> | <c>container</c>.</param>
/// <param name="CpuPctCore">
/// CPU usage as a percentage of <em>one</em> core (htop per-process convention) — a
/// multi-core server can exceed 100. Deliberately <em>not</em> the same unit as host
/// <see cref="CpuMetrics.TotalPct"/> (0–100 across all cores); the SPA normalises by
/// core count if it wants a host-relative figure.
/// </param>
/// <param name="MemBytes">
/// <c>memory.current</c> — total charged memory. Includes reclaimable page cache, so
/// it reads higher than process RSS; honest for v1 (see PLAN.md caveat).
/// </param>
/// <param name="IoReadBps">Block-IO read rate, or null when the io controller is not
/// accounted for this cgroup (<c>io.stat</c> absent — needs <c>IOAccounting=yes</c>).</param>
/// <param name="IoWriteBps">Block-IO write rate, or null (see <paramref name="IoReadBps"/>).</param>
/// <param name="Pids">Live process/thread count (<c>pids.current</c>).</param>
public sealed record ServerMetrics(
    string Id,
    string Name,
    string Kind,
    double CpuPctCore,
    long MemBytes,
    long? IoReadBps,
    long? IoWriteBps,
    int Pids);
