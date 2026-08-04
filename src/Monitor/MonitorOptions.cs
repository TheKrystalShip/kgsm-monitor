namespace TheKrystalShip.KGSM.Monitor;

/// <summary>
/// Runtime configuration, sourced from environment variables (the idiomatic
/// mechanism for a systemd daemon — see <c>Environment=</c> in the unit file).
/// Reading is deliberately reflection-free (no config-binding source generator)
/// so the daemon stays Native-AOT clean. All knobs have sane defaults; an empty
/// or unset variable keeps the default.
/// </summary>
public sealed class MonitorOptions
{
    /// <summary>Sampling cadence in milliseconds. <c>KGSM_MONITOR_INTERVAL_MS</c>.</summary>
    public int IntervalMs { get; init; } = 1000;

    /// <summary>Unix domain socket to listen on. <c>KGSM_MONITOR_SOCKET</c>. Lives inside the
    /// per-service runtime dir (systemd <c>RuntimeDirectory=kgsm-monitor</c>) so the path matches
    /// the deployed unit and a co-located API connects to the same default.</summary>
    public string SocketPath { get; init; } = "/run/kgsm-monitor/metrics.sock";

    /// <summary>
    /// Permission bits applied to the socket once it exists. <c>KGSM_MONITOR_SOCKET_MODE</c>
    /// (octal, e.g. <c>660</c>). Default <c>0660</c> — owner+group read/write, so an API
    /// process in the socket's group can scrape it without exposing it world-wide.
    /// </summary>
    public UnixFileMode SocketMode { get; init; } =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite;

    /// <summary>
    /// Extra filesystem types to hide from the mount list, on top of the always-filtered
    /// pseudo filesystems. <c>KGSM_MONITOR_MOUNT_FS_DENY</c> (comma-separated). Default empty.
    /// </summary>
    public IReadOnlySet<string> MountFsDeny { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Interface-name prefixes to exclude from host network rates, in addition to the
    /// always-excluded loopback. <c>KGSM_MONITOR_IFACE_DENY</c> (comma-separated).
    /// Default <c>veth</c>: virtual-ethernet pairs are per-container noise that double-counts
    /// container traffic in the host aggregate. Operators commonly add <c>docker</c>/<c>br-</c>.
    /// </summary>
    public IReadOnlyList<string> IfaceDenyPrefixes { get; init; } = ["veth"];

    /// <summary>
    /// Path to the KGSM executable (<c>kgsm.sh</c>). <c>KGSM_MONITOR_KGSM_PATH</c>. When
    /// empty (the default) per-server sampling is disabled and the monitor runs host-only,
    /// so the daemon is useful even where KGSM is absent. When set, the monitor periodically
    /// runs <c>instances list --detailed</c> to learn the server watch-list.
    /// </summary>
    public string KgsmPath { get; init; } = string.Empty;

    /// <summary>
    /// Directory holding KGSM's append-only event journal, which the monitor tails for engine
    /// events. <c>KGSM_MONITOR_KGSM_JOURNAL</c>. Read-only to the monitor — the engine is the
    /// only writer, and any number of consumers read the same files, so nothing here is owned
    /// by or reserved for the monitor. Unrelated to <see cref="SocketPath"/>, which is the
    /// monitor's own outbound metrics socket.
    /// </summary>
    public string KgsmJournalDir { get; init; } = "/var/lib/kgsm/events";

    /// <summary>
    /// How often to re-list KGSM instances (the source-of-truth resync), in milliseconds.
    /// <c>KGSM_MONITOR_RESYNC_MS</c>. Default 15s, floor 1s. This shells out to KGSM (a
    /// process spawn — the very cost the metrics path avoids), so it runs on its own slow
    /// cadence, off the 1 Hz metrics tick.
    /// </summary>
    public int ServerResyncMs { get; init; } = 15_000;

    /// <summary>
    /// Whether to read KGSM lifecycle events from <see cref="KgsmJournalDir"/> for low-latency
    /// watch-list deltas. <c>KGSM_MONITOR_EVENTS</c> (default on). When off, per-server metrics
    /// still work — the periodic <see cref="ServerResyncMs"/> floor remains the source of truth;
    /// this only stops the monitor reading the journal (useful in restricted sandboxes), and
    /// with it engine event history. No effect unless KGSM is configured at all
    /// (<see cref="KgsmEnabled"/>). Accepts <c>1/0</c>, <c>true/false</c>, <c>yes/no</c>,
    /// <c>on/off</c>.
    /// </summary>
    public bool EventsEnabled { get; init; } = true;

    /// <summary>
    /// How often to recompute each server's on-disk footprint (a recursive directory walk
    /// of the instance's working dir), in milliseconds. <c>KGSM_MONITOR_DISK_USAGE_MS</c>.
    /// Default 60s, floor 5s. This stats every file under the tree — far heavier than the
    /// cgroup reads — so it runs on its own slow cadence, off both the 1&#160;Hz metrics tick
    /// and the instance resync. The walked figure is cached and conflated; the tick reads
    /// the latest value. No effect unless KGSM is configured (<see cref="KgsmEnabled"/>).
    /// </summary>
    public int DiskUsageMs { get; init; } = 60_000;

    /// <summary>
    /// Identity this host persists its <c>host</c>-kind metrics under (the <c>entity_id</c> of host
    /// rows). <c>KGSM_MONITOR_HOST_ID</c>. Defaults to the machine name — the same default kgsm-api
    /// uses for its own host id, so the api's history queries (which pass its host id) line up with
    /// the rows the monitor stored. Set both to the same value when overriding.
    /// </summary>
    public string HostId { get; init; } = Environment.MachineName;

    /// <summary>Whether the monitor persists + serves metrics history. <c>KGSM_MONITOR_HISTORY_DISABLED</c>
    /// (default enabled). When off, the persist/maintenance loops and the <c>/metrics/history</c>
    /// endpoint are not wired — the monitor runs live-only.</summary>
    public bool HistoryEnabled { get; init; } = true;

    /// <summary>SQLite file for the metrics history store. <c>KGSM_MONITOR_DB_PATH</c>. Defaults under
    /// the systemd <c>StateDirectory</c> (<c>/var/lib/kgsm-monitor</c>, persistent) — NOT the tmpfs
    /// runtime dir where the socket lives.</summary>
    public string HistoryDbPath { get; init; } = "/var/lib/kgsm-monitor/metrics.db";

    /// <summary>How often the persist loop flushes the latest frame to history, ms.
    /// <c>KGSM_MONITOR_PERSIST_MS</c>. Default 15s, floor 1s (decoupled from the 1&#160;Hz sample tick).</summary>
    public int PersistMs { get; init; } = 15_000;

    /// <summary>Raw-tier retention, hours. <c>KGSM_MONITOR_RAW_RETENTION_HOURS</c>. Default 24. Also the
    /// tier-select boundary: a query range at or under this reads raw, above it reads rollup.</summary>
    public int RawRetentionHours { get; init; } = 24;

    /// <summary>Rollup bucket width, minutes. <c>KGSM_MONITOR_ROLLUP_STEP_MIN</c>. Default 5.</summary>
    public int RollupStepMin { get; init; } = 5;

    /// <summary>Rollup-tier retention, days. <c>KGSM_MONITOR_ROLLUP_RETENTION_DAYS</c>. Default 30.</summary>
    public int RollupRetentionDays { get; init; } = 30;

    /// <summary>How often maintenance (rollup + prune + vacuum) runs, ms. <c>KGSM_MONITOR_MAINT_MS</c>.
    /// Default 60s, floor 1s.</summary>
    public int MaintenanceMs { get; init; } = 60_000;

    /// <summary>Whether the monitor persists + serves KGSM engine-event history.
    /// <c>KGSM_MONITOR_EVENT_HISTORY_DISABLED</c> (default enabled). Independent of
    /// <see cref="HistoryEnabled"/> (metrics) — either can be toggled without the other. Has no
    /// effect unless <see cref="KgsmEnabled"/> (event history needs the KGSM event socket).</summary>
    public bool EventHistoryEnabled { get; init; } = true;

    /// <summary>SQLite file for the event-history store. <c>KGSM_MONITOR_EVENTS_DB_PATH</c>. Defaults
    /// under the systemd <c>StateDirectory</c> (<c>/var/lib/kgsm-monitor</c>), a separate file from
    /// <see cref="HistoryDbPath"/> — its own WAL/writer, no contention with the metrics flusher.</summary>
    public string EventsDbPath { get; init; } = "/var/lib/kgsm-monitor/events.db";

    /// <summary>Event-history retention, days. <c>KGSM_MONITOR_EVENT_RETENTION_DAYS</c>. Default 30.
    /// No rollup tier (discrete facts, not a sampled series) — rows simply age out at this cutoff.</summary>
    public int EventRetentionDays { get; init; } = 30;

    /// <summary>True when per-server sampling is configured (a KGSM path was provided).</summary>
    public bool KgsmEnabled => KgsmPath.Length > 0;

    public static MonitorOptions FromEnvironment()
    {
        static string? Env(string key) => Environment.GetEnvironmentVariable(key);

        var defaults = new MonitorOptions();

        int interval = defaults.IntervalMs;
        if (int.TryParse(Env("KGSM_MONITOR_INTERVAL_MS"), out int iv) && iv >= 100)
            interval = iv;

        string socket = Env("KGSM_MONITOR_SOCKET") is { Length: > 0 } s ? s : defaults.SocketPath;

        int resync = defaults.ServerResyncMs;
        if (int.TryParse(Env("KGSM_MONITOR_RESYNC_MS"), out int rs) && rs >= 1000)
            resync = rs;

        int diskUsage = defaults.DiskUsageMs;
        if (int.TryParse(Env("KGSM_MONITOR_DISK_USAGE_MS"), out int du) && du >= 5000)
            diskUsage = du;

        int persist = defaults.PersistMs;
        if (int.TryParse(Env("KGSM_MONITOR_PERSIST_MS"), out int pm) && pm >= 1000)
            persist = pm;

        int rawRetention = defaults.RawRetentionHours;
        if (int.TryParse(Env("KGSM_MONITOR_RAW_RETENTION_HOURS"), out int rr) && rr >= 1)
            rawRetention = rr;

        int rollupStep = defaults.RollupStepMin;
        if (int.TryParse(Env("KGSM_MONITOR_ROLLUP_STEP_MIN"), out int rst) && rst >= 1)
            rollupStep = rst;

        int rollupRetention = defaults.RollupRetentionDays;
        if (int.TryParse(Env("KGSM_MONITOR_ROLLUP_RETENTION_DAYS"), out int rrd) && rrd >= 1)
            rollupRetention = rrd;

        int maint = defaults.MaintenanceMs;
        if (int.TryParse(Env("KGSM_MONITOR_MAINT_MS"), out int mm) && mm >= 1000)
            maint = mm;

        int eventRetention = defaults.EventRetentionDays;
        if (int.TryParse(Env("KGSM_MONITOR_EVENT_RETENTION_DAYS"), out int erd) && erd >= 1)
            eventRetention = erd;

        UnixFileMode mode = defaults.SocketMode;
        if (Env("KGSM_MONITOR_SOCKET_MODE") is { Length: > 0 } modeStr)
        {
            try { mode = (UnixFileMode)Convert.ToInt32(modeStr, 8); }
            catch (Exception) { /* malformed octal -> keep default */ }
        }

        return new MonitorOptions
        {
            IntervalMs = interval,
            SocketPath = socket,
            SocketMode = mode,
            MountFsDeny = ParseSet(Env("KGSM_MONITOR_MOUNT_FS_DENY")) ?? defaults.MountFsDeny,
            IfaceDenyPrefixes = ParseList(Env("KGSM_MONITOR_IFACE_DENY")) ?? defaults.IfaceDenyPrefixes,
            KgsmPath = Env("KGSM_MONITOR_KGSM_PATH") is { Length: > 0 } kp ? kp : defaults.KgsmPath,
            KgsmJournalDir = Env("KGSM_MONITOR_KGSM_JOURNAL") is { Length: > 0 } kj ? kj : defaults.KgsmJournalDir,
            ServerResyncMs = resync,
            DiskUsageMs = diskUsage,
            EventsEnabled = ParseBool(Env("KGSM_MONITOR_EVENTS"), defaults.EventsEnabled),
            HostId = Env("KGSM_MONITOR_HOST_ID") is { Length: > 0 } hid ? hid : defaults.HostId,
            HistoryEnabled = !ParseBool(Env("KGSM_MONITOR_HISTORY_DISABLED"), false),
            HistoryDbPath = Env("KGSM_MONITOR_DB_PATH") is { Length: > 0 } db ? db : defaults.HistoryDbPath,
            PersistMs = persist,
            RawRetentionHours = rawRetention,
            RollupStepMin = rollupStep,
            RollupRetentionDays = rollupRetention,
            MaintenanceMs = maint,
            EventHistoryEnabled = !ParseBool(Env("KGSM_MONITOR_EVENT_HISTORY_DISABLED"), false),
            EventsDbPath = Env("KGSM_MONITOR_EVENTS_DB_PATH") is { Length: > 0 } edb ? edb : defaults.EventsDbPath,
            EventRetentionDays = eventRetention,
        };
    }

    private static bool ParseBool(string? value, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback,
        };
    }

    private static string[]? ParseList(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return null;
        var parts = csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts : null;
    }

    private static IReadOnlySet<string>? ParseSet(string? csv)
    {
        var list = ParseList(csv);
        return list is null ? null : new HashSet<string>(list, StringComparer.Ordinal);
    }
}
