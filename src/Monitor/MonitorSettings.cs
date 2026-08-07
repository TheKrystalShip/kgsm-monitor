using TheKrystalShip.KGSM.LeafConfig;

namespace TheKrystalShip.KGSM.Monitor;

/// <summary>
/// The daemon's configurable surface, shaped 1:1 with the <c>"Monitor"</c> section of
/// <c>kgsm-monitor.settings.json</c>. That file is the source of truth: every knob is declared
/// there with its default, and an environment variable may only override a key that exists in it
/// (<c>Monitor__IntervalMs</c>, <c>Monitor__HistoryDbPath</c>, …). A variable naming a key this
/// class does not declare sets nothing, which is what stops a stale override from looking applied.
/// </summary>
/// <remarks>
/// This type is bound, not interpreted — the values are exactly what was configured, including
/// ones the daemon cannot use. <see cref="MonitorOptions.FromSettings"/> is where clamping,
/// octal parsing and the machine-name fallback happen, so the raw configuration and the runtime
/// view stay separable. Binding is source-generated (the binder generator is on under
/// <c>PublishAot</c>), so this stays reflection-free.
/// <para>
/// Every number and flag is <b>nullable</b>, and null means "not written" — the coded default in
/// <see cref="MonitorOptions"/> applies. Two binder behaviours make this load-bearing rather than
/// stylistic: a blank value (<c>Monitor__IntervalMs=</c>, a single stray line in an env file)
/// binds to a non-nullable <see cref="int"/> by throwing, taking the daemon down at startup; and a
/// JSON null binds to <c>0</c>/<c>false</c>, silently discarding the default a property initializer
/// here would have carried. Nullable turns both into "unset". A value that is present but is not a
/// number still fails loudly, which is the point of typing it at all.
/// </para>
/// <para>
/// The <c>[LeafField]</c> attributes and <c>&lt;panel&gt;</c> tags are what
/// <c>deploy/kgsm-monitor.leaf.json</c> is generated from. Each carries only what cannot be derived:
/// the environment variable comes from the property's name under <see cref="Section"/>, and the
/// default from the settings file. A knob is therefore added in two places — here and in that file —
/// and the Control Panel picks it up with nothing further to write.
/// </para>
/// </remarks>
[LeafSection(Section)]
public sealed class MonitorSettings
{
    /// <summary>The configuration section this binds from.</summary>
    public const string Section = "Monitor";

    /// <summary>
    /// The lowest value each cadence is allowed to take. Declared once and read by both
    /// <see cref="MonitorOptions.FromSettings"/>, which raises anything lower, and the leaf
    /// descriptor, which is what the Control Panel rejects against before it restarts the daemon —
    /// so the panel can never accept a value the daemon would silently move.
    /// </summary>
    public static class Floors
    {
        public const int IntervalMs = 100;
        public const int ServerResyncMs = 1000;
        public const int DiskUsageMs = 5000;
        public const int LeafResolveMs = 1000;
        public const int PersistMs = 1000;
        public const int MaintenanceMs = 1000;

        /// <summary>Retention is in whole units, and zero would mean "keep nothing".</summary>
        public const int Retention = 1;
    }

    /// <summary>Sampling cadence in milliseconds. Floor 100 — a lower value is raised to it.</summary>
    /// <panel>How often the monitor samples host and per-server metrics.</panel>
    [LeafField("intervalMs", "Sample interval", Group = "sampling", Min = Floors.IntervalMs, Unit = "ms")]
    public int? IntervalMs { get; set; }

    /// <summary>Unix domain socket to listen on. Lives inside the per-service runtime dir
    /// (systemd <c>RuntimeDirectory=kgsm-monitor</c>) so a co-located API connects to the same
    /// default.</summary>
    /// <panel>Unix socket the monitor serves metrics on. This is the socket the API scrapes.</panel>
    [LeafField("socketPath", "Metrics socket", Group = "sockets", Type = LeafType.Path,
        Risk = LeafRisk.Wiring, PairedApiKey = "Api__MonitorSocketPath")]
    public string SocketPath { get; set; } = "/run/kgsm-monitor/metrics.sock";

    /// <summary>Permission bits applied to the socket once it exists, as octal digits
    /// (e.g. <c>"660"</c> — owner+group read/write, so an API process in the socket's group can
    /// scrape it without exposing it world-wide). Malformed input keeps the default.</summary>
    /// <panel>Octal permission bits applied to the metrics socket. The default lets a process in the
    /// socket's group scrape it without exposing it host-wide.</panel>
    [LeafField("socketMode", "Metrics socket permissions", Group = "sockets", Risk = LeafRisk.Wiring)]
    public string SocketMode { get; set; } = "660";

    /// <summary>Extra filesystem types to hide from the mount list, on top of the always-filtered
    /// pseudo filesystems. Comma-separated.</summary>
    /// <panel>Extra filesystem types to leave out of the mount list, on top of the pseudo filesystems
    /// that are always filtered.</panel>
    /// <remarks>
    /// A joined string rather than a JSON array because the Control Panel declares this field
    /// <c>csv</c> and writes one variable holding the joined value. An array would need indexed
    /// keys (<c>…__0</c>, <c>…__1</c>) that nothing on the writing side produces.
    /// </remarks>
    [LeafField("mountFsDeny", "Hidden filesystem types", Group = "sampling", Type = LeafType.Csv)]
    public string MountFsDeny { get; set; } = string.Empty;

    /// <summary>Interface-name prefixes to exclude from host network rates, in addition to the
    /// always-excluded loopback. Comma-separated. <c>veth</c> by default: virtual-ethernet pairs
    /// are per-container noise that double-counts container traffic in the host aggregate.</summary>
    /// <panel>Interface-name prefixes left out of host network rates, on top of the always-excluded
    /// loopback. Virtual-ethernet pairs would otherwise double-count container traffic.</panel>
    [LeafField("ifaceDeny", "Excluded interface prefixes", Group = "sampling", Type = LeafType.Csv)]
    public string IfaceDenyPrefixes { get; set; } = "veth";

    /// <summary>Path to the KGSM executable. Empty (the default) disables per-server sampling and
    /// runs the monitor host-only, so the daemon is useful where KGSM is absent.</summary>
    /// <panel>Path to the KGSM executable. Empty turns per-server sampling off entirely and the
    /// monitor reports host metrics only.</panel>
    [LeafField("kgsmPath", "KGSM executable", Group = "servers", Type = LeafType.Path, Risk = LeafRisk.Wiring)]
    public string KgsmPath { get; set; } = string.Empty;

    /// <summary>How often to re-list KGSM instances (the source-of-truth resync), in milliseconds.
    /// Floor 1000. This shells out to KGSM — a process spawn, the very cost the metrics path
    /// avoids — so it runs on its own slow cadence, off the metrics tick.</summary>
    /// <panel>How often the monitor re-lists KGSM instances to refresh its watch list. This spawns a
    /// process, so it runs well off the metrics tick.</panel>
    [LeafField("resyncMs", "Instance resync interval", Group = "servers",
        Min = Floors.ServerResyncMs, Unit = "ms", DependsOn = "kgsmPath")]
    public int? ServerResyncMs { get; set; }

    /// <summary>How often to recompute each server's on-disk footprint (a recursive directory
    /// walk), in milliseconds. Floor 5000. This stats every file under the tree, far heavier than
    /// the cgroup reads, so it runs on its own slow cadence.</summary>
    /// <panel>How often each server's on-disk size is recomputed. This walks the whole instance
    /// directory, so it runs on its own slow cadence.</panel>
    [LeafField("diskUsageMs", "Disk footprint interval", Group = "servers",
        Min = Floors.DiskUsageMs, Unit = "ms", DependsOn = "kgsmPath")]
    public int? DiskUsageMs { get; set; }

    /// <summary>Whether to read KGSM lifecycle events from the journal for low-latency watch-list
    /// deltas. With this off, per-server metrics still work — the periodic resync floor remains the
    /// source of truth — but engine event history stops being recorded.</summary>
    /// <panel>Tail the engine event journal for sub-second watch-list updates. With this off,
    /// per-server metrics still work — the periodic resync remains the source of truth — but engine
    /// event history stops being recorded.</panel>
    [LeafField("eventsEnabled", "Read engine events", Group = "servers", DependsOn = "kgsmPath")]
    public bool? EventsEnabled { get; set; }

    /// <summary>Directory holding KGSM's append-only event journal, which the monitor tails.
    /// Read-only: the engine is the sole writer and any number of consumers read the same files,
    /// so nothing here is owned by or reserved for the monitor.</summary>
    /// <panel>Directory holding KGSM's append-only event journal, which the monitor tails for engine
    /// events. Read-only: the engine is the sole writer, and any number of consumers read the same
    /// files with no coordination.</panel>
    [LeafField("kgsmJournalDir", "Engine event journal", Group = "servers", Type = LeafType.Path,
        Risk = LeafRisk.Wiring, DependsOn = "kgsmPath")]
    public string KgsmJournalDir { get; set; } = "/var/lib/kgsm/events";

    /// <summary>Turns off sampling of the ecosystem's own leaf daemons, leaving the frame's leaf array
    /// empty. On by default: it needs no privilege, no KGSM and no other leaf.</summary>
    /// <panel>Turn off resource sampling of the KGSM leaves themselves. The Control Panel's per-leaf
    /// resource charts then have no data to draw; host and game-server metrics are unaffected.</panel>
    [LeafField("leafMetricsDisabled", "Disable leaf metrics", Group = "leaves")]
    public bool? LeafMetricsDisabled { get; set; }

    /// <summary>Directory holding the leaf config descriptors, which is where the leaf watch-list comes
    /// from — each declares one leaf's id and unit. Read-only and shared: every leaf's deploy installs its
    /// own file here and kgsm-api scans the same directory.</summary>
    /// <panel>Directory the monitor reads to learn which leaves exist. Every leaf installs its config
    /// descriptor here on deploy, so a leaf added later is measured with no change to this daemon.</panel>
    [LeafField("leafDescriptorDir", "Leaf descriptor directory", Group = "leaves", Type = LeafType.Path,
        Risk = LeafRisk.Wiring, PairedApiKey = "Api__LeafDescriptorDir", DependsOn = "leafMetricsDisabled")]
    public string LeafDescriptorDir { get; set; } = "/var/lib/kgsm/leaves";

    /// <summary>How often to re-resolve which leaves are running and which cgroup each one's main process
    /// lives in, ms. Floor 1000. This spawns systemctl, so it stays off the metrics tick; a cgroup that
    /// disappears also nudges an immediate re-resolve.</summary>
    /// <panel>How often the monitor re-checks which leaves are running. This spawns a process, so it runs
    /// well off the metrics tick — a leaf that restarts is picked up immediately regardless.</panel>
    [LeafField("leafResolveMs", "Leaf resolve interval", Group = "leaves",
        Min = Floors.LeafResolveMs, Unit = "ms", DependsOn = "leafMetricsDisabled")]
    public int? LeafResolveMs { get; set; }

    /// <summary>The <c>systemctl</c> binary used to read each leaf unit's main pid — the one thing about a
    /// leaf only the service manager knows. Resolved via <c>PATH</c> by default.</summary>
    /// <panel>The systemctl binary used to look up each leaf's main process. Set an absolute path where
    /// PATH can't be relied on.</panel>
    [LeafField("systemctlPath", "systemctl binary", Group = "leaves", Type = LeafType.Path,
        DependsOn = "leafMetricsDisabled")]
    public string SystemctlPath { get; set; } = "systemctl";

    /// <summary>Identity this host persists its <c>host</c>-kind metrics under. Empty (the default)
    /// resolves to the machine name — the same default kgsm-api uses for its own host id, so the
    /// api's history queries line up with the rows the monitor stored.</summary>
    /// <panel>Identity this host stores its own metrics under. Defaults to the machine's hostname; it
    /// must match the API's host id or history queries return nothing.</panel>
    [LeafField("hostId", "Host id", Group = "general", Risk = LeafRisk.Wiring,
        PairedApiKey = "Api__HostId", NoDefault = true)]
    public string HostId { get; set; } = string.Empty;

    /// <summary>Turns off metrics-history persistence and the <c>/metrics/history</c> endpoint,
    /// leaving the monitor live-only.</summary>
    /// <panel>Turn off metrics persistence and the history endpoint. The monitor then serves live
    /// frames only, and the Control Panel's metric charts have no data to draw.</panel>
    [LeafField("historyDisabled", "Disable metrics history", Group = "history")]
    public bool? HistoryDisabled { get; set; }

    /// <summary>SQLite file for the metrics history store. Defaults under the systemd
    /// <c>StateDirectory</c> (persistent), not the tmpfs runtime dir where the socket lives.</summary>
    /// <panel>SQLite file holding metrics history. Repointing it starts an empty store and orphans the
    /// existing history.</panel>
    [LeafField("dbPath", "Metrics database", Group = "history", Type = LeafType.Path,
        Risk = LeafRisk.Destructive)]
    public string HistoryDbPath { get; set; } = "/var/lib/kgsm-monitor/metrics.db";

    /// <summary>How often the persist loop flushes the latest frame to history, ms. Floor 1000,
    /// decoupled from the sample tick.</summary>
    /// <panel>How often the latest sampled frame is written to history.</panel>
    [LeafField("persistMs", "Persist interval", Group = "history", Min = Floors.PersistMs, Unit = "ms")]
    public int? PersistMs { get; set; }

    /// <summary>Raw-tier retention, hours. Floor 1. Also the tier-select boundary: a query range
    /// at or under this reads raw, above it reads rollup.</summary>
    /// <panel>How long full-resolution samples are kept. Also the tier boundary: a query inside this
    /// window reads raw samples, one beyond it reads rollups. Lowering it prunes existing rows.</panel>
    [LeafField("rawRetentionHours", "Raw retention", Group = "history", Min = Floors.Retention,
        Unit = "hours", Risk = LeafRisk.Destructive)]
    public int? RawRetentionHours { get; set; }

    /// <summary>Rollup bucket width, minutes. Floor 1.</summary>
    /// <panel>Width of each aggregated history bucket beyond the raw window.</panel>
    [LeafField("rollupStepMin", "Rollup bucket width", Group = "history", Min = Floors.Retention, Unit = "min")]
    public int? RollupStepMin { get; set; }

    /// <summary>Rollup-tier retention, days. Floor 1.</summary>
    /// <panel>How long aggregated history is kept. Lowering it prunes existing rows.</panel>
    [LeafField("rollupRetentionDays", "Rollup retention", Group = "history", Min = Floors.Retention,
        Unit = "days", Risk = LeafRisk.Destructive)]
    public int? RollupRetentionDays { get; set; }

    /// <summary>How often maintenance (rollup + prune + vacuum) runs, ms. Floor 1000.</summary>
    /// <panel>How often rollup, pruning and vacuuming run over the history store.</panel>
    [LeafField("maintenanceMs", "Maintenance interval", Group = "history",
        Min = Floors.MaintenanceMs, Unit = "ms")]
    public int? MaintenanceMs { get; set; }
}
