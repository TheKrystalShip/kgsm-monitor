namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// Periodic rollup + retention maintenance for the history store. Rolls up complete raw buckets into
/// the rollup tier, prunes expired rows from both tiers, and reclaims disk via incremental vacuum.
/// Runs once at startup (catch-up after downtime — gaps are honest, not backfilled) then on the
/// <c>KGSM_MONITOR_MAINT_MS</c> timer (default 60s).
/// </summary>
public sealed class MetricsMaintenanceService : BackgroundService
{
    private readonly HistoryStore _store;
    private readonly MonitorOptions _options;
    private readonly ILogger<MetricsMaintenanceService> _logger;

    public MetricsMaintenanceService(
        HistoryStore store, MonitorOptions options, ILogger<MetricsMaintenanceService> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "metrics maintenance: started (interval={IntervalMs}ms, raw={RawH}h, rollup={RollD}d, step={StepM}min)",
            _options.MaintenanceMs, _options.RawRetentionHours, _options.RollupRetentionDays, _options.RollupStepMin);

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

        await _store.RollupAsync(_options.RollupStepMin, nowMs, ct).ConfigureAwait(false);

        long rawCutoff = nowMs - (_options.RawRetentionHours * 3_600_000L);
        await _store.PruneRawAsync(rawCutoff, ct).ConfigureAwait(false);

        long rollupCutoff = nowMs - (_options.RollupRetentionDays * 86_400_000L);
        await _store.PruneRollupsAsync(rollupCutoff, ct).ConfigureAwait(false);

        await _store.VacuumAsync(ct).ConfigureAwait(false);
    }
}
