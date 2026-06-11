using TheKrystalShip.KGSM.Monitor.Model;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// The single host sampler. Ticks on a fixed cadence, reads the kernel counters,
/// computes rates against the previous sample, and publishes the latest snapshot.
/// It is intentionally <em>consumer-agnostic</em>: it knows nothing about the HTTP
/// endpoint or who scrapes it. <see cref="Latest"/> is read by the endpoint; the
/// write is a single reference swap (conflation — latest always wins, stale frames
/// are never queued).
/// </summary>
public sealed class MetricsSampler(ILogger<MetricsSampler> logger, MonitorOptions options) : BackgroundService
{
    private readonly int _intervalMs = options.IntervalMs;

    // Stateful sources hold the previous counters needed to derive rates.
    private readonly CpuSource _cpu = new();
    private readonly NetworkSource _net = new(options.IfaceDenyPrefixes);
    private readonly DiskSource _disk = new(options.MountFsDeny);

    private volatile Snapshot? _latest;

    /// <summary>The most recent snapshot, or null until the first tick completes.</summary>
    public Snapshot? Latest => _latest;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Prime the delta sources so the first published frame already carries rates.
        _cpu.Sample();
        _net.Sample();
        _disk.Sample();

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_intervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    _latest = Build();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "metrics sample failed; keeping previous frame");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private Snapshot Build()
    {
        var (cpuTotal, perCore) = _cpu.Sample();
        var mem = MemorySource.Read();
        var net = _net.Sample();
        var disk = _disk.Sample();
        var (load, uptime, host) = SystemSource.Read();

        return new Snapshot(
            Ts: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IntervalMs: _intervalMs,
            Hostname: host,
            UptimeSec: uptime,
            Cpu: new CpuMetrics(cpuTotal, perCore, load),
            Mem: mem,
            Disk: disk,
            Net: net);
    }
}
