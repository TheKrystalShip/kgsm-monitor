namespace TheKrystalShip.KGSM.Monitor.Contracts;

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
    SensorReading[] Sensors,  // hwmon temperatures (empty when none/absent — never invented)
    ServerMetrics[] Servers); // per-KGSM-server cgroup metrics (empty when none running)

public sealed record CpuMetrics(double TotalPct, double[] PerCore, LoadAvg Load, CpuInfo? Info);

public sealed record LoadAvg(double One, double Five, double Fifteen);

/// <summary>
/// Static CPU identity from <c>/proc/cpuinfo</c> + <c>/sys/.../cpufreq</c> — read once at
/// startup (it does not change) and carried on every frame. Every field is
/// <c>null</c> when its source can't be read, never guessed.
/// </summary>
/// <param name="Model"><c>model name</c> from cpuinfo (e.g. "AMD Ryzen 7 3800X 8-Core Processor").</param>
/// <param name="Cores">
/// Physical cores — the first socket's <c>cpu cores</c>. On a multi-socket host this
/// reports one socket's core count, not the box total (single-socket simplification;
/// see <c>CpuInfoSource</c>). <see cref="Threads"/> stays correct (it counts every
/// <c>processor</c> line).
/// </param>
/// <param name="Threads">Logical CPUs — the count of <c>processor</c> lines (hardware threads).</param>
/// <param name="MaxFreqGhz">
/// Stable max clock from <c>cpuinfo_max_freq</c> (kHz → GHz), <em>not</em> the jittery
/// instantaneous <c>cpu MHz</c>. <c>null</c> when cpufreq is unavailable.
/// </param>
public sealed record CpuInfo(string? Model, int? Cores, int? Threads, double? MaxFreqGhz);

public sealed record MemoryMetrics(
    long TotalKb,
    long AvailableKb,
    long UsedKb,
    double UsedPct,
    long SwapTotalKb,
    long SwapUsedKb,
    long CachedKb,    // /proc/meminfo Cached (verbatim — SReclaimable is NOT folded in)
    long BuffersKb);  // /proc/meminfo Buffers

public sealed record DiskMetrics(MountUsage[] Mounts, DiskIo Io);

/// <param name="Mount">Mount point (e.g. <c>/</c>, <c>/boot</c>).</param>
/// <param name="Fs">Filesystem type (e.g. <c>ext4</c>, <c>vfat</c>).</param>
/// <param name="TotalBytes">Total capacity in bytes.</param>
/// <param name="UsedBytes">Used capacity in bytes (total − free).</param>
/// <param name="UsedPct">Used percentage (0–100, one decimal).</param>
/// <param name="Device">
/// The backing disk's <c>model</c> string (e.g. "Samsung SSD 990 EVO Plus 1TB"), resolved
/// via <c>/proc/self/mountinfo</c> → <c>/dev</c> node → whole-disk → <c>/sys/block/&lt;disk&gt;/device/model</c>.
/// <em>This is the device model, not the <c>/dev</c> path.</em> <c>null</c> when the chain
/// can't be resolved (e.g. LVM/device-mapper mounts with no <c>/sys/block</c> model). Static per mount.
/// </param>
public sealed record MountUsage(string Mount, string Fs, long TotalBytes, long UsedBytes, double UsedPct, string? Device);

public sealed record DiskIo(long ReadBps, long WriteBps);

public sealed record NetworkMetrics(InterfaceRate[] Ifaces);

/// <param name="Name">Interface name (e.g. <c>enp4s0</c>). Loopback and denied prefixes are excluded.</param>
/// <param name="RxBps">Receive throughput, bytes/sec (delta against the previous sample).</param>
/// <param name="TxBps">Transmit throughput, bytes/sec.</param>
/// <param name="RxPps">Receive rate, packets/sec.</param>
/// <param name="TxPps">Transmit rate, packets/sec.</param>
/// <param name="Mac">Interface hardware address from <c>/sys/class/net/&lt;if&gt;/address</c>; <c>null</c> when unreadable.</param>
/// <param name="Errors">
/// Total link errors = <c>statistics/rx_errors</c> + <c>tx_errors</c>; <c>null</c> only when
/// neither file reads (never a fabricated 0).
/// </param>
public sealed record InterfaceRate(string Name, long RxBps, long TxBps, long RxPps, long TxPps, string? Mac, long? Errors);

/// <summary>
/// One hwmon temperature reading: a chip's <c>tempN_input</c>, in °C. Sourced from
/// <c>/sys/class/hwmon/hwmon*/</c>. The array is empty (never invented) when no hwmon
/// chip exposes a temperature.
/// </summary>
/// <param name="Chip">The hwmon <c>name</c> (e.g. "k10temp", "nvme"). Not unique — two chips can share a name.</param>
/// <param name="Label">The <c>tempN_label</c> if present (e.g. "Tctl", "Composite"); <c>null</c> when the chip has no label file.</param>
/// <param name="ValueC">Temperature in °C (the raw <c>tempN_input</c> milli-°C divided by 1000).</param>
public sealed record SensorReading(string Chip, string? Label, double ValueC);

/// <summary>
/// Per-game-server resource usage. For <c>systemd</c>/<c>container</c> servers this comes
/// from cgroup v2 counters; for <c>native</c> (standalone, no cgroup) servers it is summed
/// from the <c>/proc</c> process tree rooted at the instance <c>.pid</c> (Slice 3).
/// </summary>
/// <param name="Id">Stable instance name (KGSM instance identifier).</param>
/// <param name="Name">Display name (same as <paramref name="Id"/> today; kept distinct for future blueprint/alias labels).</param>
/// <param name="Kind">How the server was measured: <c>systemd</c> | <c>container</c> (cgroup) | <c>native</c> (<c>/proc</c> tree).</param>
/// <param name="CpuPctCore">
/// CPU usage as a percentage of <em>one</em> core (htop per-process convention) — a
/// multi-core server can exceed 100. Deliberately <em>not</em> the same unit as host
/// <see cref="CpuMetrics.TotalPct"/> (0–100 across all cores); the SPA normalises by
/// core count if it wants a host-relative figure.
/// </param>
/// <param name="MemBytes">
/// cgroup kinds: <c>memory.current</c> (total charged memory, incl. reclaimable page cache,
/// so higher than RSS). <c>native</c>: summed process RSS (double-counts shared pages, an
/// upper bound). Both honest, neither a plain <c>ps</c> RSS — see PLAN.md caveat.
/// </param>
/// <param name="IoReadBps">Block-IO read rate (bytes/sec). <c>null</c> for cgroup kinds when
/// the io controller isn't accounted (<c>io.stat</c> absent — needs <c>IOAccounting=yes</c>);
/// <c>native</c> reads <c>/proc/[pid]/io</c> as root so it reports a number, never null.</param>
/// <param name="IoWriteBps">Block-IO write rate, or null (see <paramref name="IoReadBps"/>).</param>
/// <param name="Pids">Live process/thread count (<c>pids.current</c>).</param>
/// <param name="DiskBytes">
/// On-disk footprint: the apparent total size (sum of file lengths) of the instance's
/// working directory — install + saves + backups + logs + temp. Unlike the cgroup
/// counters above this is a <em>filesystem</em> figure cgroups don't expose, so it is
/// sampled on a slow, separate cadence configured by the daemon (a directory walk, not the
/// 1&#160;Hz tick) and conflated like the rest of the frame.
/// Symlinks are not followed (no double-count). <c>null</c> when not yet walked or the
/// directory can't be read — never a fabricated 0. Only attached to running servers
/// (the frame lists running servers only), so a stopped server's footprint is absent.
/// </param>
/// <param name="RxBps">
/// Per-server network <em>receive</em> throughput in bytes/sec, measured by a passive eBPF
/// <c>cgroup/skb</c> byte counter attached once to the KGSM parent cgroup (<c>kgsm.slice</c>)
/// and read from a pinned BPF map keyed by cgroup id (see <c>NetworkCgroupSource</c>). A rate,
/// like the I/O counters, so it needs two samples. <c>null</c> — never a fabricated 0 — when the
/// meter isn't measuring this server: the eBPF meter isn't set up (pin missing / cap not granted),
/// or the server's cgroup is outside <c>kgsm.slice</c> (a <c>systemd</c> or <c>container</c> server,
/// or a <c>native</c> server with no live cgroup) so the counter never sees its packets, or no
/// traffic has been attributed to its cgroup yet. Same honest nullable contract as
/// <paramref name="IoReadBps"/>.
/// </param>
/// <param name="TxBps">Per-server network <em>transmit</em> throughput, bytes/sec, or null (see <paramref name="RxBps"/>).</param>
public sealed record ServerMetrics(
    string Id,
    string Name,
    string Kind,
    double CpuPctCore,
    long MemBytes,
    long? IoReadBps,
    long? IoWriteBps,
    int Pids,
    long? DiskBytes,
    long? RxBps,
    long? TxBps);
