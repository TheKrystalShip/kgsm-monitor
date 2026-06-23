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

    /// <summary>Unix domain socket to listen on. <c>KGSM_MONITOR_SOCKET</c>.</summary>
    public string SocketPath { get; init; } = "/run/kgsm-monitor.sock";

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
    /// Unix socket the monitor owns for KGSM event delivery (the low-latency watch-list
    /// delta in Slice 2b). <c>KGSM_MONITOR_KGSM_SOCKET</c>. Distinct from <see cref="SocketPath"/>
    /// (which the API scrapes); this one is where KGSM pushes <c>instance_*</c> events.
    /// </summary>
    public string KgsmSocketPath { get; init; } = "/run/kgsm-monitoring.sock";

    /// <summary>
    /// How often to re-list KGSM instances (the source-of-truth resync), in milliseconds.
    /// <c>KGSM_MONITOR_RESYNC_MS</c>. Default 15s, floor 1s. This shells out to KGSM (a
    /// process spawn — the very cost the metrics path avoids), so it runs on its own slow
    /// cadence, off the 1 Hz metrics tick.
    /// </summary>
    public int ServerResyncMs { get; init; } = 15_000;

    /// <summary>
    /// Whether to listen for KGSM lifecycle events on <see cref="KgsmSocketPath"/> for
    /// low-latency watch-list deltas (Slice 2b). <c>KGSM_MONITOR_EVENTS</c> (default on).
    /// When off, per-server metrics still work — the periodic <see cref="ServerResyncMs"/>
    /// floor remains the source of truth; this only stops the monitor binding the event
    /// socket (useful in restricted sandboxes). No effect unless KGSM is configured at all
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
            KgsmSocketPath = Env("KGSM_MONITOR_KGSM_SOCKET") is { Length: > 0 } ks ? ks : defaults.KgsmSocketPath,
            ServerResyncMs = resync,
            DiskUsageMs = diskUsage,
            EventsEnabled = ParseBool(Env("KGSM_MONITOR_EVENTS"), defaults.EventsEnabled),
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
