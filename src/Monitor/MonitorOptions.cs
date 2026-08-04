namespace TheKrystalShip.KGSM.Monitor;

/// <summary>
/// The normalized runtime view of <see cref="MonitorSettings"/> — what the daemon actually runs
/// on, after clamping, octal parsing and the machine-name fallback. Configuration is declared in
/// the <c>"Monitor"</c> section of <c>kgsm-monitor.settings.json</c> and overridden per-key by
/// environment variables (<c>Monitor__IntervalMs</c>); this type is the result of that, not a
/// second place to configure anything.
/// </summary>
/// <remarks>
/// Kept separate from the bound settings so the raw configuration stays inspectable: a value the
/// daemon rejected is still visible as what was configured. Binding is source-generated, so the
/// daemon stays Native-AOT clean.
/// </remarks>
public sealed class MonitorOptions
{
    /// <summary>Sampling cadence in milliseconds.</summary>
    public int IntervalMs { get; init; } = 1000;

    /// <summary>Unix domain socket to listen on. Lives inside the
    /// per-service runtime dir (systemd <c>RuntimeDirectory=kgsm-monitor</c>) so the path matches
    /// the deployed unit and a co-located API connects to the same default.</summary>
    public string SocketPath { get; init; } = "/run/kgsm-monitor/metrics.sock";

    /// <summary>
    /// Permission bits applied to the socket once it exists. Default <c>0660</c> — owner+group
    /// read/write, so an API process in the socket's group can scrape it without exposing it
    /// world-wide.
    /// </summary>
    public UnixFileMode SocketMode { get; init; } =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

    /// <summary>
    /// Extra filesystem types to hide from the mount list, on top of the always-filtered
    /// pseudo filesystems. Default empty.
    /// </summary>
    public IReadOnlySet<string> MountFsDeny { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Interface-name prefixes to exclude from host network rates, in addition to the
    /// always-excluded loopback.
    /// Default <c>veth</c>: virtual-ethernet pairs are per-container noise that double-counts
    /// container traffic in the host aggregate. Operators commonly add <c>docker</c>/<c>br-</c>.
    /// </summary>
    public IReadOnlyList<string> IfaceDenyPrefixes { get; init; } = ["veth"];

    /// <summary>
    /// Path to the KGSM executable (<c>kgsm.sh</c>). When
    /// empty (the default) per-server sampling is disabled and the monitor runs host-only,
    /// so the daemon is useful even where KGSM is absent. When set, the monitor periodically
    /// runs <c>instances list --detailed</c> to learn the server watch-list.
    /// </summary>
    public string KgsmPath { get; init; } = string.Empty;

    /// <summary>
    /// Directory holding KGSM's append-only event journal, which the monitor tails for engine
    /// events. Read-only to the monitor — the engine is the
    /// only writer, and any number of consumers read the same files, so nothing here is owned
    /// by or reserved for the monitor. Unrelated to <see cref="SocketPath"/>, which is the
    /// monitor's own outbound metrics socket.
    /// </summary>
    public string KgsmJournalDir { get; init; } = "/var/lib/kgsm/events";

    /// <summary>
    /// How often to re-list KGSM instances (the source-of-truth resync), in milliseconds.
    /// Default 15s, floor 1s. This shells out to KGSM (a
    /// process spawn — the very cost the metrics path avoids), so it runs on its own slow
    /// cadence, off the 1 Hz metrics tick.
    /// </summary>
    public int ServerResyncMs { get; init; } = 15_000;

    /// <summary>
    /// Whether to read KGSM lifecycle events from <see cref="KgsmJournalDir"/> for low-latency
    /// watch-list deltas. When off, per-server metrics
    /// still work — the periodic <see cref="ServerResyncMs"/> floor remains the source of truth;
    /// this only stops the monitor reading the journal (useful in restricted sandboxes), and
    /// with it engine event history. No effect unless KGSM is configured at all
    /// (<see cref="KgsmEnabled"/>).
    /// </summary>
    public bool EventsEnabled { get; init; } = true;

    /// <summary>
    /// How often to recompute each server's on-disk footprint (a recursive directory walk
    /// of the instance's working dir), in milliseconds.
    /// Default 60s, floor 5s. This stats every file under the tree — far heavier than the
    /// cgroup reads — so it runs on its own slow cadence, off both the 1&#160;Hz metrics tick
    /// and the instance resync. The walked figure is cached and conflated; the tick reads
    /// the latest value. No effect unless KGSM is configured (<see cref="KgsmEnabled"/>).
    /// </summary>
    public int DiskUsageMs { get; init; } = 60_000;

    /// <summary>
    /// Identity this host persists its <c>host</c>-kind metrics under (the <c>entity_id</c> of host
    /// rows). Defaults to the machine name — the same default kgsm-api
    /// uses for its own host id, so the api's history queries (which pass its host id) line up with
    /// the rows the monitor stored. Set both to the same value when overriding.
    /// </summary>
    public string HostId { get; init; } = Environment.MachineName;

    /// <summary>Whether the monitor persists + serves metrics history. When off, the
    /// persist/maintenance loops and the <c>/metrics/history</c> endpoint are not wired — the
    /// monitor runs live-only.</summary>
    public bool HistoryEnabled { get; init; } = true;

    /// <summary>SQLite file for the metrics history store. Defaults under
    /// the systemd <c>StateDirectory</c> (<c>/var/lib/kgsm-monitor</c>, persistent) — NOT the tmpfs
    /// runtime dir where the socket lives.</summary>
    public string HistoryDbPath { get; init; } = "/var/lib/kgsm-monitor/metrics.db";

    /// <summary>How often the persist loop flushes the latest frame to history, ms.
    /// Default 15s, floor 1s (decoupled from the 1&#160;Hz sample tick).</summary>
    public int PersistMs { get; init; } = 15_000;

    /// <summary>Raw-tier retention, hours. Default 24. Also the
    /// tier-select boundary: a query range at or under this reads raw, above it reads rollup.</summary>
    public int RawRetentionHours { get; init; } = 24;

    /// <summary>Rollup bucket width, minutes. Default 5.</summary>
    public int RollupStepMin { get; init; } = 5;

    /// <summary>Rollup-tier retention, days. Default 30.</summary>
    public int RollupRetentionDays { get; init; } = 30;

    /// <summary>How often maintenance (rollup + prune + vacuum) runs, ms.
    /// Default 60s, floor 1s.</summary>
    public int MaintenanceMs { get; init; } = 60_000;

    /// <summary>Whether the monitor persists + serves KGSM engine-event history.
    /// Independent of
    /// <see cref="HistoryEnabled"/> (metrics) — either can be toggled without the other. Has no
    /// effect unless <see cref="KgsmEnabled"/> (event history is derived from the engine's
    /// journal, which is only read when KGSM is configured).</summary>
    public bool EventHistoryEnabled { get; init; } = true;

    /// <summary>SQLite file for the event-history store. Defaults
    /// under the systemd <c>StateDirectory</c> (<c>/var/lib/kgsm-monitor</c>), a separate file from
    /// <see cref="HistoryDbPath"/> — its own WAL/writer, no contention with the metrics flusher.</summary>
    public string EventsDbPath { get; init; } = "/var/lib/kgsm-monitor/events.db";

    /// <summary>Event-history retention, days. Default 30.
    /// No rollup tier (discrete facts, not a sampled series) — rows simply age out at this cutoff.</summary>
    public int EventRetentionDays { get; init; } = 30;

    /// <summary>True when per-server sampling is configured (a KGSM path was provided).</summary>
    public bool KgsmEnabled => KgsmPath.Length > 0;

    /// <summary>
    /// Normalizes bound configuration into the runtime view.
    /// </summary>
    /// <remarks>
    /// A cadence below its floor is raised to the floor rather than reverting to the coded
    /// default: the floor is the nearest legal value to what was asked for, and it is the same
    /// bound the Control Panel rejects against before it restarts anything. A blank string means
    /// "unset" and falls back — which is what lets a settings file ship an empty
    /// <see cref="HostId"/> and still resolve to the machine name.
    /// </remarks>
    public static MonitorOptions FromSettings(MonitorSettings s)
    {
        var defaults = new MonitorOptions();

        return new MonitorOptions
        {
            IntervalMs = Floor(s.IntervalMs ?? defaults.IntervalMs, 100),
            SocketPath = Or(s.SocketPath, defaults.SocketPath),
            SocketMode = ParseMode(s.SocketMode, defaults.SocketMode),
            MountFsDeny = new HashSet<string>(SplitCsv(s.MountFsDeny), StringComparer.Ordinal),
            IfaceDenyPrefixes = SplitCsv(s.IfaceDenyPrefixes) is { Length: > 0 } iface
                ? iface
                : defaults.IfaceDenyPrefixes,
            KgsmPath = s.KgsmPath?.Trim() ?? string.Empty,
            KgsmJournalDir = Or(s.KgsmJournalDir, defaults.KgsmJournalDir),
            ServerResyncMs = Floor(s.ServerResyncMs ?? defaults.ServerResyncMs, 1000),
            EventsEnabled = s.EventsEnabled ?? defaults.EventsEnabled,
            DiskUsageMs = Floor(s.DiskUsageMs ?? defaults.DiskUsageMs, 5000),
            HostId = Or(s.HostId, Environment.MachineName),
            HistoryEnabled = !(s.HistoryDisabled ?? !defaults.HistoryEnabled),
            HistoryDbPath = Or(s.HistoryDbPath, defaults.HistoryDbPath),
            PersistMs = Floor(s.PersistMs ?? defaults.PersistMs, 1000),
            RawRetentionHours = Floor(s.RawRetentionHours ?? defaults.RawRetentionHours, 1),
            RollupStepMin = Floor(s.RollupStepMin ?? defaults.RollupStepMin, 1),
            RollupRetentionDays = Floor(s.RollupRetentionDays ?? defaults.RollupRetentionDays, 1),
            MaintenanceMs = Floor(s.MaintenanceMs ?? defaults.MaintenanceMs, 1000),
            EventHistoryEnabled = !(s.EventHistoryDisabled ?? !defaults.EventHistoryEnabled),
            EventsDbPath = Or(s.EventsDbPath, defaults.EventsDbPath),
            EventRetentionDays = Floor(s.EventRetentionDays ?? defaults.EventRetentionDays, 1),
        };
    }

    private static int Floor(int value, int min) => value < min ? min : value;

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string[] SplitCsv(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static UnixFileMode ParseMode(string? octal, UnixFileMode fallback)
    {
        if (string.IsNullOrWhiteSpace(octal))
            return fallback;
        try { return (UnixFileMode)Convert.ToInt32(octal.Trim(), 8); }
        catch (Exception) { return fallback; }   // malformed octal -> keep the default
    }
}
