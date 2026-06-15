using TheKrystalShip.KGSM.Extensions;
using TheKrystalShip.KGSM.Monitor;
using TheKrystalShip.KGSM.Monitor.Contracts;
using TheKrystalShip.KGSM.Monitor.Sampling;

var builder = WebApplication.CreateSlimBuilder(args);

// All configuration comes from environment variables (systemd-friendly, AOT-safe).
var options = MonitorOptions.FromEnvironment();
builder.Services.AddSingleton(options);

// Per-server sampling (Slice 2) is opt-in: only when a KGSM path is configured. The
// embedded kgsm-lib supplies the instance watch-list (resync) and, later, event deltas.
// Without it the monitor runs host-only and the servers array is simply empty.
if (options.KgsmEnabled)
{
    builder.Services.AddKgsmServices(options.KgsmPath, options.KgsmSocketPath);
    builder.Services.AddSingleton<ServerSampler>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerSampler>());
}

// The sampler is one singleton that is also the hosted background service, so the
// /metrics endpoint reads the exact instance that is ticking.
builder.Services.AddSingleton<MetricsSampler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricsSampler>());

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

app.Logger.LogInformation(
    "kgsm-monitor listening on unix:{Socket} (interval {Interval}ms)",
    options.SocketPath, options.IntervalMs);
app.Run();
