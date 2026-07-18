using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Events;
using TheKrystalShip.KGSM.Monitor.History;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// The event-history store (raw ADO SQLite). Each test uses its own temp DB file so they don't
/// collide, and disposes the store (closing the connection) before deleting.
/// </summary>
public class EventHistoryStoreTests
{
    private static (EventHistoryStore store, string db) NewStore()
    {
        string db = Path.Combine(Path.GetTempPath(), $"kgsm-monitor-events-{Guid.NewGuid():N}.db");
        var opts = new MonitorOptions { EventsDbPath = db };
        return (new EventHistoryStore(opts, NullLogger<EventHistoryStore>.Instance), db);
    }

    private static void Cleanup(EventHistoryStore store, string db)
    {
        store.Dispose();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            File.Delete(db + suffix);
    }

    private static EventWrapper Wrapper(
        string eventType = "instance_started",
        string dataJson = """{"InstanceName":"factorio-test"}""",
        DateTimeOffset? timestamp = null,
        string? actor = "heisen",
        string? origin = "ui",
        string? hostname = "hotrod") =>
        new()
        {
            EventType = eventType,
            Data = JsonSerializer.Deserialize<JsonElement>(dataJson),
            Timestamp = timestamp ?? new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero),
            Actor = actor,
            Origin = origin,
            Hostname = hostname,
        };

    [Fact]
    public async Task Append_then_query_round_trips_a_row()
    {
        var (store, db) = NewStore();
        try
        {
            EventWrapper w = Wrapper();
            await store.AppendAsync(w);

            EventHistoryResponse resp = await store.QueryEventsAsync(
                instance: null, type: null, sinceMs: null, untilMs: null,
                beforeTs: null, beforeId: null, limit: 200);

            EventHistoryItem item = Assert.Single(resp.Events);
            Assert.Equal(AuditId.ForEvent(w), item.Id);
            Assert.Equal("instance_started", item.Type);
            Assert.Equal("factorio-test", item.Instance);
            Assert.Equal("heisen", item.Actor);
            Assert.Equal("ui", item.Origin);
            Assert.NotNull(item.Data);
            Assert.Equal("factorio-test", item.Data!.Value.GetProperty("InstanceName").GetString());
            Assert.Equal(1, resp.Count);
            // A partial (non-full) page carries no cursor — "no more rows".
            Assert.Null(resp.NextCursorTs);
            Assert.Null(resp.NextCursorId);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Null_actor_origin_and_instance_persist_as_null_never_fabricated()
    {
        var (store, db) = NewStore();
        try
        {
            // A host/global event: no InstanceName in Data, no Actor/Origin (bare CLI, no enrichment).
            var w = Wrapper(dataJson: "{}", actor: null, origin: null);
            await store.AppendAsync(w);

            EventHistoryResponse resp = await store.QueryEventsAsync(
                instance: null, type: null, sinceMs: null, untilMs: null,
                beforeTs: null, beforeId: null, limit: 200);

            EventHistoryItem item = Assert.Single(resp.Events);
            Assert.Null(item.Instance);
            Assert.Null(item.Actor);
            Assert.Null(item.Origin);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Missing_timestamp_falls_back_to_receipt_time()
    {
        var (store, db) = NewStore();
        try
        {
            long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // Constructed directly (not via the Wrapper() helper, which defaults Timestamp to a
            // fixed date when the caller passes null) so this genuinely exercises a wrapper with no
            // Timestamp at all — the pre-enrichment-KGSM case the store must fall back for.
            var w = new EventWrapper
            {
                EventType = "instance_started",
                Data = JsonSerializer.Deserialize<JsonElement>("""{"InstanceName":"factorio-test"}"""),
                Timestamp = null,
                Hostname = "hotrod",
            };
            await store.AppendAsync(w);
            long after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            EventHistoryResponse resp = await store.QueryEventsAsync(
                instance: null, type: null, sinceMs: null, untilMs: null,
                beforeTs: null, beforeId: null, limit: 200);

            EventHistoryItem item = Assert.Single(resp.Events);
            long ts = item.Ts.ToUnixTimeMilliseconds();
            Assert.InRange(ts, before, after);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Duplicate_append_is_ignored_not_duplicated()
    {
        var (store, db) = NewStore();
        try
        {
            EventWrapper w = Wrapper();
            await store.AppendAsync(w);
            await store.AppendAsync(w); // identical content -> identical deterministic id

            EventHistoryResponse resp = await store.QueryEventsAsync(
                instance: null, type: null, sinceMs: null, untilMs: null,
                beforeTs: null, beforeId: null, limit: 200);

            Assert.Single(resp.Events);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Query_filters_by_instance_and_type_and_window()
    {
        var (store, db) = NewStore();
        try
        {
            var t0 = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
            await store.AppendAsync(Wrapper(
                eventType: "instance_started",
                dataJson: """{"InstanceName":"factorio-test"}""",
                timestamp: t0));
            await store.AppendAsync(Wrapper(
                eventType: "instance_stopped",
                dataJson: """{"InstanceName":"factorio-test"}""",
                timestamp: t0.AddSeconds(1)));
            await store.AppendAsync(Wrapper(
                eventType: "instance_started",
                dataJson: """{"InstanceName":"terraria-test"}""",
                timestamp: t0.AddSeconds(2)));

            EventHistoryResponse byInstance = await store.QueryEventsAsync(
                instance: "factorio-test", type: null, sinceMs: null, untilMs: null,
                beforeTs: null, beforeId: null, limit: 200);
            Assert.Equal(2, byInstance.Count);
            Assert.All(byInstance.Events, e => Assert.Equal("factorio-test", e.Instance));

            EventHistoryResponse byType = await store.QueryEventsAsync(
                instance: null, type: "instance_stopped", sinceMs: null, untilMs: null,
                beforeTs: null, beforeId: null, limit: 200);
            EventHistoryItem stopped = Assert.Single(byType.Events);
            Assert.Equal("instance_stopped", stopped.Type);

            EventHistoryResponse byWindow = await store.QueryEventsAsync(
                instance: null, type: null,
                sinceMs: t0.AddSeconds(1).ToUnixTimeMilliseconds(),
                untilMs: t0.AddSeconds(2).ToUnixTimeMilliseconds(),
                beforeTs: null, beforeId: null, limit: 200);
            Assert.Equal(2, byWindow.Count);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Composite_cursor_pages_through_ts_ties_without_skip_or_repeat()
    {
        var (store, db) = NewStore();
        try
        {
            // Three rows sharing the exact same ts (a burst) — the id tiebreak in the composite
            // cursor is what keeps paging stable across the tie.
            var t0 = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
            await store.AppendAsync(Wrapper(dataJson: """{"InstanceName":"a"}""", timestamp: t0));
            await store.AppendAsync(Wrapper(dataJson: """{"InstanceName":"b"}""", timestamp: t0));
            await store.AppendAsync(Wrapper(dataJson: """{"InstanceName":"c"}""", timestamp: t0));

            EventHistoryResponse page1 = await store.QueryEventsAsync(
                instance: null, type: null, sinceMs: null, untilMs: null,
                beforeTs: null, beforeId: null, limit: 2);
            Assert.Equal(2, page1.Events.Count);
            Assert.NotNull(page1.NextCursorTs);
            Assert.NotNull(page1.NextCursorId);

            EventHistoryResponse page2 = await store.QueryEventsAsync(
                instance: null, type: null, sinceMs: null, untilMs: null,
                beforeTs: long.Parse(page1.NextCursorTs!), beforeId: page1.NextCursorId,
                limit: 2);

            // The full 3 rows across both pages, no duplicate id, no gap.
            var allIds = page1.Events.Select(e => e.Id).Concat(page2.Events.Select(e => e.Id)).ToList();
            Assert.Equal(3, allIds.Distinct().Count());
            Assert.Equal(3, allIds.Count);
            // page2 is a partial page (1 of the 3 remain) -> no further cursor.
            Assert.Single(page2.Events);
            Assert.Null(page2.NextCursorTs);
            Assert.Null(page2.NextCursorId);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Prune_removes_old_rows_only()
    {
        var (store, db) = NewStore();
        try
        {
            var old = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var recent = new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero);
            await store.AppendAsync(Wrapper(dataJson: """{"InstanceName":"old"}""", timestamp: old));
            await store.AppendAsync(Wrapper(dataJson: """{"InstanceName":"new"}""", timestamp: recent));

            int deleted = await store.PruneOlderThanAsync(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
            Assert.Equal(1, deleted);

            EventHistoryResponse resp = await store.QueryEventsAsync(
                instance: null, type: null, sinceMs: null, untilMs: null,
                beforeTs: null, beforeId: null, limit: 200);
            EventHistoryItem survivor = Assert.Single(resp.Events);
            Assert.Equal("new", survivor.Instance);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Empty_store_yields_empty_page_never_fabricated()
    {
        var (store, db) = NewStore();
        try
        {
            EventHistoryResponse resp = await store.QueryEventsAsync(
                instance: null, type: null, sinceMs: null, untilMs: null,
                beforeTs: null, beforeId: null, limit: 200);

            Assert.Empty(resp.Events);
            Assert.Equal(0, resp.Count);
            Assert.Null(resp.NextCursorTs);
            Assert.Null(resp.NextCursorId);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Limit_is_clamped_to_the_hard_cap()
    {
        var (store, db) = NewStore();
        try
        {
            var t0 = DateTimeOffset.UtcNow;
            for (int i = 0; i < 3; i++)
                await store.AppendAsync(Wrapper(dataJson: $$"""{"InstanceName":"s{{i}}"}""", timestamp: t0.AddSeconds(i)));

            // A limit above MaxLimit must not throw and must still cap sanely (won't return more
            // rows than exist, but must not error on the oversized request).
            EventHistoryResponse resp = await store.QueryEventsAsync(
                instance: null, type: null, sinceMs: null, untilMs: null,
                beforeTs: null, beforeId: null, limit: EventHistoryStore.MaxLimit + 500);
            Assert.Equal(3, resp.Count);
        }
        finally { Cleanup(store, db); }
    }
}
