using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Monitor.Contracts;
using TheKrystalShip.KGSM.Monitor.History;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// The metrics-history store (raw ADO SQLite) and the snapshot→row mapping. Each test uses its own
/// temp DB file so they don't collide, and disposes the store (closing the connection) before deleting.
/// </summary>
public class HistoryStoreTests
{
    private static (HistoryStore store, MonitorOptions opts, string db) NewStore()
    {
        string db = Path.Combine(Path.GetTempPath(), $"kgsm-monitor-hist-{Guid.NewGuid():N}.db");
        var opts = new MonitorOptions
        {
            HistoryDbPath = db,
            PersistMs = 15_000,
            RawRetentionHours = 24,
            RollupStepMin = 5,
            RollupRetentionDays = 30,
        };
        return (new HistoryStore(opts, NullLogger<HistoryStore>.Instance), opts, db);
    }

    private static void Cleanup(HistoryStore store, string db)
    {
        store.Dispose();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            File.Delete(db + suffix);
    }

    [Fact]
    public async Task Raw_round_trip_groups_rows_into_series()
    {
        var (store, _, db) = NewStore();
        try
        {
            // Points a few seconds in the past so both fall inside the [now-1h, now] window.
            long t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5000;
            await store.WriteSamplesAsync(
            [
                new HistoryRow("host", "h1", "cpuTotalPct", t0, 10.0),
                new HistoryRow("host", "h1", "cpuTotalPct", t0 + 1000, 20.0),
                new HistoryRow("host", "h1", "memUsedKb", t0, 500),
            ]);

            MetricsHistoryResponse resp = await store.QueryHistoryAsync("host", "h1", "1h");

            Assert.Equal("h1", resp.EntityId);
            Assert.Equal("host", resp.Kind);
            Assert.Equal("raw", resp.Tier);
            Assert.Equal(15, resp.Step); // PersistMs / 1000
            Assert.Equal(2, resp.Series["cpuTotalPct"].Count);
            Assert.Single(resp.Series["memUsedKb"]);
            Assert.Equal(10.0, resp.Series["cpuTotalPct"][0].Value);
            Assert.Null(resp.Series["cpuTotalPct"][0].Min); // raw points carry no min/max/n
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Duplicate_key_is_replaced_not_duplicated()
    {
        var (store, _, db) = NewStore();
        try
        {
            long t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await store.WriteSamplesAsync([new HistoryRow("host", "h1", "cpuTotalPct", t0, 10.0)]);
            await store.WriteSamplesAsync([new HistoryRow("host", "h1", "cpuTotalPct", t0, 99.0)]); // same PK

            MetricsHistoryResponse resp = await store.QueryHistoryAsync("host", "h1", "1h");
            Assert.Single(resp.Series["cpuTotalPct"]);
            Assert.Equal(99.0, resp.Series["cpuTotalPct"][0].Value);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Rollup_aggregates_closed_buckets_and_reads_on_wide_range()
    {
        var (store, opts, db) = NewStore();
        try
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long stepMs = opts.RollupStepMin * 60_000L;
            // Two points in a closed bucket well in the past (2 buckets ago), same metric.
            long baseTs = ((nowMs / stepMs) * stepMs) - (2 * stepMs);
            await store.WriteSamplesAsync(
            [
                new HistoryRow("host", "h1", "cpuTotalPct", baseTs + 1000, 10.0),
                new HistoryRow("host", "h1", "cpuTotalPct", baseTs + 2000, 30.0),
            ]);

            await store.RollupAsync(opts.RollupStepMin, nowMs);

            // A 7d range is > raw retention (24h) → rollup tier.
            MetricsHistoryResponse resp = await store.QueryHistoryAsync("host", "h1", "7d");
            Assert.Equal("rollup", resp.Tier);
            Assert.Equal(opts.RollupStepMin * 60, resp.Step);
            MetricsHistoryPoint pt = Assert.Single(resp.Series["cpuTotalPct"]);
            Assert.Equal(20.0, pt.Value);   // avg
            Assert.Equal(10.0, pt.Min);
            Assert.Equal(30.0, pt.Max);
            Assert.Equal(2, pt.N);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Prune_removes_old_raw_rows()
    {
        var (store, _, db) = NewStore();
        try
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long old = nowMs - (long)TimeSpan.FromDays(2).TotalMilliseconds;
            await store.WriteSamplesAsync(
            [
                new HistoryRow("host", "h1", "cpuTotalPct", old, 10.0),
                new HistoryRow("host", "h1", "cpuTotalPct", nowMs, 20.0),
            ]);

            int deleted = await store.PruneRawAsync(nowMs - (long)TimeSpan.FromHours(24).TotalMilliseconds);
            Assert.Equal(1, deleted);

            MetricsHistoryResponse resp = await store.QueryHistoryAsync("host", "h1", "1h");
            Assert.Single(resp.Series["cpuTotalPct"]); // only the recent one survives
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Empty_store_yields_empty_series_never_fabricated()
    {
        var (store, _, db) = NewStore();
        try
        {
            MetricsHistoryResponse resp = await store.QueryHistoryAsync("server", "does-not-exist", "1h");
            Assert.Empty(resp.Series);
            Assert.Equal("raw", resp.Tier);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public void Server_mapping_omits_null_io_net_disk_rows()
    {
        var rows = new List<HistoryRow>();
        var sm = new ServerMetrics(
            Id: "factorio-test", Name: "factorio-test", Kind: "native",
            CpuPctCore: 12.34, MemBytes: 1000,
            IoReadBps: null, IoWriteBps: null, Pids: 7, DiskBytes: null, RxBps: null, TxBps: null);

        MetricsPersistService.MapServerMetrics(rows, sm, 123);

        // Only the always-present metrics: cpuPctCore, memBytes, pids (io/disk/net are null → no row).
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.Metric == "cpuPctCore" && r.Value == 12.3); // rounded to 1 dp
        Assert.Contains(rows, r => r.Metric == "memBytes");
        Assert.Contains(rows, r => r.Metric == "pids");
        Assert.DoesNotContain(rows, r => r.Metric == "ioReadBps");
        Assert.DoesNotContain(rows, r => r.Metric == "rxBps");
        Assert.DoesNotContain(rows, r => r.Metric == "diskBytes");
    }

    [Fact]
    public void Server_mapping_includes_metered_io_net_disk_rows()
    {
        var rows = new List<HistoryRow>();
        var sm = new ServerMetrics(
            Id: "s1", Name: "s1", Kind: "container",
            CpuPctCore: 5, MemBytes: 2000,
            IoReadBps: 100, IoWriteBps: 200, Pids: 3, DiskBytes: 4096, RxBps: 10, TxBps: 20);

        MetricsPersistService.MapServerMetrics(rows, sm, 123);

        Assert.Equal(8, rows.Count); // all fields present
        Assert.Contains(rows, r => r.Metric == "ioReadBps" && r.Value == 100);
        Assert.Contains(rows, r => r.Metric == "txBps" && r.Value == 20);
        Assert.Contains(rows, r => r.Metric == "diskBytes" && r.Value == 4096);
    }
}
