using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;
using TheKrystalShip.KGSM.Monitor.Model;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Owns the per-server watch-list and turns it into <see cref="ServerMetrics"/>.
/// <para>
/// Two cadences, deliberately separate:
/// <list type="bullet">
/// <item><b>Resync (slow, off the metrics tick):</b> periodically runs KGSM's
/// <c>instances list --detailed</c> — a <em>process spawn</em>, the exact cost the
/// metrics path avoids — to refresh the authoritative instance list. Builds a fresh
/// immutable dictionary and swaps the reference (conflation, same pattern as the host
/// frame's <c>volatile</c> latest).</item>
/// <item><b>Sample (fast, the host tick):</b> <see cref="MetricsSampler"/> calls
/// <see cref="Sample"/> once per tick; it reads the current list reference and reads each
/// server's counters from cheap kernel files — cgroup files for systemd/container servers
/// (<see cref="CgroupSampler"/>) and the <c>/proc</c> process tree for native-standalone
/// servers (<see cref="ProcTreeSampler"/>, Slice 3). No process spawn, no lock.</item>
/// </list>
/// </para>
/// <para>
/// <b>Slice 2b — event-driven delta (the "watch" half of list+watch):</b> KGSM lifecycle
/// events (<c>instance_started/stopped/removed/uninstalled</c>) arrive on the
/// monitor-owned <see cref="MonitorOptions.KgsmSocketPath"/> socket (KGSM connects via
/// <c>socat</c>). Each event simply <em>nudges</em> an immediate resync rather than
/// mutating the list directly: the event payload carries only the instance name, not the
/// cgroup-resolution inputs (compose-file / pid-file / systemd unit), so a partial "add"
/// would need a lookup anyway — re-listing via the proven <see cref="IInstanceService.GetAll"/>
/// keeps the watch-list <b>single-writer</b> (no lock on the volatile swap) and self-heals
/// on the best-effort event channel. The periodic resync stays the floor; events only cut
/// the latency from "up to <see cref="MonitorOptions.ServerResyncMs"/>" to "sub-second".
/// </para>
/// </summary>
public sealed class ServerSampler(
    ILogger<ServerSampler> logger,
    MonitorOptions options,
    IInstanceService instances,
    IEventService? events = null) : BackgroundService
{
    private static readonly IReadOnlyDictionary<string, Instance> Empty =
        new Dictionary<string, Instance>();

    private readonly CgroupSampler _cgroup = new();

    // Slice 3: the standalone-native fallback. Servers with no cgroup (LifecycleManager
    // Standalone, no compose_file) are invisible to _cgroup; this reads their /proc process
    // tree instead. It owns a disjoint set of servers, so the two outputs simply concatenate.
    private readonly ProcTreeSampler _procTree = new();

    // Coalescing resync signal. RequestResync() releases it; the single drain loop in
    // ExecuteAsync waits on it and is the ONLY writer of _watch. Max count 1, so a burst
    // of events (or an event landing mid-resync) collapses to a single pending resync —
    // the SemaphoreFullException is the coalesce, not an error.
    private readonly SemaphoreSlim _resyncSignal = new(0, 1);

    // Swapped wholesale by the resync loop, read by the sampling tick. Reference
    // assignment is atomic; readers never see a torn mid-resync list.
    private volatile IReadOnlyDictionary<string, Instance> _watch = Empty;

    /// <summary>
    /// Read every server's metrics for the current watch-list: cgroup counters for the
    /// cgroup-addressable servers (systemd + container) and the <c>/proc</c> process tree for
    /// native-standalone servers (Slice 3). The two samplers cover disjoint server kinds, so
    /// their results concatenate. Called on the host sampling thread; returns an empty array
    /// until the first resync lands.
    /// </summary>
    public ServerMetrics[] Sample()
    {
        var watch = _watch;
        var cgroup = _cgroup.Sample(watch);
        var native = _procTree.Sample(watch);
        return native.Length == 0 ? cgroup : [.. cgroup, .. native];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WireEvents();

        // Prime the watch-list before the first metrics tick so the opening frames
        // already carry servers (best-effort: a slow first KGSM bootstrap may leave
        // the first frame or two server-less).
        Resync();

        // Periodic floor: nudge a resync every ServerResyncMs regardless of events, so a
        // missed (best-effort) event still self-heals within one period.
        var periodic = RunPeriodicNudgeAsync(stoppingToken);

        try
        {
            // Single-writer drain loop: the ONLY place _watch is rewritten. Both the
            // periodic floor and KGSM events feed RequestResync(); serializing them here
            // is what lets the volatile swap stay lock-free.
            while (true)
            {
                await _resyncSignal.WaitAsync(stoppingToken).ConfigureAwait(false);
                Resync();
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }

        await periodic.ConfigureAwait(false);
    }

    private async Task RunPeriodicNudgeAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.ServerResyncMs));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                RequestResync();
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    /// <summary>
    /// Subscribe to the KGSM lifecycle events that change watch-list membership and start
    /// listening on the event socket. Each handler just nudges a resync (see the class
    /// remarks for why we re-list rather than apply a partial delta).
    /// </summary>
    private void WireEvents()
    {
        if (events is null || !options.EventsEnabled)
        {
            logger.LogInformation(
                "server events disabled; resync-only every {ResyncMs}ms", options.ServerResyncMs);
            return;
        }

        try
        {
            // The explicit type argument is load-bearing: EventService keys handlers by
            // typeof(T), so RegisterHandler<InstanceStartedData>(...) must name the concrete
            // event type (a method group typed to the EventDataBase base would key under the
            // base and never match a deserialized concrete event).
            events.RegisterHandler<InstanceStartedData>(OnLifecycleChange);
            events.RegisterHandler<InstanceStoppedData>(OnLifecycleChange);
            events.RegisterHandler<InstanceRemovedData>(OnLifecycleChange);
            events.RegisterHandler<InstanceUninstalledData>(OnLifecycleChange);

            // Binds the socket on a fire-and-forget task inside the lib; a bind failure
            // (perms/path) surfaces only as a background LogError there, NOT here — the
            // try/catch covers the disposal-race paths Initialize() itself can throw. Either
            // way per-server metrics keep working via the resync floor; a dead event socket
            // is silent (documented in PLAN §12).
            events.Initialize();

            logger.LogInformation(
                "server events: listening on {Socket} (resync floor {ResyncMs}ms)",
                options.KgsmSocketPath, options.ServerResyncMs);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "KGSM event listener init failed; falling back to resync-only");
        }
    }

    // One handler for all four events: each is a "watch-list may have changed" nudge. The
    // EventDataBase parameter binds to every concrete Func<TEvent, Task> via method-group
    // contravariance, so the four RegisterHandler calls share it.
    private Task OnLifecycleChange(EventDataBase data)
    {
        logger.LogDebug("KGSM event for {Instance}; requesting resync", data.InstanceName);
        RequestResync();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Ask the drain loop to resync. Coalesces: the signal caps at 1, so concurrent or
    /// rapid requests collapse to a single resync rather than a process-spawn storm.
    /// </summary>
    private void RequestResync()
    {
        try
        {
            _resyncSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // a resync is already pending; coalesce
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

    public override void Dispose()
    {
        _resyncSignal.Dispose();
        base.Dispose();
    }
}
