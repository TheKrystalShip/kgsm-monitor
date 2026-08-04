using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// Indexes every KGSM engine event into <see cref="EventHistoryStore"/> via
/// <see cref="IEventService.RegisterRawHandler"/> — fires on every deserialized envelope, known or
/// unknown <c>EventType</c>, independent of (and never suppressing) the monitor's typed per-server
/// dispatch (<see cref="Sampling.ServerSampler"/>'s lifecycle handlers keep driving watch-list
/// resync unmodified). No background loop of its own: the work happens entirely inside the callback
/// the journal reader's loop invokes, so <see cref="ExecuteAsync"/> only logs startup and checks
/// retention layering.
/// <para>
/// It also records journal gaps, so a history missing a stretch of events says so rather than
/// reading as complete. Only this service can: it is the one component that knows the store the
/// history lives in.
/// </para>
/// </summary>
/// <remarks>
/// <b>Registration-order assumption (load-bearing, documented rather than solved with new
/// machinery):</b> the raw handler is registered in this class's <b>constructor</b>, not in
/// <c>StartAsync</c>/<see cref="ExecuteAsync"/>. The ASP.NET Core Generic Host resolves — i.e.
/// constructs — every registered <see cref="IHostedService"/> up front (via
/// <c>IEnumerable&lt;IHostedService&gt;</c>) before calling <c>StartAsync</c> on any of them. So this
/// constructor runs, and the raw handler is registered, strictly before
/// <see cref="Sampling.ServerSampler"/>'s <c>ExecuteAsync</c> can run — and it is that method, not
/// this one, that owns the single <see cref="IEventService.Initialize"/> call that starts the
/// journal read loop. This service only ever registers a handler on the shared singleton.
/// </remarks>
public sealed class EventPersistService : BackgroundService
{
    /// <summary>The engine config key holding how many days of journal segments survive pruning.</summary>
    private const string JournalRetentionKey = "event_journal_retention_days";

    private readonly EventHistoryStore _store;
    private readonly IConfigService _config;
    private readonly MonitorOptions _options;
    private readonly ILogger<EventPersistService> _logger;

    public EventPersistService(
        IEventService events, EventHistoryStore store, IConfigService config, MonitorOptions options,
        ILogger<EventPersistService> logger)
    {
        _store = store;
        _config = config;
        _options = options;
        _logger = logger;

        events.RegisterRawHandler(OnRawEventAsync);
        events.RegisterGapHandler(OnGapAsync);
    }

    // A gap that fails to record is worse than one that fails to be read: the history would then
    // claim coverage it does not have. Log it loudly so the caveat survives even when the row does
    // not.
    private async Task OnGapAsync(EventJournalGap gap)
    {
        _logger.LogWarning(
            "event history: journal gap at {Segment}+{Offset} ({Reason}); events before {Resumed} are missing from this store",
            gap.LostSegment, gap.LostOffset, gap.Reason, gap.ResumedAtSegment ?? "(nothing)");

        try
        {
            await _store.RecordGapAsync(gap).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "event history: failed to record the journal gap at {Segment}", gap.LostSegment);
        }
    }

    // Swallow + log: a persist failure (locked db, disk full, malformed envelope) must never take
    // down the event socket's read loop or block other raw/typed handlers — mirrors
    // MetricsPersistService's tick try/catch.
    private async Task OnRawEventAsync(EventWrapper wrapper)
    {
        try
        {
            await _store.AppendAsync(wrapper).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "event history: failed to persist {EventType}", wrapper.EventType);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield before the config read below: BackgroundService runs ExecuteAsync inline from
        // StartAsync, so a synchronous kgsm exec here would stall host startup.
        await Task.Yield();

        _logger.LogInformation(
            "event history: indexing engine events via raw handler (db={Db}, retention={RetentionDays}d)",
            _options.EventsDbPath, _options.EventRetentionDays);

        CheckRetentionLayering();

        // Nothing to loop after that: the raw handler registered in the constructor does the actual
        // work, invoked directly by the journal reader's read loop (owned by ServerSampler).
    }

    /// <summary>
    /// Report whether the journal still reaches back as far as this index claims to.
    /// </summary>
    /// <remarks>
    /// The index is derived from the journal, so journal retention must be <b>≥</b> index retention.
    /// Configured the other way round, the store keeps serving rows whose segments have been pruned
    /// — correct until something rebuilds, at which point history silently shortens to the journal's
    /// window. Reporting it at startup turns a latent data-loss configuration into a visible one.
    /// <para>
    /// The monitor reports and does not correct: retention lives in the engine's config, the engine
    /// prunes on age alone and never consults a consumer, and a leaf quietly rewriting the engine's
    /// configuration to suit itself would invert that ownership. An unreadable value is logged as
    /// unknown, never assumed to be fine.
    /// </para>
    /// </remarks>
    internal void CheckRetentionLayering()
    {
        string? raw;
        try
        {
            raw = _config.Get(JournalRetentionKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "event history: could not read {Key} from kgsm; journal coverage is unverified", JournalRetentionKey);
            return;
        }

        if (!int.TryParse(raw, out int journalDays))
        {
            _logger.LogWarning(
                "event history: kgsm reported no usable {Key} (got {Raw}); journal coverage is unverified",
                JournalRetentionKey, string.IsNullOrEmpty(raw) ? "nothing" : raw);
            return;
        }

        if (journalDays < _options.EventRetentionDays)
        {
            _logger.LogError(
                "event history: journal retention ({JournalDays}d) is shorter than index retention ({IndexDays}d) — "
                + "a rebuild would return only {JournalDays}d of history. Raise {Key} in kgsm's config, or lower "
                + "KGSM_MONITOR_EVENT_RETENTION_DAYS to match",
                journalDays, _options.EventRetentionDays, journalDays, JournalRetentionKey);
        }
        else
        {
            _logger.LogInformation(
                "event history: journal retention {JournalDays}d covers index retention {IndexDays}d",
                journalDays, _options.EventRetentionDays);
        }
    }
}
