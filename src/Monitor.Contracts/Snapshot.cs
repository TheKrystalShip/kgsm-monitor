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
    ServerMetrics[] Servers,  // per-KGSM-server cgroup metrics (empty when none running)
    LeafMetrics[] Leaves,     // per-KGSM-leaf cgroup metrics (empty when off/none running)
    ConditionReading[] Conditions);  // threshold conditions currently breaching (empty when none/off)

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

/// <summary>
/// Per-KGSM-leaf resource usage, read from the cgroup v2 counters of the systemd unit each leaf runs as.
/// The array holds only leaves that are <em>running and resolvable</em>: a socket-activated leaf sitting
/// idle has no cgroup and is simply absent, never a row of zeros.
/// <para>
/// <b>The cgroup sampled is the one the leaf's main process lives in, not its unit's.</b> cgroup v2
/// counters are recursive, so a leaf that supervises other workloads in child cgroups would otherwise
/// report theirs as its own — <c>kgsm-watchdog</c> runs itself in a <c>supervisor</c> child of its unit
/// cgroup and spawns each game server into a sibling, and its unit-level memory is dominated by the
/// servers. Descendants of the resolved cgroup are still counted, which is the boundary that matters:
/// work a leaf forks into sub-cgroups is its own, work supervised beside it is not. For every leaf whose
/// main process sits directly in its unit cgroup this is identical to the unit-level figure.
/// </para>
/// <para>
/// <b>No network and no disk footprint here, deliberately.</b> The eBPF <c>cgroup/skb</c> meter is
/// attached to <c>kgsm.slice</c>, so it never sees a leaf in <c>system.slice</c>; and a leaf's on-disk
/// size is its install prefix, which is static and not worth a recurring walk. Absent beats invented.
/// </para>
/// </summary>
/// <param name="Id">The leaf id from its config descriptor (<c>monitor</c>, <c>watchdog</c>, …) — the same
/// identity kgsm-api and the Control Panel address it by.</param>
/// <param name="Unit">The systemd unit the leaf runs as, carried so a consumer can name what was measured
/// without re-deriving it.</param>
/// <param name="CpuPctCore">CPU as a percentage of <em>one</em> core, the same unit and htop convention as
/// <see cref="ServerMetrics.CpuPctCore"/> — a multi-threaded leaf can exceed 100.</param>
/// <param name="MemBytes"><c>memory.current</c> for the resolved cgroup. Includes reclaimable page cache
/// like every cgroup memory figure, so it sits above the process's RSS.</param>
/// <param name="IoReadBps">Block-IO read rate (bytes/sec), or <c>null</c> when <c>io.stat</c> is absent
/// (the io controller isn't accounted for this cgroup) — never a fabricated 0.</param>
/// <param name="IoWriteBps">Block-IO write rate, or null (see <paramref name="IoReadBps"/>).</param>
/// <param name="Pids">Live process/thread count (<c>pids.current</c>).</param>
public sealed record LeafMetrics(
    string Id,
    string Unit,
    double CpuPctCore,
    long MemBytes,
    long? IoReadBps,
    long? IoWriteBps,
    int Pids);

/// <summary>
/// One threshold rule's verdict about one target: this metric is over its line, and has been for long
/// enough to say so. Decided by the daemon at the <em>sample</em> cadence against every reading it took,
/// which is what lets it claim a sustained breach at all — a consumer scraping this frame every few
/// seconds sees a decision, not a sample it must judge for itself.
/// <para>
/// <b>The array lists breaching conditions only.</b> A condition that clears is simply absent from the
/// next frame; there is no cleared/resolved state on the wire. A consumer mirroring these into its own
/// surface resolves on absence, the same way it would for a server that stopped reporting.
/// </para>
/// <para>
/// <b>Deliberately free of any consumer's vocabulary.</b> No severity names beyond the two threshold
/// bands, no display strings, no deep links, no ids belonging to somebody else's feed. This says what
/// the kernel counters did against a configured line, and nothing about what anyone should render.
/// </para>
/// </summary>
/// <param name="EpisodeId">Stable identity for one continuous breach: <c>&lt;ruleKey&gt;:&lt;ref-or-serverId
/// -or-empty&gt;:&lt;openedAtMs&gt;</c>. Constant for as long as the breach lasts and never reused, so a
/// consumer can tell "still the same problem" from "it cleared and came back" without diffing values.</param>
/// <param name="RuleKey">The rule that fired, e.g. <c>host-temp</c>. Stable across restarts and edits;
/// it is what an operator recognises the rule by.</param>
/// <param name="Metric">Which measurement the rule watches, as the daemon's own metric name (e.g.
/// <c>HostTempC</c>). A string rather than a shared enum: the set of measurable fields is the daemon's to
/// grow, and a consumer that meets an unknown one should carry it, not fail to parse the frame.</param>
/// <param name="Scope"><c>host</c> or <c>server</c> — whether this is about the machine or about one game
/// server. Derived from the metric, so a consumer never re-derives it.</param>
/// <param name="Ref">Which one of several like targets, for a metric that fans out: the mount path for
/// disk, the chip/label for a sensor. <c>null</c> for a metric with a single target.</param>
/// <param name="ServerId">The instance this is about, for a <c>server</c>-scope condition; <c>null</c> for
/// a host-scope one.</param>
/// <param name="Band"><c>warn</c> or <c>danger</c> — which of the rule's two lines the value is over.
/// A condition that worsens stays the same episode and changes band.</param>
/// <param name="Value">The reading at this frame's timestamp, in the metric's own unit.</param>
/// <param name="WindowMax">The highest reading seen since the breach opened — what actually justifies the
/// alarm, as opposed to whatever the value happened to be when the frame was built. For a scraper polling
/// slower than the sample rate these differ, and this is the honest one.</param>
/// <param name="Threshold">The line <paramref name="Band"/> was crossed at, carried so a consumer can say
/// how far over the value is without holding a copy of the policy.</param>
/// <param name="Since">Unix epoch ms when the breach opened — the first reading over the line that went on
/// to satisfy the rule's dwell, not the moment the dwell completed. So "how long has this been wrong" is
/// answered from when it started being wrong.</param>
public sealed record ConditionReading(
    string EpisodeId,
    string RuleKey,
    string Metric,
    string Scope,
    string? Ref,
    string? ServerId,
    string Band,
    double Value,
    double WindowMax,
    double Threshold,
    long Since);
