using TheKrystalShip.KGSM.Monitor.Contracts;
using TheKrystalShip.KGSM.Monitor.Thresholds;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// The single host sampler. Ticks on a fixed cadence, reads the kernel counters,
/// computes rates against the previous sample, and publishes the latest snapshot.
/// It is intentionally <em>consumer-agnostic</em>: it knows nothing about the HTTP
/// endpoint or who scrapes it. <see cref="Latest"/> is read by the endpoint; the
/// write is a single reference swap (conflation — latest always wins, stale frames
/// are never queued).
/// </summary>
public sealed class MetricsSampler(
    ILogger<MetricsSampler> logger,
    MonitorOptions options,
    ServerSampler? servers = null,
    LeafSampler? leaves = null,
    ConditionEvaluator? conditions = null) : BackgroundService
{
    private readonly int _intervalMs = options.IntervalMs;

    // Stateful sources hold the previous counters needed to derive rates.
    private readonly CpuSource _cpu = new();
    private readonly NetworkSource _net = new(options.IfaceDenyPrefixes);
    private readonly DiskSource _disk = new(options.MountFsDeny);
    private readonly SensorSource _sensors = new();

    // Static CPU identity — read once (it doesn't change) and reused on every frame.
    private readonly CpuInfo _cpuInfo = CpuInfoSource.Read();

    // Per-server cgroup sampler — null when KGSM integration is unconfigured (the
    // monitor then runs host-only and the servers array is always empty).
    private readonly ServerSampler? _servers = servers;

    // Per-leaf cgroup sampler — null when leaf sampling is turned off (the leaves array is then always
    // empty). Independent of the server sampler above: it needs no KGSM, so a host with no game servers
    // still reports on the daemons running there.
    private readonly LeafSampler? _leaves = leaves;

    // Threshold evaluation — null when switched off, and then every frame carries no conditions. It runs
    // HERE, on the sample tick, rather than in whatever scrapes the socket: a rule asks whether a value
    // held for a length of time, and this loop is the only thing that sees every value.
    private readonly ConditionEvaluator? _conditions = conditions;

    // The rule set the evaluator is run against. The built-in baseline until an operator applies their own.
    private readonly MetricsThresholdPolicy _policy = MetricsThresholdPolicy.Default;

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

    internal Snapshot Build()
    {
        var (cpuTotal, perCore) = _cpu.Sample();
        var mem = MemorySource.Read();
        var net = _net.Sample();
        var disk = _disk.Sample();
        var (load, uptime, host) = SystemSource.Read();

        var frame = new Snapshot(
            Ts: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IntervalMs: _intervalMs,
            Hostname: host,
            UptimeSec: uptime,
            Cpu: new CpuMetrics(cpuTotal, perCore, load, _cpuInfo),
            Mem: mem,
            Disk: disk,
            Net: net,
            Sensors: _sensors.Sample(),
            Servers: _servers?.Sample() ?? [],
            Leaves: _leaves?.Sample() ?? [],
            Conditions: []);

        // The rules are evaluated against the frame that is about to be published, and the verdict is folded
        // back into it — so a condition and the reading that produced it are never a tick apart, and a
        // consumer reading one frame sees a self-consistent answer.
        return _conditions is null
            ? frame
            : frame with { Conditions = _conditions.Evaluate(_policy, frame) };
    }
}
