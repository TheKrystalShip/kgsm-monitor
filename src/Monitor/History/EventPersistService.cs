using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// Persists every KGSM engine event to <see cref="EventHistoryStore"/> via
/// <see cref="IEventService.RegisterRawHandler"/> — fires on every deserialized envelope, known or
/// unknown <c>EventType</c>, independent of (and never suppressing) the monitor's typed per-server
/// dispatch (<see cref="Sampling.ServerSampler"/>'s lifecycle handlers keep driving watch-list
/// resync unmodified). No background loop of its own: the work happens entirely inside the callback
/// the event socket's read loop invokes, so <see cref="ExecuteAsync"/> just logs startup and returns.
/// </summary>
/// <remarks>
/// <b>Registration-order assumption (load-bearing, documented rather than solved with new
/// machinery):</b> the raw handler is registered in this class's <b>constructor</b>, not in
/// <c>StartAsync</c>/<see cref="ExecuteAsync"/>. The ASP.NET Core Generic Host resolves — i.e.
/// constructs — every registered <see cref="IHostedService"/> up front (via
/// <c>IEnumerable&lt;IHostedService&gt;</c>) before calling <c>StartAsync</c> on any of them. So this
/// constructor runs, and the raw handler is registered, strictly before
/// <see cref="Sampling.ServerSampler"/>'s <c>ExecuteAsync</c> can run — and it is that method, not
/// this one, that owns the single <see cref="IEventService.Initialize"/> call binding the event
/// socket. This service deliberately never calls <c>Initialize()</c> itself (a second call would
/// double-bind the socket); it only ever registers a handler on the shared singleton.
/// </remarks>
public sealed class EventPersistService : BackgroundService
{
    private readonly EventHistoryStore _store;
    private readonly MonitorOptions _options;
    private readonly ILogger<EventPersistService> _logger;

    public EventPersistService(
        IEventService events, EventHistoryStore store, MonitorOptions options, ILogger<EventPersistService> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;

        events.RegisterRawHandler(OnRawEventAsync);
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

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "event history: persisting engine events via raw handler (db={Db}, retention={RetentionDays}d)",
            _options.EventsDbPath, _options.EventRetentionDays);

        // Nothing to loop: the raw handler registered in the constructor does the actual work,
        // invoked directly by the event socket's read loop (owned by ServerSampler). Returning a
        // completed task here just satisfies BackgroundService's contract.
        return Task.CompletedTask;
    }
}
