using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Extensions;
using TheKrystalShip.KGSM.Monitor;
using TheKrystalShip.KGSM.Monitor.Contracts;
using TheKrystalShip.KGSM.Monitor.History;
using TheKrystalShip.KGSM.Monitor.Sampling;

var builder = WebApplication.CreateSlimBuilder(args);

// Load the daemon's settings file from beside the binary. Two reasons it must be explicit (same as
// kgsm-watchdog):
//   1. CreateSlimBuilder under a systemd unit with no WorkingDirectory leaves the content root at "/",
//      so the framework's default appsettings.json discovery finds nothing — the file's settings (the
//      "Microsoft.AspNetCore":"Warning" log filter today) silently never apply, and ASP.NET's
//      per-request Information chatter floods journald on every api scrape. Resolve it from
//      AppContext.BaseDirectory (the binary's own dir, /opt/kgsm-monitor), where deploy installs it.
//   2. It is named kgsm-monitor.settings.json, NOT appsettings.json, so it can never collide with a
//      sibling ecosystem service's config if they ever share a directory.
// optional:true so a missing file never stops the daemon.
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "kgsm-monitor.settings.json"), optional: true, reloadOnChange: false);

// Environment variables are re-registered so they sit LAST and therefore win. Configuration
// resolves by source order, and the settings file above was appended after the sources the builder
// installed — including the builder's own environment provider. Without this line the file would
// outrank every Monitor__* and Logging__* variable, and an override would read as applied while
// changing nothing.
builder.Configuration.AddEnvironmentVariables();

// Ecosystem-standard logging (see ../tks/logging-convention.md): one journald-native SystemdConsole
// sink (the <N> syslog priority prefix lets `journalctl -p` filter by level). AddConfiguration binds the
// "Logging" section from kgsm-monitor.settings.json plus any Logging__LogLevel__Default override —
// wired explicitly so the level knob is deterministic on the slim builder rather than relying on an
// implicit default.
builder.Logging.ClearProviders();
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
builder.Logging.AddSystemdConsole();

// kgsm-monitor.settings.json is the source of truth for every knob: each is declared there with
// its default, and an environment variable (Monitor__IntervalMs, Monitor__HistoryDbPath, …) may
// only override a key that exists in it. A variable naming an undeclared key binds to nothing,
// which is precisely what stops a stale override from looking applied.
var settings = builder.Configuration.GetSection(MonitorSettings.Section).Get<MonitorSettings>()
    ?? new MonitorSettings();
var options = MonitorOptions.FromSettings(settings);
builder.Services.AddSingleton(options);

// Per-server sampling (Slice 2) is opt-in: only when a KGSM path is configured. The
// embedded kgsm-lib supplies the instance watch-list (resync) and, later, event deltas.
// Without it the monitor runs host-only and the servers array is simply empty.
if (options.KgsmEnabled)
{
    // Engine events come from the journal: a file any number of consumers read concurrently,
    // with no socket to bind, no path to own, and nothing for the engine to be configured with.
    // CursorOrOldest because this monitor IS the event index — it must be able to replay the
    // surviving journal to rebuild, and the deterministic AuditId makes a replay idempotent.
    builder.Services.AddKgsmServices(new KgsmOptions
    {
        KgsmPath = options.KgsmPath,
        EventJournalDirectory = options.KgsmJournalDir,
        EventStartPosition = EventStartPosition.CursorOrOldest
    });
    builder.Services.AddSingleton<ServerSampler>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerSampler>());
}

// Per-leaf sampling, independent of everything above: the ecosystem's own daemons are systemd units
// with cgroups, which needs no KGSM, no privilege and no other leaf. The watch-list comes from the
// shared descriptor directory, so a leaf deployed later is measured with nothing rebuilt here.
if (options.LeafMetricsEnabled)
{
    builder.Services.AddSingleton<LeafSampler>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<LeafSampler>());
}

// The sampler is one singleton that is also the hosted background service, so the
// /metrics endpoint reads the exact instance that is ticking.
builder.Services.AddSingleton<MetricsSampler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricsSampler>());

// Metrics history (opt-out via Monitor__HistoryDisabled): the monitor is the single source of
// truth for history. The persist loop flushes the latest frame to SQLite every Monitor__PersistMs;
// maintenance rolls up + prunes; GET /metrics/history serves windowed queries. Raw ADO SQLite (AOT-safe).
if (options.HistoryEnabled)
{
    builder.Services.AddSingleton<HistoryStore>();
    builder.Services.AddHostedService<MetricsPersistService>();
}

// Event index: the monitor owns the queryable index over KGSM ENGINE events (server-triggered
// lifecycle/config/etc). The record itself is the engine's append-only journal — events.db is derived
// from it and rebuildable from it (POST /events/rebuild). Gated on KgsmEnabled (needs IEventService,
// only registered above under that flag) AND EventHistoryEnabled (an independent opt-out from metrics
// history). A separate events.db (own WAL/single-writer gate) — no contention with the 15s metrics
// flusher. GET /events serves windowed/filtered queries; kgsm-api merges this with its own API-only
// audit rows at read time — the monitor stays a neutral leaf with no dependency on the API.
if (options.KgsmEnabled && options.EventHistoryEnabled)
{
    builder.Services.AddSingleton<EventHistoryStore>();
    builder.Services.AddSingleton<EventIndexRebuilder>();

    // Replaces the library's default file-backed cursor store, registered above by
    // AddKgsmServices — last registration wins. The monitor's position belongs in the same
    // database as the events derived from it, not in a separate file that can disagree with it.
    builder.Services.AddSingleton<IEventCursorStore, EventJournalCursorStore>();

    builder.Services.AddHostedService<EventPersistService>();
}

// One shared rollup/prune/vacuum loop for whichever history store(s) are active. Registered whenever
// either metrics or event history is on; MetricsMaintenanceService takes both stores as OPTIONAL
// constructor params so it degrades correctly if only one is enabled (they're independently
// toggleable — see MonitorOptions.HistoryEnabled / EventHistoryEnabled).
if (options.HistoryEnabled || (options.KgsmEnabled && options.EventHistoryEnabled))
{
    builder.Services.AddHostedService<MetricsMaintenanceService>();
}

// Server -> client only, and the only consumer is the local KGSM API. Bind a unix
// domain socket (no exposed TCP port; the socket's filesystem perms are the boundary).
builder.WebHost.ConfigureKestrel(kestrel =>
{
    if (File.Exists(options.SocketPath))
        File.Delete(options.SocketPath);
    kestrel.ListenUnixSocket(options.SocketPath);
});

var app = builder.Build();

// The socket file only exists once the host has started listening — chmod it here,
// not before app.Run() (which would hit ENOENT). Default 0660 lets an API process in
// the socket's group scrape it without exposing the data world-wide.
app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        if (OperatingSystem.IsLinux() && File.Exists(options.SocketPath))
            File.SetUnixFileMode(options.SocketPath, options.SocketMode);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "could not set mode on socket {Socket}", options.SocketPath);
    }
});

// Unified ecosystem liveness/readiness probe: 200 ⇒ the metrics service is up and able
// to serve (an empty/warming snapshot is still "available" — the no-fresh-frame state lives
// on /metrics 503, never here). Any non-200/no-answer ⇒ unavailable. Renamed from /healthz.
app.MapGet("/health", () => Results.Text("ok\n"));

// Consumer-agnostic scrape: return the latest precomputed frame (conflated). 503
// until the first tick lands.
app.MapGet("/metrics", (MetricsSampler sampler) =>
{
    var snapshot = sampler.Latest;
    return snapshot is null
        ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        : Results.Json(snapshot, MonitorJsonContext.Default.Snapshot);
});

// Windowed metrics history. Primitive query params only (AOT-safe minimal-API binding). Tier is
// chosen automatically by range (≤ raw retention → raw, else rollup). Serialized via the daemon-local
// history JSON context — the shared MonitorJsonContext (Snapshot) is untouched. Only mapped when
// history is enabled (the store singleton exists).
if (options.HistoryEnabled)
{
    app.MapGet("/metrics/history", async (HistoryStore store, string? kind, string? id, string? range, CancellationToken ct) =>
    {
        // An unrecognised kind falls to "server" rather than 400-ing, which is what the endpoint has always
        // done: the entity kinds are a closed set the api passes verbatim, and an unknown one simply
        // matches no rows. Naming one explicitly is the only way to read its rows.
        string entityKind = kind switch
        {
            "host" => "host",
            "leaf" => "leaf",
            _ => "server",
        };
        if (string.IsNullOrEmpty(id))
            return Results.BadRequest();
        MetricsHistoryResponse resp = await store.QueryHistoryAsync(entityKind, id, range, ct);
        return Results.Json(resp, MonitorHistoryJsonContext.Default.MetricsHistoryResponse);
    });
}

// Windowed/filtered engine-event history. Primitive query params only (AOT-safe minimal-API
// binding); ms/int values are parsed by hand. ts-DESC, composite (before_ts, before_id) keyset
// cursor. Serialized via the same daemon-local history JSON context as /metrics/history. Only mapped
// when event history is enabled (the store singleton exists).
if (options.KgsmEnabled && options.EventHistoryEnabled)
{
    app.MapGet("/events", async (
        EventHistoryStore store,
        string? instance, string? blueprint, string? type, string? since, string? until,
        string? before_ts, string? before_id, string? limit,
        CancellationToken ct) =>
    {
        long? sinceMs = long.TryParse(since, out long sv) ? sv : null;
        long? untilMs = long.TryParse(until, out long uv) ? uv : null;
        long? beforeTsMs = long.TryParse(before_ts, out long bv) ? bv : null;
        int lim = int.TryParse(limit, out int lv) ? lv : EventHistoryStore.DefaultLimit;

        EventHistoryResponse resp = await store.QueryEventsAsync(
            instance, type, sinceMs, untilMs, beforeTsMs, before_id, lim, blueprint, ct);
        return Results.Json(resp, MonitorHistoryJsonContext.Default.EventHistoryResponse);
    });

    // Rebuild the index from the journal it is derived from — the operator's recovery path when
    // events.db is lost, corrupted, or was never written (a monitor that was down while the engine
    // kept emitting). Additive and idempotent: it inserts what is missing, never clears the table,
    // never moves the live cursor, and never erases a recorded gap. Safe to call while streaming.
    // POST because it writes, though it is the rare write with no arguments to get wrong.
    app.MapPost("/events/rebuild", async (EventIndexRebuilder rebuilder, CancellationToken ct) =>
    {
        EventIndexRebuildResult result = await rebuilder.RebuildAsync(ct);
        return result.Status == "busy"
            ? Results.Json(result, MonitorHistoryJsonContext.Default.EventIndexRebuildResult,
                statusCode: StatusCodes.Status409Conflict)
            : Results.Json(result, MonitorHistoryJsonContext.Default.EventIndexRebuildResult);
    });
}

app.Logger.LogInformation(
    "kgsm-monitor listening on unix:{Socket} (interval {Interval}ms)",
    options.SocketPath, options.IntervalMs);
app.Run();
