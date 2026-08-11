using TheKrystalShip.KGSM.Monitor.Thresholds;

namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// Writes threshold episodes to the history store as they open and close.
/// </summary>
/// <remarks>
/// <para><b>Why a loop rather than a call from the evaluator.</b> Evaluation runs on the sample tick, and
/// persisting is disk I/O — a slow write there would delay a sample, which is the one thing the sampling
/// path must never do. The evaluator queues its transitions instead and this drains them, so the two are
/// only coupled by a queue. Draining rather than reading means a tick this loop misses is not a
/// transition it loses.</para>
/// <para><b>Cadence.</b> Faster than the metrics persist loop, because these are transitions rather than
/// samples: an episode opens once, and whoever is reading them off this store is reconciling against a
/// live feed that already knows. A second's lag is invisible; a minute's would show up as an audit row
/// arriving well after the alert it belongs to.</para>
/// <para><b>Only wired when history is on.</b> With no store there is nowhere to write, so episodes are
/// simply not recorded — alerts still work, since those come off the live frame. That is an honest
/// degrade and it is logged once, because "this host keeps no record of what fired" is exactly the sort
/// of thing somebody finds out at the worst moment otherwise.</para>
/// </remarks>
public sealed class EpisodeRecorder(
    HistoryStore store,
    ConditionEvaluator evaluator,
    MonitorOptions options,
    ILogger<EpisodeRecorder> logger) : BackgroundService
{
    private static readonly TimeSpan DrainInterval = TimeSpan.FromSeconds(1);

    /// <summary>This daemon's leaf id, stamped on every episode it records. It is what a consumer turns
    /// into "kgsm-monitor established this", and it is the daemon's own name for itself rather than
    /// something the consumer assumes.</summary>
    private const string Producer = "monitor";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("threshold episodes: recording to {Db}", options.HistoryDbPath);

        using var timer = new PeriodicTimer(DrainInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await DrainAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A failed write must not take the loop down: the next drain carries on, and the live
                    // conditions on the frame are unaffected either way.
                    logger.LogError(ex, "threshold episodes: drain failed");
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        IReadOnlyList<EpisodeTransition> transitions = evaluator.DrainTransitions();
        if (transitions.Count == 0) return;

        foreach (EpisodeTransition t in transitions)
        {
            if (t.ClosedTs is { } closedTs)
            {
                await store.CloseEpisodeAsync(t.EpisodeId, closedTs, t.Value, t.PeakValue, t.Band, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await store.OpenEpisodeAsync(new EpisodeOpen(
                    EpisodeId: t.EpisodeId,
                    RuleKey: t.RuleKey,
                    Metric: t.Metric,
                    Scope: t.Scope,
                    Ref: t.Ref,
                    ServerId: t.ServerId,
                    OpenedTs: t.OpenedTs,
                    Band: t.Band,
                    Value: t.Value,
                    Threshold: t.Threshold,
                    Producer: Producer), ct).ConfigureAwait(false);
            }
        }

        logger.LogDebug("threshold episodes: recorded {Count} transition(s)", transitions.Count);
    }
}
