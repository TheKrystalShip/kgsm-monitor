using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Extensions;
using TheKrystalShip.KGSM.Monitor;
using TheKrystalShip.KGSM.Monitor.Contracts;
using TheKrystalShip.KGSM.Monitor.History;
using TheKrystalShip.KGSM.Monitor.Sampling;
using TheKrystalShip.KGSM.Monitor.Thresholds;
using TheKrystalShip.KGSM.Services;

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
    // Tail with no cursor — these events only trigger a watch-list resync, and the resync floor
    // re-derives the same answer anyway, so replaying a backlog on start would be redundant work
    // rather than recovered state. The monitor persists no event, so there is nothing to catch up.
    builder.Services.AddKgsmServices(new KgsmOptions
    {
        KgsmPath = options.KgsmPath,
        EventJournalDirectory = options.KgsmJournalDir,
        EventStartPosition = EventStartPosition.Tail
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

// Threshold evaluation, registered before the sampler that consumes it. Left unregistered when switched
// off, which is what makes the sampler's optional dependency resolve to null and every frame carry no
// conditions — the daemon does not evaluate rules nobody asked it to.
if (options.ThresholdsEnabled)
{
    builder.Services.AddSingleton<ConditionEvaluator>();
    builder.Services.AddSingleton<PolicyStore>();

    // Every RESPONSE this daemon writes names its context explicitly, so the framework's own serializer
    // options have never needed a resolver. A request BODY is different: minimal APIs bind it through those
    // options, and with no resolver the PUT below cannot be built — which fails while the route table is
    // being constructed and therefore takes every endpoint down with it, /metrics included. Registering the
    // context here is what makes the body bindable.
    builder.Services.ConfigureHttpJsonOptions(json =>
        json.SerializerOptions.TypeInfoResolverChain.Insert(0, MonitorThresholdJsonContext.Default));
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

// The rollup/prune/vacuum loop for metrics history. Its outcome holder is registered unconditionally:
// GET /stats reads it whether or not history is on, and a missing singleton would make the endpoint's
// wiring depend on a knob rather than the answer depending on it.
builder.Services.AddSingleton<MaintenanceState>();
if (options.HistoryEnabled)
{
    builder.Services.AddHostedService<MetricsMaintenanceService>();
}

// Threshold episodes — the durable record of what fired and for how long, as opposed to the live
// conditions on each frame. Needs both halves: something evaluating the rules, and somewhere to write.
// Missing either, alerts still work off the live frame and nothing is recorded, which is said out loud
// because "this host keeps no record of what fired" is found out at the worst moment otherwise.
if (options.ThresholdsEnabled && options.HistoryEnabled)
{
    // This daemon's own event journal, beside its history database. Nothing else on this host takes
    // these measurements, so nothing else can honestly say a value crossed a line — recording them here
    // puts the fact where it happened, instead of leaving another component to poll them out of a
    // database and transcribe them into its own store. The producer id is the state directory's own
    // name, which is what a reader scans for, so writer and readers agree on the location without
    // either being told.
    builder.Services.AddSingleton<IEventJournalWriter>(sp => new EventJournalWriter(
        new EventJournalWriterOptions
        {
            Producer = "kgsm-monitor",
            Directory = Path.Combine(
                Path.GetDirectoryName(options.HistoryDbPath) ?? "/var/lib/kgsm-monitor", "events"),
            ProducerVersion = typeof(Program).Assembly.GetName().Version?.ToString(),
        },
        sp.GetRequiredService<ILogger<EventJournalWriter>>()));
    builder.Services.AddSingleton<MonitorJournal>();

    // Create the journal directory now rather than on the first episode. A reader discovers a producer
    // by finding its journal, so a daemon that has simply not breached anything yet would otherwise be
    // indistinguishable from one that does not write a journal at all — and would stay invisible until
    // both an episode fired and every reader restarted.
    string journalDir = Path.Combine(
        Path.GetDirectoryName(options.HistoryDbPath) ?? "/var/lib/kgsm-monitor", "events");
    try { Directory.CreateDirectory(journalDir); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // Not fatal: the writer retries on every append and says so if it still cannot.
        Console.Error.WriteLine($"<4>could not create the event journal directory {journalDir}: {ex.Message}");
    }

    builder.Services.AddHostedService<EpisodeRecorder>();
}
else if (options.ThresholdsEnabled)
{
    Console.WriteLine("<4>threshold episodes: history is off, so nothing this host alerts on is recorded");
}

// Server -> client only, and the only consumer is the local KGSM API. Bind a unix
// domain socket (no exposed TCP port; the socket's filesystem perms are the boundary).
builder.WebHost.ConfigureKestrel(kestrel =>
{
    if (File.Exists(options.SocketPath))
        File.Delete(options.SocketPath);
    kestrel.ListenUnixSocket(options.SocketPath);
});

// Stamped before the host starts so uptime is this process's, not the endpoint's first call.
var startedAt = DateTimeOffset.UtcNow;

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

// The rules this daemon watches its own numbers against, and the one place they can be changed without a
// restart. Only mapped when thresholds are on: with them off there is no policy being evaluated, and
// serving one would describe rules nothing is applying.
//
// NB this makes the socket writable, which it otherwise is not. The boundary is unchanged — the socket's
// filesystem permissions, which already govern reading every metric this host produces.
if (options.ThresholdsEnabled)
{
    app.MapGet("/thresholds", (PolicyStore store) =>
        Results.Json(store.Document(), MonitorThresholdJsonContext.Default.ThresholdPolicyDocument));

    app.MapPut("/thresholds", async (ThresholdPolicyRequest? request, PolicyStore store, CancellationToken ct) =>
    {
        PolicyStore.ApplyResult result = await store.ApplyAsync(request?.Rules, ct);
        if (result.Ok)
            return Results.Json(result.Document, MonitorThresholdJsonContext.Default.ThresholdPolicyDocument);

        // A refused policy is the caller's to fix; a policy that could not be written is this host's, and
        // the two must not look alike to whatever is driving this.
        var error = new ThresholdErrorResponse(result.Error!, result.Key);
        return Results.Json(error, MonitorThresholdJsonContext.Default.ThresholdErrorResponse,
            statusCode: result.Retryable ? StatusCodes.Status500InternalServerError : StatusCodes.Status400BadRequest);
    });

    // What fired, and for how long — the durable half, as opposed to /metrics's live conditions. Only
    // mapped with history on, matching /metrics/history: with no store there are no episodes, and an empty
    // list would claim nothing ever fired rather than that nothing was written down.
    if (options.HistoryEnabled)
    {
        app.MapGet("/thresholds/episodes", async (HistoryStore store, long? since, int? limit, CancellationToken ct) =>
        {
            // Default to the raw-retention window: the same span this host keeps samples for, so an episode
            // and the curve behind it are readable together or not at all.
            long sinceMs = since ?? DateTimeOffset.UtcNow.AddHours(-options.RawRetentionHours).ToUnixTimeMilliseconds();
            int cap = Math.Clamp(limit ?? 500, 1, 5000);
            IReadOnlyList<EpisodeRow> rows = await store.QueryEpisodesAsync(sinceMs, cap, ct);
            return Results.Json(new EpisodesResponse(rows), MonitorEpisodeJsonContext.Default.EpisodesResponse);
        });
    }

    app.MapDelete("/thresholds", async (PolicyStore store, CancellationToken ct) =>
    {
        PolicyStore.ApplyResult result = await store.ResetAsync(ct);
        if (result.Ok)
            return Results.Json(result.Document, MonitorThresholdJsonContext.Default.ThresholdPolicyDocument);
        return Results.Json(new ThresholdErrorResponse(result.Error!, null),
            MonitorThresholdJsonContext.Default.ThresholdErrorResponse,
            statusCode: StatusCodes.Status500InternalServerError);
    });
}

// What this recorder is doing and what it has recorded — the daemon's view of itself, as opposed to
// /metrics (the host's numbers) and /metrics/history (a window of them). Always mapped: with history off
// it still answers, reporting `history:null`, because "history is switched off" is exactly the kind of
// thing an operator comes here to find out and a 404 would leave them guessing.
//
// The store read touches SQLite, so unlike /metrics this endpoint does real work per call. It is a
// human-cadence page, not a scrape target, and the counts are cheap (PK-index aggregates).
app.MapGet("/stats", async (
    MetricsSampler sampler,
    MonitorOptions opts,
    MaintenanceState maintenance,
    IServiceProvider services,
    CancellationToken ct) =>
{
    Snapshot? latest = sampler.Latest;

    // Coverage is counted off the newest FRAME, not off what the daemon was told to watch: a source
    // configured but finding nothing and a source not configured are different facts, and the frame is
    // the one that reports what was actually measured. No frame yet ⇒ zeroes beside the enabled flags,
    // which together say "wired, nothing sampled yet".
    var coverage = new SampleCoverage(
        Servers: latest?.Servers.Length ?? 0,
        Leaves: latest?.Leaves.Length ?? 0,
        Sensors: latest?.Sensors.Length ?? 0,
        Cores: latest?.Cpu.PerCore.Length ?? 0,
        ServersEnabled: opts.KgsmEnabled,
        LeavesEnabled: opts.LeafMetricsEnabled);

    HistoryStats? history = null;
    if (opts.HistoryEnabled && services.GetService(typeof(HistoryStore)) is HistoryStore store)
    {
        HistoryStoreStats? measured = await store.StatsAsync(ct);
        if (measured is not null)
        {
            // The configured intents and the measured reality, joined here and never reconciled: the
            // whole value of this block is that the reader can see them disagree.
            history = new HistoryStats(
                DbPath: measured.DbPath,
                DbBytes: measured.DbBytes,
                RawRetentionHours: opts.RawRetentionHours,
                RollupStepMin: opts.RollupStepMin,
                RollupRetentionDays: opts.RollupRetentionDays,
                MaintenanceMs: opts.MaintenanceMs,
                LastMaintenanceMs: maintenance.LastRunMs,
                LastMaintenanceOk: maintenance.LastOk,
                RawRows: measured.RawRows,
                RawEntities: measured.RawEntities,
                RawOldestMs: measured.RawOldestMs,
                RawNewestMs: measured.RawNewestMs,
                RollupRows: measured.RollupRows,
                RollupEntities: measured.RollupEntities,
                RollupOldestMs: measured.RollupOldestMs,
                RollupNewestMs: measured.RollupNewestMs);
        }
    }

    var stats = new MonitorStats(
        IntervalMs: opts.IntervalMs,
        LatestSampleMs: latest?.Ts,
        // Measured from this process, so a daemon that restarted five minutes ago says so — which is
        // the explanation for a history gap that would otherwise look like a store fault.
        UptimeSec: (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds,
        Coverage: coverage,
        History: history);

    return Results.Json(stats, MonitorHistoryJsonContext.Default.MonitorStats);
});

app.Logger.LogInformation(
    "kgsm-monitor listening on unix:{Socket} (interval {Interval}ms)",
    options.SocketPath, options.IntervalMs);
app.Run();
