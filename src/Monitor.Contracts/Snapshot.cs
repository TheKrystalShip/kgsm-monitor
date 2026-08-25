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
    ConditionReading[] Conditions,   // threshold conditions currently breaching (empty when none/off)
    ServerDiskUsage[]? ServerDisks = null,   // on-disk footprint per WATCHED instance, running or not
    FanReading[]? Fans = null,       // hwmon fan tachometers reading above zero (empty when none spin)
    GpuMetrics? Gpu = null,          // GPU devices + compute contexts (null when the host has none)
    SliceMetrics? Slice = null);     // the KGSM parent cgroup's aggregate (null when kgsm.slice is absent)

/// <summary>
/// The aggregate of everything under the KGSM parent cgroup (<c>kgsm.slice</c>) — the game servers'
/// collective share of the host, measured at the slice itself rather than summed from per-server rows.
/// The slice's own counters are recursive over every descendant, so this also covers work a per-server
/// row can miss (an instance mid-teardown, a cgroup the resolver hasn't re-found yet), which is what
/// makes "game servers vs the rest of the host" an honest split.
/// </summary>
/// <remarks>
/// Null when <c>kgsm.slice</c> does not exist — a host with no watchdog, or one where nothing native has
/// ever started. That is an ordinary state, not a fault. <see cref="CpuPctCore"/> follows the per-server
/// convention (percent of <em>one</em> core, may exceed 100) and is null on the first observation, when
/// there is no delta to rate against; each other field is null when its counter file could not be read —
/// never a substituted zero.
/// </remarks>
public sealed record SliceMetrics(double? CpuPctCore, long? MemBytes, int? Pids);

/// <summary>
/// The GPU devices on this host and every compute context running on them. Pure measurement — which
/// leaf a context is attributed to is decided separately, in <see cref="LeafGpu"/>.
/// </summary>
/// <remarks>
/// Null on a host with no card, no driver, or no <c>libnvidia-ml.so.1</c>. The library is opened lazily
/// and its absence is an ordinary state rather than a fault: a host that never had a GPU reports one
/// less thing, not an error.
/// </remarks>
public sealed record GpuMetrics(GpuDevice[] Devices, GpuProcess[] Processes);

/// <summary>One GPU, and what it currently holds.</summary>
/// <remarks>
/// ⚠ Device memory is <b>never summed across devices</b> by any consumer. VRAM does not pool — a total
/// would imply a model could use it, and a model that does not fit on one card simply fails to load.
/// </remarks>
/// <param name="Index">The NVML device index. Stable only within one boot; join on <paramref name="Uuid"/>.</param>
/// <param name="Name">Marketing name, e.g. "NVIDIA GeForce RTX 3060".</param>
/// <param name="Uuid">Immutable per-card identifier — the key history rows and threshold episodes address a device by.</param>
/// <param name="MemTotalBytes">Total device memory.</param>
/// <param name="MemUsedBytes">Device memory in use, the card's own figure — not the sum of <see cref="GpuProcess"/> rows.</param>
/// <param name="SmPct">Device-wide compute utilisation, or null when unreadable.</param>
/// <param name="TempC">Core temperature in °C, or null when unreadable.</param>
/// <param name="PowerW">Current draw in watts, or null when unreadable.</param>
/// <param name="PowerCapW">Enforced power limit in watts, or null when unreadable.</param>
public sealed record GpuDevice(
    int Index,
    string Name,
    string Uuid,
    long MemTotalBytes,
    long MemUsedBytes,
    double? SmPct,
    double? TempC,
    double? PowerW,
    double? PowerCapW);

/// <summary>
/// One compute context on a device, resolved to the systemd unit that owns it.
/// </summary>
/// <remarks>
/// <b>This names processes that have nothing to do with KGSM.</b> Anything on the host using the card
/// for compute appears here, so a consumer serving untrusted or lower-privileged readers projects this
/// down — naming only contexts that resolve to a known unit and aggregating the rest into an unnamed
/// row — rather than passing it through. The aggregate must keep its memory figure: dropping the rows
/// instead of folding them would leave the per-process figures failing to sum to the device's.
/// </remarks>
/// <param name="DeviceIndex">Which <see cref="GpuDevice"/> this context runs on.</param>
/// <param name="Pid">The owning process.</param>
/// <param name="ProcessName">The executable's name, as the driver reports it.</param>
/// <param name="Unit">The systemd unit from <c>/proc/&lt;pid&gt;/cgroup</c>, or null when it resolves to none.</param>
/// <param name="MemBytes">Device memory held by this context.</param>
/// <param name="SmPct">
/// Compute utilisation, or <c>null</c> when this process was not sampled in the lookback window.
/// Absence is idleness, never a measured zero.
/// </param>
public sealed record GpuProcess(
    int DeviceIndex,
    int Pid,
    string ProcessName,
    string? Unit,
    long MemBytes,
    double? SmPct);

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
/// One hwmon temperature reading: a chip's <c>tempN_input</c> in °C, carrying whatever semantic
/// identity the daemon could establish for it. Sourced from <c>/sys/class/hwmon/hwmon*/</c>. The
/// array is empty (never invented) when no hwmon chip exposes a temperature.
/// </summary>
/// <param name="Id">
/// Stable identity for the channel: <c>chip/device/tempN</c>, where <c>device</c> is the basename the
/// chip's <c>device</c> symlink resolves to (a PCI address, an i2c address, a platform device). Unique
/// where <see cref="Chip"/> is not — two DDR4 DIMMs both named <c>jc42</c> separate as <c>0-0018</c>
/// and <c>0-0019</c> — and stable across a reboot, which the hwmon index is not: the same chip lands on
/// a different <c>hwmonN</c> depending on the order its bus binds.
/// </param>
/// <param name="Chip">The hwmon <c>name</c> (e.g. "k10temp", "nvme"). Not unique — two chips can share a name.</param>
/// <param name="Label">The <c>tempN_label</c> if present (e.g. "Tctl", "Composite"); <c>null</c> when the chip has no label file.</param>
/// <param name="ValueC">Temperature in °C (the raw <c>tempN_input</c> milli-°C divided by 1000).</param>
/// <param name="Role">
/// What the channel measures, as a coarse class a surface can group, sort and icon by: <c>cpu</c>,
/// <c>gpu</c>, <c>memory</c>, <c>drive</c>, <c>board</c>, <c>network</c>. <c>null</c> when the daemon
/// holds no entry for the chip — the reading is real and still reported, merely unclassified.
/// </param>
/// <param name="Name">
/// A human name for the channel ("CPU temperature", "Memory module 1"). <c>null</c> exactly when
/// <see cref="Role"/> is null, which is a surface's signal to fall back to chip/label.
/// </param>
public sealed record SensorReading(
    string Id,
    string Chip,
    string? Label,
    double ValueC,
    string? Role = null,
    string? Name = null);

/// <summary>
/// One hwmon fan tachometer, in RPM, from a chip's <c>fanN_input</c>.
/// </summary>
/// <remarks>
/// <para><b>Separate from <see cref="SensorReading"/> on purpose.</b> That record's <see cref="SensorReading.ValueC"/>
/// is a °C contract which the <c>HostTempC</c> threshold fans out over; an RPM carried in the same array
/// would be reconciled against a temperature line.</para>
/// <para><b>A tachometer reading zero is omitted.</b> A header with no fan plugged into it and a fan that has
/// stopped both read 0, and hwmon offers nothing to tell them apart. Boards publish far more headers than
/// anyone populates, so reporting every zero would state that fans exist which physically do not — the more
/// certain error of the two.</para>
/// </remarks>
/// <param name="Id">Stable identity, <c>chip/device/fanN</c> — built like <see cref="SensorReading.Id"/>.</param>
/// <param name="Chip">The hwmon <c>name</c> exposing the tachometer (e.g. "nct6792").</param>
/// <param name="Label">The <c>fanN_label</c> when the chip publishes one; <c>null</c> otherwise.</param>
/// <param name="Rpm">Revolutions per minute, straight from <c>fanN_input</c>.</param>
/// <param name="Name">A human name ("CPU fan"); <c>null</c> when the daemon holds no entry for the chip.</param>
public sealed record FanReading(
    string Id,
    string Chip,
    string? Label,
    int Rpm,
    string? Name = null);

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
/// directory can't be read — never a fabricated 0. This array lists running servers only, so a
/// stopped instance's footprint rides <see cref="Snapshot.ServerDisks"/> instead — the same
/// measurement, from the same cache, published for every watched instance.
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
/// <param name="Memory">
/// The memory counters that describe what this server <em>holds</em> and whether it is <em>short</em>,
/// as opposed to <see cref="MemBytes"/>, which is what it is charged for. <c>null</c> when the kind
/// being sampled exposes none of them. See <see cref="ServerMemory"/>.
/// </param>
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
    long? TxBps,
    ServerMemory? Memory = null);

/// <summary>
/// One server's memory in the four terms that distinguish a workload that is <em>using</em> memory
/// from one that is <em>short</em> of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not <see cref="ServerMetrics.MemBytes"/>.</b> That figure is <c>memory.current</c>,
/// which charges reclaimable page cache — a server streaming saves and map chunks fills whatever
/// allowance it is given, so the number grows toward the ceiling rather than toward what the workload
/// holds. It is the honest thing to chart and the wrong thing to size against. These four are the
/// sizing terms.
/// </para>
/// <para>
/// <b>Every field is nullable and null means not measured.</b> A server sampled from a <c>/proc</c>
/// process tree has no cgroup, so it can report a working set and nothing else; a cgroup without the
/// memory controller reports none of them. Absent beats invented, as everywhere else in this contract.
/// </para>
/// </remarks>
/// <param name="AnonBytes">
/// Anonymous memory — the pages the workload actually holds, with page cache excluded
/// (<c>memory.stat</c> <c>anon</c>; summed <c>RssAnon</c> for a <c>/proc</c> tree). The figure a
/// requirement is reasoned from.
/// </param>
/// <param name="SwapBytes">
/// What has been pushed to swap (<c>memory.swap.current</c>). Part of the working set: a server whose
/// pages moved to disk still holds them, and reading <see cref="AnonBytes"/> alone would report the
/// eviction as a shrinking footprint.
/// </param>
/// <param name="PeakBytes">
/// The kernel's own high-water mark for this cgroup (<c>memory.peak</c>). Immune to sampling gaps —
/// a spike between two ticks is invisible to a sampled series and present here.
/// ⚠ It is scoped to the cgroup, so it resets on every restart: this is a <em>per-run</em> maximum,
/// and accumulating one across runs is the reader's job.
/// </param>
/// <param name="OomKills">
/// How many processes the kernel has killed in this cgroup for want of memory (<c>memory.events</c>
/// <c>oom_kill</c>). Non-zero is not an inference: this server was refused memory it asked for, which
/// establishes a lower bound on what it needs. Resets with the cgroup, like <paramref name="PeakBytes"/>.
/// </param>
/// <param name="MaxEvents">
/// How many times allocation hit the cgroup's <c>memory.max</c> ceiling (<c>memory.events</c>
/// <c>max</c>). Reclaim under a cap that succeeded — the warning that precedes
/// <paramref name="OomKills"/>, and the evidence that a cap is binding even when nothing died.
/// </param>
/// <param name="StallPct">
/// The share of the last 60 seconds in which <em>every</em> task in the cgroup was stalled waiting on
/// memory (PSI <c>memory.pressure</c>, <c>full avg60</c>). This is the difference between a server
/// using a lot of memory and one that is short of it: a large working set with a zero here is a server
/// that is comfortable.
/// </param>
/// <param name="StallTotalUsec">
/// Cumulative full-stall time in microseconds (PSI <c>total</c>), for a reader that wants stall over a
/// window of its own choosing rather than the kernel's fixed averages. Resets with the cgroup.
/// </param>
public sealed record ServerMemory(
    long? AnonBytes,
    long? SwapBytes,
    long? PeakBytes,
    long? OomKills,
    long? MaxEvents,
    double? StallPct,
    long? StallTotalUsec);

/// <summary>
/// One watched instance's on-disk footprint, published <em>independently of run state</em>.
/// <para>
/// Every other per-server figure is a runtime reading and exists only while the server runs:
/// <see cref="ServerMetrics"/> is produced from a live cgroup or process tree, so a stopped instance
/// has no row there at all. Disk is the exception — the footprint of an installed instance is a
/// property of its files, measured by the same slow directory walk whether or not anything is
/// running — so it is published here for the whole watch-list rather than being lost with the row
/// that never existed. A surface can therefore show what an instance occupies without first asking
/// whether it is up.
/// </para>
/// <para>
/// An instance whose working directory can't be read is <em>absent</em> from the array (the
/// honest "not measured"), never a row of 0. The value is identical to the running row's
/// <see cref="ServerMetrics.DiskBytes"/> — one cache feeds both.
/// </para>
/// </summary>
/// <param name="Id">Stable instance name — the same join key <see cref="ServerMetrics.Id"/> uses.</param>
/// <param name="DiskBytes">Apparent total size (sum of file lengths) of the instance's working
/// directory, symlinks not followed. See <see cref="ServerMetrics.DiskBytes"/> for the full
/// measurement contract.</param>
public sealed record ServerDiskUsage(string Id, long DiskBytes);

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
/// <param name="Gpu">
/// GPU attributed to this leaf, or <c>null</c> when it has no GPU context at all — which is the
/// ordinary case for most leaves and is <em>not</em> the same as a zero. See <see cref="LeafGpu"/>.
/// </param>
public sealed record LeafMetrics(
    string Id,
    string Unit,
    double CpuPctCore,
    long MemBytes,
    long? IoReadBps,
    long? IoWriteBps,
    int Pids,
    LeafGpu? Gpu = null);

/// <summary>
/// GPU attributed to one leaf, kept deliberately apart from that leaf's own cgroup figures.
/// </summary>
/// <remarks>
/// <para>
/// <b>The work is the leaf's; the process spending it may not be.</b> A leaf that drives a model
/// backend — the assistant and its <c>llama-server</c> units — causes GPU cost in a process it neither
/// started nor owns. Both facts have to survive to a reader, so the figures live here rather than being
/// folded into <see cref="LeafMetrics.CpuPctCore"/> and <see cref="LeafMetrics.MemBytes"/>, which stay
/// strictly scoped to the leaf's own cgroup. A surface renders "via kgsm-llama-chat.service", never
/// "this leaf's process is using 8 GiB".
/// </para>
/// <para>
/// <b>There is deliberately no "shared" flag.</b> A backend can have drivers that ship no leaf
/// descriptor and are therefore invisible to any derivation — so a computed boolean would confidently
/// claim sole ownership it cannot verify. <see cref="Units"/> is the honest signal and is always right.
/// </para>
/// </remarks>
/// <param name="Attribution">
/// <c>own</c> when the leaf's own processes hold the GPU contexts, <c>backend</c> when the figures come
/// from units it drives but does not own. Declaration exists only for what discovery cannot see:
/// <c>own</c> falls out of the leaf's cgroup, <c>backend</c> is declared by the leaf's config descriptor.
/// </param>
/// <param name="Units">The units these figures actually came from. Never empty.</param>
/// <param name="MemBytes">Device memory held by those processes, summed. Exact — VRAM has no sampling window.</param>
/// <param name="SmPct">
/// Compute utilisation, summed across those processes, or <c>null</c> when none of them was sampled in
/// the lookback window. Null means <em>idle, not measured</em> — a loaded-but-idle backend reports
/// <see cref="MemBytes"/> with a null here, which a zero could not distinguish from doing no work at all.
/// </param>
public sealed record LeafGpu(
    string Attribution,
    string[] Units,
    long MemBytes,
    double? SmPct);

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
