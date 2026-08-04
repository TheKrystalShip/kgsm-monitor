using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;
using TheKrystalShip.KGSM.Monitor.History;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// <see cref="EventIndexRebuilder"/> is what makes "events.db is a derived index, the journal is
/// the record" verifiable rather than asserted. These tests pin the three properties an operator's
/// recovery path depends on: a rebuild reconstructs the index from journal files alone, it is
/// additive (it never destroys rows whose segments are gone), and it reports what it actually did.
/// </summary>
public class EventIndexRebuilderTests
{
    [Fact]
    public async Task Rebuilds_an_empty_index_from_the_journal()
    {
        using var journal = new TempJournal();
        journal.Write("2026-08-01.ndjson",
            Line("instance_started", "factorio", "2026-08-01T10:00:00Z"),
            Line("instance_stopped", "factorio", "2026-08-01T11:00:00Z"));
        journal.Write("2026-08-02.ndjson",
            Line("instance_started", "terraria", "2026-08-02T09:00:00Z"));

        using var fixture = new StoreFixture(journal.Dir);

        EventIndexRebuildResult result = await fixture.Rebuilder.RebuildAsync();

        Assert.Equal("ok", result.Status);
        Assert.Equal(2, result.Segments);
        Assert.Equal("2026-08-01.ndjson", result.OldestSegment);
        Assert.Equal("2026-08-02.ndjson", result.NewestSegment);
        Assert.Equal(3, result.Lines);
        Assert.Equal(3, result.Inserted);
        Assert.Equal(0, result.Duplicates);
        Assert.Equal(0, result.Malformed);

        EventHistoryResponse resp = await fixture.QueryAllAsync();
        Assert.Equal(3, resp.Events.Count);
    }

    /// <summary>
    /// The expected shape of a rebuild against a healthy index: everything is already there, so the
    /// pass reports duplicates and inserts nothing. This is what makes the command safe to run on a
    /// live daemon — the deterministic AuditId turns a replay into a no-op.
    /// </summary>
    [Fact]
    public async Task Replaying_the_same_journal_twice_inserts_nothing_the_second_time()
    {
        using var journal = new TempJournal();
        journal.Write("2026-08-01.ndjson",
            Line("instance_started", "factorio", "2026-08-01T10:00:00Z"),
            Line("instance_stopped", "factorio", "2026-08-01T11:00:00Z"));

        using var fixture = new StoreFixture(journal.Dir);

        await fixture.Rebuilder.RebuildAsync();
        EventIndexRebuildResult second = await fixture.Rebuilder.RebuildAsync();

        Assert.Equal(0, second.Inserted);
        Assert.Equal(2, second.Duplicates);
        Assert.Equal(2, (await fixture.QueryAllAsync()).Events.Count);
    }

    /// <summary>
    /// The property that forbids a clear-then-replay implementation. The journal is pruned on age
    /// and the index is not, so rows whose segment no longer exists are irreplaceable — a rebuild
    /// must add to them, never start from an empty table.
    /// </summary>
    [Fact]
    public async Task A_row_whose_segment_was_pruned_survives_a_rebuild()
    {
        using var journal = new TempJournal();
        journal.Write("2026-08-02.ndjson", Line("instance_started", "terraria", "2026-08-02T09:00:00Z"));

        using var fixture = new StoreFixture(journal.Dir);

        // Stands in for an event indexed live from a segment retention has since deleted.
        await fixture.Store.AppendAsync(Wrapper("instance_started", "factorio", "2026-07-01T10:00:00Z"));

        EventIndexRebuildResult result = await fixture.Rebuilder.RebuildAsync();

        Assert.Equal(1, result.Inserted);
        EventHistoryResponse resp = await fixture.QueryAllAsync();
        Assert.Equal(2, resp.Events.Count);
        Assert.Contains(resp.Events, e => e.Instance == "factorio");
    }

    /// <summary>
    /// A gap records events that are permanently gone. Replaying the segments that survived does
    /// not recover them, so the caveat must outlive the rebuild — erasing it would turn an honest
    /// "incomplete before here" into a fabricated claim of full coverage.
    /// </summary>
    [Fact]
    public async Task A_recorded_gap_is_not_cleared_by_a_rebuild()
    {
        using var journal = new TempJournal();
        journal.Write("2026-08-02.ndjson", Line("instance_started", "terraria", "2026-08-02T09:00:00Z"));

        using var fixture = new StoreFixture(journal.Dir);
        await fixture.Store.RecordGapAsync(new EventJournalGap(
            "2026-07-01.ndjson", 4096, EventJournalGapReason.SegmentPruned,
            "2026-08-02.ndjson", 0, DateTimeOffset.Parse("2026-08-02T08:00:00Z")));

        await fixture.Rebuilder.RebuildAsync();

        EventHistoryResponse resp = await fixture.QueryAllAsync();
        Assert.Single(resp.Gaps);
        Assert.Equal("2026-07-01.ndjson", resp.Gaps[0].LostSegment);
    }

    /// <summary>
    /// A line that cannot be parsed is dropped and counted, never guessed at or partially indexed.
    /// The count is the honest signal that the index holds less than the journal does.
    /// </summary>
    [Fact]
    public async Task Unparseable_lines_are_counted_and_skipped_without_stopping_the_pass()
    {
        using var journal = new TempJournal();
        journal.Write("2026-08-01.ndjson",
            Line("instance_started", "factorio", "2026-08-01T10:00:00Z"),
            "{ this is not json",
            Line("instance_stopped", "factorio", "2026-08-01T11:00:00Z"));

        using var fixture = new StoreFixture(journal.Dir);

        EventIndexRebuildResult result = await fixture.Rebuilder.RebuildAsync();

        Assert.Equal(3, result.Lines);
        Assert.Equal(2, result.Inserted);
        Assert.Equal(1, result.Malformed);
        Assert.Equal(0, result.Duplicates);
        Assert.Equal(2, (await fixture.QueryAllAsync()).Events.Count);
    }

    /// <summary>
    /// "No journal here" and "read the journal, it was empty" are different facts and must not
    /// share a response — the first is a misconfiguration, the second is a quiet host.
    /// </summary>
    [Fact]
    public async Task A_missing_journal_directory_reports_no_journal_rather_than_an_empty_success()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"kgsm-journal-absent-{Guid.NewGuid():N}");
        using var fixture = new StoreFixture(missing);

        EventIndexRebuildResult result = await fixture.Rebuilder.RebuildAsync();

        Assert.Equal("no_journal", result.Status);
        Assert.Equal(missing, result.JournalDirectory);
    }

    [Fact]
    public async Task An_empty_journal_directory_is_a_successful_pass_over_nothing()
    {
        using var journal = new TempJournal();
        using var fixture = new StoreFixture(journal.Dir);

        EventIndexRebuildResult result = await fixture.Rebuilder.RebuildAsync();

        Assert.Equal("ok", result.Status);
        Assert.Equal(0, result.Segments);
        Assert.Null(result.OldestSegment);
    }

    /// <summary>
    /// The engine appends without locking, so a rebuild can land mid-write. The partial line has no
    /// terminating newline and must be counted as unparseable rather than indexed as a truncated
    /// event — the live reader delivers it once the newline lands.
    /// </summary>
    [Fact]
    public async Task A_trailing_partial_line_is_never_indexed_as_an_event()
    {
        using var journal = new TempJournal();
        journal.WriteRaw("2026-08-01.ndjson",
            Line("instance_started", "factorio", "2026-08-01T10:00:00Z") + "\n"
            + """{"EventType":"instance_stopp""");

        using var fixture = new StoreFixture(journal.Dir);

        EventIndexRebuildResult result = await fixture.Rebuilder.RebuildAsync();

        Assert.Equal(1, result.Inserted);
        Assert.Equal(1, result.Malformed);
        Assert.Single((await fixture.QueryAllAsync()).Events);
    }

    /// <summary>
    /// Segment names are dates, so ordinal order is chronological — the same walk the live reader
    /// does. Pinned because a different sort would index the journal in an order the streaming path
    /// never produces.
    /// </summary>
    [Fact]
    public async Task Segments_are_read_in_chronological_order()
    {
        using var journal = new TempJournal();
        journal.Write("2026-08-10.ndjson", Line("instance_started", "c", "2026-08-10T10:00:00Z"));
        journal.Write("2026-08-02.ndjson", Line("instance_started", "a", "2026-08-02T10:00:00Z"));
        journal.Write("2026-08-09.ndjson", Line("instance_started", "b", "2026-08-09T10:00:00Z"));

        using var fixture = new StoreFixture(journal.Dir);

        EventIndexRebuildResult result = await fixture.Rebuilder.RebuildAsync();

        Assert.Equal("2026-08-02.ndjson", result.OldestSegment);
        Assert.Equal("2026-08-10.ndjson", result.NewestSegment);
    }

    /// <summary>Files that are not journal segments share the directory with none of its meaning.</summary>
    [Fact]
    public async Task Non_segment_files_in_the_journal_directory_are_ignored()
    {
        using var journal = new TempJournal();
        journal.Write("2026-08-01.ndjson", Line("instance_started", "factorio", "2026-08-01T10:00:00Z"));
        journal.WriteRaw("README.txt", "not a segment");
        journal.WriteRaw("2026-08-01.ndjson.tmp", "half a segment");

        using var fixture = new StoreFixture(journal.Dir);

        EventIndexRebuildResult result = await fixture.Rebuilder.RebuildAsync();

        Assert.Equal(1, result.Segments);
        Assert.Equal(1, result.Inserted);
    }

    // --- helpers ------------------------------------------------------------------------------

    private static string Line(string type, string instance, string ts) =>
        JsonSerializer.Serialize(new
        {
            EventType = type,
            Data = new { InstanceName = instance },
            Timestamp = ts,
            Actor = "heisen",
            Origin = "cli",
            Hostname = "hotrod",
        });

    private static EventWrapper Wrapper(string type, string instance, string ts) =>
        JsonSerializer.Deserialize(Line(type, instance, ts), KgsmJsonContext.Default.EventWrapper)!;

    /// <summary>A throwaway journal directory holding hand-written segments.</summary>
    private sealed class TempJournal : IDisposable
    {
        public string Dir { get; } =
            Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), $"kgsm-journal-{Guid.NewGuid():N}")).FullName;

        /// <summary>Write a segment of complete NDJSON lines (each newline-terminated).</summary>
        public void Write(string name, params string[] lines) =>
            File.WriteAllText(Path.Combine(Dir, name), string.Concat(lines.Select(l => l + "\n")));

        /// <summary>Write exact bytes — for the partial-line and non-segment cases.</summary>
        public void WriteRaw(string name, string content) =>
            File.WriteAllText(Path.Combine(Dir, name), content);

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A real <see cref="EventHistoryStore"/> on a temp database, plus its rebuilder.</summary>
    private sealed class StoreFixture : IDisposable
    {
        private readonly string _db;

        public StoreFixture(string journalDir)
        {
            _db = Path.Combine(Path.GetTempPath(), $"kgsm-monitor-rebuild-{Guid.NewGuid():N}.db");
            var options = new MonitorOptions { EventsDbPath = _db, KgsmJournalDir = journalDir };
            Store = new EventHistoryStore(options, NullLogger<EventHistoryStore>.Instance);
            Rebuilder = new EventIndexRebuilder(Store, options, NullLogger<EventIndexRebuilder>.Instance);
        }

        public EventHistoryStore Store { get; }

        public EventIndexRebuilder Rebuilder { get; }

        public Task<EventHistoryResponse> QueryAllAsync() =>
            Store.QueryEventsAsync(
                instance: null, type: null, sinceMs: null, untilMs: null,
                beforeTs: null, beforeId: null, limit: 100);

        public void Dispose()
        {
            Rebuilder.Dispose();
            Store.Dispose();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                File.Delete(_db + suffix);
        }
    }
}
