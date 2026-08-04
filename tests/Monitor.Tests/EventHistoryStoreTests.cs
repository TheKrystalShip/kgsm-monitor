using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Models;
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
            Assert.Null(item.Blueprint); // a server event is never blueprint-scoped
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
            Assert.Null(item.Blueprint);
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

    // --- blueprint-scoped events (Phase 2 of blueprint-editor-plan.md) --------------------------
    // A blueprint event's subject is BlueprintName, not InstanceName. The store carries a dedicated
    // `blueprint` column so such rows are attributable and filterable without overloading `instance`
    // (which would invent an instance relationship that does not exist). For every other (instance
    // or host/global) event the blueprint column stays null — never fabricated.

    [Fact]
    public async Task Blueprint_event_persists_blueprint_name_with_instance_null()
    {
        var (store, db) = NewStore();
        try
        {
            var w = Wrapper(
                eventType: "blueprint_updated",
                dataJson: """{"BlueprintName":"factorio","Tier":"user","OverridesSystem":true,"Runtime":"native"}""");
            await store.AppendAsync(w);

            EventHistoryResponse resp = await store.QueryEventsAsync(
                instance: null, type: null, sinceMs: null, untilMs: null,
                beforeTs: null, beforeId: null, limit: 200);

            EventHistoryItem item = Assert.Single(resp.Events);
            Assert.Equal("blueprint_updated", item.Type);
            Assert.Null(item.Instance); // a blueprint event never carries an instance
            Assert.Equal("factorio", item.Blueprint); // …but it does carry a blueprint
            Assert.Equal("factorio", item.Data!.Value.GetProperty("BlueprintName").GetString());
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Blueprint_filter_returns_only_matching_blueprint_rows()
    {
        var (store, db) = NewStore();
        try
        {
            var t0 = DateTimeOffset.UtcNow;
            await store.AppendAsync(Wrapper(eventType: "blueprint_updated",
                dataJson: """{"BlueprintName":"factorio"}""", timestamp: t0));
            await store.AppendAsync(Wrapper(eventType: "blueprint_updated",
                dataJson: """{"BlueprintName":"factorio"}""", timestamp: t0.AddSeconds(1)));
            await store.AppendAsync(Wrapper(eventType: "blueprint_updated",
                dataJson: """{"BlueprintName":"terraria"}""", timestamp: t0.AddSeconds(2)));

            EventHistoryResponse resp = await store.QueryEventsAsync(
                instance: null, type: null, sinceMs: null, untilMs: null,
                beforeTs: null, beforeId: null, limit: 200, blueprint: "factorio");

            Assert.Equal(2, resp.Count);
            Assert.All(resp.Events, i => Assert.Equal("factorio", i.Blueprint));
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Server_event_persists_instance_and_leaves_blueprint_null()
    {
        // The columns are orthogonal — a server event must never have its instance name accidentally
        // loaded into the blueprint column (which would conflate an instance fact with a blueprint one
        // and break the ?blueprint= filter for any instance that shares a blueprint's name).
        var (store, db) = NewStore();
        try
        {
            var w = Wrapper(); // default {"InstanceName":"factorio-test"}, no BlueprintName key
            await store.AppendAsync(w);

            EventHistoryResponse resp = await store.QueryEventsAsync(
                instance: null, type: null, sinceMs: null, untilMs: null,
                beforeTs: null, beforeId: null, limit: 200);

            EventHistoryItem item = Assert.Single(resp.Events);
            Assert.Equal("factorio-test", item.Instance);
            Assert.Null(item.Blueprint);
        }
        finally { Cleanup(store, db); }
    }

    // --- journal position ---------------------------------------------------------------

    [Fact]
    public async Task No_cursor_is_stored_until_one_is_saved()
    {
        var (store, db) = NewStore();
        try
        {
            // A fresh database, or one written before the journal transport existed, has no
            // position — which must read as "start cold", not as offset 0 of some segment.
            Assert.Null(await store.LoadCursorAsync());
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Cursor_round_trips_and_the_latest_save_wins()
    {
        var (store, db) = NewStore();
        try
        {
            await store.SaveCursorAsync(new EventCursor { Segment = "2026-08-04.ndjson", Offset = 1861 });

            EventCursor? first = await store.LoadCursorAsync();
            Assert.Equal("2026-08-04.ndjson", first!.Segment);
            Assert.Equal(1861, first.Offset);

            await store.SaveCursorAsync(new EventCursor { Segment = "2026-08-05.ndjson", Offset = 12 });

            // One row, upserted: the store keeps a position, not a history of positions.
            EventCursor? second = await store.LoadCursorAsync();
            Assert.Equal("2026-08-05.ndjson", second!.Segment);
            Assert.Equal(12, second.Offset);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task The_cursor_store_facade_reads_and_writes_the_same_row()
    {
        // The library talks to IEventCursorStore; proving the facade lands in the same database
        // is what makes "the position lives beside the events" true rather than intended.
        var (store, db) = NewStore();
        try
        {
            var facade = new EventJournalCursorStore(store);
            await facade.SaveAsync(new EventCursor { Segment = "2026-08-04.ndjson", Offset = 99 });

            Assert.Equal(99, (await store.LoadCursorAsync())!.Offset);
            Assert.Equal("2026-08-04.ndjson", (await facade.LoadAsync())!.Segment);
        }
        finally { Cleanup(store, db); }
    }

    // --- gaps ---------------------------------------------------------------------------

    [Fact]
    public async Task An_intact_history_reports_no_gaps()
    {
        var (store, db) = NewStore();
        try
        {
            await store.AppendAsync(Wrapper());

            EventHistoryResponse resp = await store.QueryEventsAsync(
                instance: null, type: null, sinceMs: null, untilMs: null,
                beforeTs: null, beforeId: null, limit: 200);

            // An empty list is a positive claim of unbroken coverage, so it must never be
            // absent or null — a reader distinguishes "no gaps" from "gaps unknown".
            Assert.Empty(resp.Gaps);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task A_recorded_gap_is_reported_alongside_the_events()
    {
        var (store, db) = NewStore();
        try
        {
            var detected = new DateTimeOffset(2026, 7, 18, 11, 0, 0, TimeSpan.Zero);
            await store.RecordGapAsync(new EventJournalGap(
                "2026-05-01.ndjson", 4096, EventJournalGapReason.SegmentPruned,
                "2026-07-18.ndjson", 0, detected));
            await store.AppendAsync(Wrapper());

            EventHistoryResponse resp = await store.QueryEventsAsync(
                instance: null, type: null, sinceMs: null, untilMs: null,
                beforeTs: null, beforeId: null, limit: 200);

            // Without this the page would look like a complete record of the window when it is
            // not — the exact fabrication the journal transport exists to make impossible.
            EventHistoryGap gap = Assert.Single(resp.Gaps);
            Assert.Equal("2026-05-01.ndjson", gap.LostSegment);
            Assert.Equal(4096, gap.LostOffset);
            Assert.Equal("SegmentPruned", gap.Reason);
            Assert.Equal("2026-07-18.ndjson", gap.ResumedAtSegment);
            Assert.Equal(detected, gap.DetectedAt);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Gaps_are_scoped_to_the_queried_window()
    {
        var (store, db) = NewStore();
        try
        {
            var old = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            await store.RecordGapAsync(new EventJournalGap(
                "2025-12-01.ndjson", 10, EventJournalGapReason.SegmentPruned,
                "2026-01-01.ndjson", 0, old));
            await store.AppendAsync(Wrapper());

            // A query over an intact stretch must not inherit a caveat from an unrelated one.
            EventHistoryResponse resp = await store.QueryEventsAsync(
                instance: null, type: null,
                sinceMs: new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                untilMs: null, beforeTs: null, beforeId: null, limit: 200);

            Assert.Single(resp.Events);
            Assert.Empty(resp.Gaps);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task A_replayed_event_does_not_duplicate_a_row()
    {
        // This is what makes at-least-once delivery safe: the journal reader saves its cursor
        // only past events already handed over, so a crash re-delivers the tail on restart.
        var (store, db) = NewStore();
        try
        {
            await store.AppendAsync(Wrapper());
            await store.AppendAsync(Wrapper());

            EventHistoryResponse resp = await store.QueryEventsAsync(
                instance: null, type: null, sinceMs: null, untilMs: null,
                beforeTs: null, beforeId: null, limit: 200);

            Assert.Single(resp.Events);
        }
        finally { Cleanup(store, db); }
    }
}
