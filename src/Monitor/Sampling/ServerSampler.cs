using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Monitor.Model;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Owns the per-server watch-list and turns it into <see cref="ServerMetrics"/>.
/// <para>
/// Two cadences, deliberately separate:
/// <list type="bullet">
/// <item><b>Resync (slow, this background loop):</b> periodically runs KGSM's
/// <c>instances list --detailed</c> — a <em>process spawn</em>, the exact cost the
/// metrics path avoids — to refresh the authoritative instance list. Builds a fresh
/// immutable dictionary and swaps the reference (conflation, same pattern as the host
/// frame's <c>volatile</c> latest).</item>
/// <item><b>Sample (fast, the host tick):</b> <see cref="MetricsSampler"/> calls
/// <see cref="Sample"/> once per tick; it reads the current list reference and reads
/// each server's cgroup counters (cheap kernel files). No process spawn, no lock.</item>
/// </list>
/// The list is the source of truth; in Slice 2b KGSM socket events become the
/// low-latency delta on top of this resync floor.
/// </para>
/// </summary>
public sealed class ServerSampler(
    ILogger<ServerSampler> logger,
    MonitorOptions options,
    IInstanceService instances) : BackgroundService
{
    private static readonly IReadOnlyDictionary<string, Instance> Empty =
        new Dictionary<string, Instance>();

    private readonly CgroupSampler _cgroup = new();

    // Swapped wholesale by the resync loop, read by the sampling tick. Reference
    // assignment is atomic; readers never see a torn mid-resync list.
    private volatile IReadOnlyDictionary<string, Instance> _watch = Empty;

    /// <summary>
    /// Read every addressable server's cgroup counters for the current watch-list.
    /// Called on the host sampling thread; returns an empty array until the first
    /// resync lands.
    /// </summary>
    public ServerMetrics[] Sample() => _cgroup.Sample(_watch);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Prime the watch-list before the first metrics tick so the opening frames
        // already carry servers (best-effort: a slow first KGSM bootstrap may leave
        // the first frame or two server-less).
        Resync();

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.ServerResyncMs));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                Resync();
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private void Resync()
    {
        try
        {
            var all = instances.GetAll();
            _watch = all.Count == 0 ? Empty : all;
            logger.LogDebug("server resync: {Count} instance(s) known", all.Count);
        }
        catch (Exception ex)
        {
            // Keep the previous watch-list rather than blanking servers on a transient
            // KGSM hiccup (process spawn failure, timeout, malformed output).
            logger.LogWarning(ex, "server resync failed; keeping previous watch-list");
        }
    }
}
