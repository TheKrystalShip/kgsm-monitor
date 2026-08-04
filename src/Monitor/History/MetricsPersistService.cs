using TheKrystalShip.KGSM.Monitor.Contracts;
using TheKrystalShip.KGSM.Monitor.Sampling;

namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// Persists the latest snapshot to the history store on a slow cadence
/// (<c>Monitor__PersistMs</c>, default 15s — decoupled from the 1&#160;Hz sample tick, matching
/// the row density the metrics history was designed around). Reads <see cref="MetricsSampler.Latest"/>
/// (never a second scrape) and writes one batched transaction per tick.
/// <para><b>Honest gaps:</b> no frame yet (monitor warming) or a null metric field (io/net/disk not
/// accounted) produces <em>no row</em> — never a zero, never a carried-forward stale value. The row
/// timestamp is the frame's own <see cref="Snapshot.Ts"/>, so a conflated stale frame re-flushes the
/// same key (idempotent) rather than fabricating a fresh point.</para>
/// </summary>
public sealed class MetricsPersistService : BackgroundService
{
    private readonly MetricsSampler _sampler;
    private readonly HistoryStore _store;
    private readonly MonitorOptions _options;
    private readonly ILogger<MetricsPersistService> _logger;

    public MetricsPersistService(
        MetricsSampler sampler, HistoryStore store, MonitorOptions options, ILogger<MetricsPersistService> logger)
    {
        _sampler = sampler;
        _store = store;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "metrics persist: started (interval={IntervalMs}ms, db={Db}, hostId={HostId})",
            _options.PersistMs, _options.HistoryDbPath, _options.HostId);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.PersistMs));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    Snapshot? snap = _sampler.Latest;
                    if (snap is null)
                    {
                        _logger.LogDebug("metrics persist: no frame yet (warming), skipping");
                        continue;
                    }

                    var rows = new List<HistoryRow>();
                    MapHostMetrics(rows, _options.HostId, snap, snap.Ts);
                    foreach (ServerMetrics sm in snap.Servers)
                        MapServerMetrics(rows, sm, snap.Ts);

                    if (rows.Count > 0)
                    {
                        await _store.WriteSamplesAsync(rows, stoppingToken).ConfigureAwait(false);
                        _logger.LogDebug("metrics persist: wrote {Count} rows (ts={Ts})", rows.Count, snap.Ts);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "metrics persist: tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    // The canonical metric-name vocabulary the SPA's series keys depend on. Null fields (io/net/disk
    // not accounted) write NO row — honest gap, never a fabricated 0.
    internal static void MapServerMetrics(List<HistoryRow> rows, ServerMetrics sm, long ts)
    {
        rows.Add(new HistoryRow("server", sm.Id, "cpuPctCore", ts, Math.Round(sm.CpuPctCore, 1)));
        rows.Add(new HistoryRow("server", sm.Id, "memBytes", ts, sm.MemBytes));
        if (sm.IoReadBps is { } ioR)
            rows.Add(new HistoryRow("server", sm.Id, "ioReadBps", ts, ioR));
        if (sm.IoWriteBps is { } ioW)
            rows.Add(new HistoryRow("server", sm.Id, "ioWriteBps", ts, ioW));
        rows.Add(new HistoryRow("server", sm.Id, "pids", ts, sm.Pids));
        if (sm.DiskBytes is { } disk)
            rows.Add(new HistoryRow("server", sm.Id, "diskBytes", ts, disk));
        if (sm.RxBps is { } rx)
            rows.Add(new HistoryRow("server", sm.Id, "rxBps", ts, rx));
        if (sm.TxBps is { } tx)
            rows.Add(new HistoryRow("server", sm.Id, "txBps", ts, tx));
    }

    internal static void MapHostMetrics(List<HistoryRow> rows, string hostId, Snapshot s, long ts)
    {
        rows.Add(new HistoryRow("host", hostId, "cpuTotalPct", ts, Math.Round(s.Cpu.TotalPct, 1)));
        rows.Add(new HistoryRow("host", hostId, "memUsedKb", ts, s.Mem.UsedKb));
        rows.Add(new HistoryRow("host", hostId, "memTotalKb", ts, s.Mem.TotalKb));
        rows.Add(new HistoryRow("host", hostId, "memAvailableKb", ts, s.Mem.AvailableKb));
        rows.Add(new HistoryRow("host", hostId, "swapUsedKb", ts, s.Mem.SwapUsedKb));
        rows.Add(new HistoryRow("host", hostId, "loadOne", ts, s.Cpu.Load.One));
        rows.Add(new HistoryRow("host", hostId, "loadFive", ts, s.Cpu.Load.Five));
        rows.Add(new HistoryRow("host", hostId, "loadFifteen", ts, s.Cpu.Load.Fifteen));

        if (s.Disk.Io is { } io)
        {
            rows.Add(new HistoryRow("host", hostId, "diskReadBps", ts, io.ReadBps));
            rows.Add(new HistoryRow("host", hostId, "diskWriteBps", ts, io.WriteBps));
        }
    }
}
