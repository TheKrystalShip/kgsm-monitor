using TheKrystalShip.KGSM.Monitor.Model;
using TheKrystalShip.KGSM.Monitor.Sampling;

var builder = WebApplication.CreateSlimBuilder(args);

// The sampler is one singleton that is also the hosted background service, so the
// /metrics endpoint reads the exact instance that is ticking.
builder.Services.AddSingleton<MetricsSampler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricsSampler>());

// Server -> client only, and the only consumer is the local KGSM API. Bind a unix
// domain socket (no exposed TCP port; the socket's filesystem perms are the boundary).
// Override with KGSM_MONITOR_SOCKET (e.g. for local dev outside /run).
var socketPath = Environment.GetEnvironmentVariable("KGSM_MONITOR_SOCKET") ?? "/run/kgsm-monitor.sock";
builder.WebHost.ConfigureKestrel(options =>
{
    if (File.Exists(socketPath))
        File.Delete(socketPath);
    options.ListenUnixSocket(socketPath);
});

var app = builder.Build();

app.MapGet("/healthz", () => Results.Text("ok\n"));

// Consumer-agnostic scrape: return the latest precomputed frame (conflated). 503
// until the first tick lands.
app.MapGet("/metrics", (MetricsSampler sampler) =>
{
    var snapshot = sampler.Latest;
    return snapshot is null
        ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
        : Results.Json(snapshot, MonitorJsonContext.Default.Snapshot);
});

app.Logger.LogInformation("kgsm-monitor listening on unix:{Socket}", socketPath);
app.Run();
