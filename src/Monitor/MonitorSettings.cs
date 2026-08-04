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
/// </remarks>
public sealed class MonitorSettings
{
    /// <summary>The configuration section this binds from.</summary>
    public const string Section = "Monitor";

    /// <summary>Sampling cadence in milliseconds. Floor 100 — a lower value is raised to it.</summary>
    public int IntervalMs { get; set; } = 1000;

    /// <summary>Unix domain socket to listen on. Lives inside the per-service runtime dir
    /// (systemd <c>RuntimeDirectory=kgsm-monitor</c>) so a co-located API connects to the same
    /// default.</summary>
    public string SocketPath { get; set; } = "/run/kgsm-monitor/metrics.sock";

    /// <summary>Permission bits applied to the socket once it exists, as octal digits
    /// (e.g. <c>"660"</c> — owner+group read/write, so an API process in the socket's group can
    /// scrape it without exposing it world-wide). Malformed input keeps the default.</summary>
    public string SocketMode { get; set; } = "660";

    /// <summary>Extra filesystem types to hide from the mount list, on top of the always-filtered
    /// pseudo filesystems. Comma-separated.</summary>
    /// <remarks>
    /// A joined string rather than a JSON array because the Control Panel declares this field
    /// <c>csv</c> and writes one variable holding the joined value. An array would need indexed
    /// keys (<c>…__0</c>, <c>…__1</c>) that nothing on the writing side produces.
    /// </remarks>
    public string MountFsDeny { get; set; } = string.Empty;

    /// <summary>Interface-name prefixes to exclude from host network rates, in addition to the
    /// always-excluded loopback. Comma-separated. <c>veth</c> by default: virtual-ethernet pairs
    /// are per-container noise that double-counts container traffic in the host aggregate.</summary>
    public string IfaceDenyPrefixes { get; set; } = "veth";

    /// <summary>Path to the KGSM executable. Empty (the default) disables per-server sampling and
    /// runs the monitor host-only, so the daemon is useful where KGSM is absent.</summary>
    public string KgsmPath { get; set; } = string.Empty;

    /// <summary>Directory holding KGSM's append-only event journal, which the monitor tails.
    /// Read-only: the engine is the sole writer and any number of consumers read the same files,
    /// so nothing here is owned by or reserved for the monitor.</summary>
    public string KgsmJournalDir { get; set; } = "/var/lib/kgsm/events";

    /// <summary>How often to re-list KGSM instances (the source-of-truth resync), in milliseconds.
    /// Floor 1000. This shells out to KGSM — a process spawn, the very cost the metrics path
    /// avoids — so it runs on its own slow cadence, off the metrics tick.</summary>
    public int ServerResyncMs { get; set; } = 15_000;

    /// <summary>Whether to read KGSM lifecycle events from the journal for low-latency watch-list
    /// deltas. With this off, per-server metrics still work — the periodic resync floor remains the
    /// source of truth — but engine event history stops being recorded.</summary>
    public bool EventsEnabled { get; set; } = true;

    /// <summary>How often to recompute each server's on-disk footprint (a recursive directory
    /// walk), in milliseconds. Floor 5000. This stats every file under the tree, far heavier than
    /// the cgroup reads, so it runs on its own slow cadence.</summary>
    public int DiskUsageMs { get; set; } = 60_000;

    /// <summary>Identity this host persists its <c>host</c>-kind metrics under. Empty (the default)
    /// resolves to the machine name — the same default kgsm-api uses for its own host id, so the
    /// api's history queries line up with the rows the monitor stored.</summary>
    public string HostId { get; set; } = string.Empty;

    /// <summary>Turns off metrics-history persistence and the <c>/metrics/history</c> endpoint,
    /// leaving the monitor live-only.</summary>
    public bool HistoryDisabled { get; set; }

    /// <summary>SQLite file for the metrics history store. Defaults under the systemd
    /// <c>StateDirectory</c> (persistent), not the tmpfs runtime dir where the socket lives.</summary>
    public string HistoryDbPath { get; set; } = "/var/lib/kgsm-monitor/metrics.db";

    /// <summary>How often the persist loop flushes the latest frame to history, ms. Floor 1000,
    /// decoupled from the sample tick.</summary>
    public int PersistMs { get; set; } = 15_000;

    /// <summary>Raw-tier retention, hours. Floor 1. Also the tier-select boundary: a query range
    /// at or under this reads raw, above it reads rollup.</summary>
    public int RawRetentionHours { get; set; } = 24;

    /// <summary>Rollup bucket width, minutes. Floor 1.</summary>
    public int RollupStepMin { get; set; } = 5;

    /// <summary>Rollup-tier retention, days. Floor 1.</summary>
    public int RollupRetentionDays { get; set; } = 30;

    /// <summary>How often maintenance (rollup + prune + vacuum) runs, ms. Floor 1000.</summary>
    public int MaintenanceMs { get; set; } = 60_000;

    /// <summary>Turns off KGSM engine-event history. Independent of <see cref="HistoryDisabled"/>
    /// (metrics) — either can be toggled without the other.</summary>
    public bool EventHistoryDisabled { get; set; }

    /// <summary>SQLite file for the event-history store. A separate file from
    /// <see cref="HistoryDbPath"/> — its own WAL and writer, no contention with the metrics
    /// flusher.</summary>
    public string EventsDbPath { get; set; } = "/var/lib/kgsm-monitor/events.db";

    /// <summary>Event-history retention, days. Floor 1. No rollup tier (discrete facts, not a
    /// sampled series) — rows simply age out at this cutoff.</summary>
    public int EventRetentionDays { get; set; } = 30;
}
