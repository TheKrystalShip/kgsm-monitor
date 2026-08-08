namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// Periodic rollup + retention maintenance for metrics history. Rolls up complete raw buckets into
/// the rollup tier, prunes expired rows from both tiers, and reclaims disk via incremental vacuum.
/// Runs once at startup (catch-up after downtime — gaps are honest, not backfilled) then on the
/// <c>Monitor__MaintenanceMs</c> timer (default 60s).
/// </summary>
/// <remarks>
/// The store is an optional constructor parameter because history is toggleable, so the singleton may
/// be absent from the container. The built-in DI container passes <see langword="null"/> for an
/// unregistered optional-with-default-value parameter rather than throwing.
/// </remarks>
public sealed class MetricsMaintenanceService : BackgroundService
{
    private readonly HistoryStore? _store;
    private readonly MonitorOptions _options;
    private readonly MaintenanceState _state;
    private readonly ILogger<MetricsMaintenanceService> _logger;

    public MetricsMaintenanceService(
        MonitorOptions options,
        MaintenanceState state,
        ILogger<MetricsMaintenanceService> logger,
        HistoryStore? store = null)
    {
        _store = store;
        _options = options;
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "history maintenance: started (interval={IntervalMs}ms, raw={RawH}h, rollup={RollD}d, step={StepM}min)",
            _options.MaintenanceMs, _options.RawRetentionHours, _options.RollupRetentionDays, _options.RollupStepMin);

        // Catch-up pass on startup (downtime = honest gaps, not backfilled).
        try { await RunMaintenanceAsync(stoppingToken).ConfigureAwait(false); _state.Record(true); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _state.Record(false);
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
                    // Recorded whether or not history is enabled: with the store absent the pass is a
                    // no-op that trivially succeeds, and reporting "never ran" for a daemon whose timer
                    // is ticking fine would send someone looking for a fault that isn't there.
                    _state.Record(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _state.Record(false);
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
    }
}
