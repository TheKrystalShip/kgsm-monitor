namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// Periodic rollup + retention maintenance, shared across both history stores. Rolls up complete raw
/// metrics buckets into the rollup tier, prunes expired rows from the metrics store's both tiers,
/// reclaims disk via incremental vacuum, and — when event history is wired — prunes expired rows from
/// the event store in the same pass (events carry no rollup tier: discrete facts, not a sampled
/// series, so pruning is their only retention step). Runs once at startup (catch-up after downtime —
/// gaps are honest, not backfilled) then on the <c>KGSM_MONITOR_MAINT_MS</c> timer (default 60s).
/// </summary>
/// <remarks>
/// <see cref="EventHistoryStore"/> is an <b>optional</b> constructor parameter: this service is
/// registered whenever either history feature is enabled (metrics and/or events — see
/// <c>Program.cs</c>), and the two are independently toggleable, so either store singleton may be
/// absent from the container. The built-in DI container passes <see langword="null"/> for an
/// unregistered optional-with-default-value parameter rather than throwing.
/// </remarks>
public sealed class MetricsMaintenanceService : BackgroundService
{
    private readonly HistoryStore? _store;
    private readonly EventHistoryStore? _eventStore;
    private readonly MonitorOptions _options;
    private readonly ILogger<MetricsMaintenanceService> _logger;

    public MetricsMaintenanceService(
        MonitorOptions options,
        ILogger<MetricsMaintenanceService> logger,
        HistoryStore? store = null,
        EventHistoryStore? eventStore = null)
    {
        _store = store;
        _eventStore = eventStore;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "history maintenance: started (interval={IntervalMs}ms, raw={RawH}h, rollup={RollD}d, step={StepM}min, events={EventsOn}, eventRetention={EventRetD}d)",
            _options.MaintenanceMs, _options.RawRetentionHours, _options.RollupRetentionDays, _options.RollupStepMin,
            _eventStore is not null, _options.EventRetentionDays);

        // Catch-up pass on startup (downtime = honest gaps, not backfilled).
        try { await RunMaintenanceAsync(stoppingToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "metrics maintenance: startup pass failed");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.MaintenanceMs));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await RunMaintenanceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "metrics maintenance: tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    private async Task RunMaintenanceAsync(CancellationToken ct)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (_store is not null)
        {
            await _store.RollupAsync(_options.RollupStepMin, nowMs, ct).ConfigureAwait(false);

            long rawCutoff = nowMs - (_options.RawRetentionHours * 3_600_000L);
            await _store.PruneRawAsync(rawCutoff, ct).ConfigureAwait(false);

            long rollupCutoff = nowMs - (_options.RollupRetentionDays * 86_400_000L);
            await _store.PruneRollupsAsync(rollupCutoff, ct).ConfigureAwait(false);

            await _store.VacuumAsync(ct).ConfigureAwait(false);
        }

        if (_eventStore is not null && _options.EventHistoryEnabled)
        {
            long eventCutoff = nowMs - (_options.EventRetentionDays * 86_400_000L);
            await _eventStore.PruneOlderThanAsync(eventCutoff, ct).ConfigureAwait(false);
        }
    }
}
