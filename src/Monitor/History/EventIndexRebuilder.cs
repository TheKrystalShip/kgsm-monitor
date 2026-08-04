using System.Diagnostics;
using System.Text.Json;
using TheKrystalShip.KGSM;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// Rebuilds <see cref="EventHistoryStore"/> from the engine's event journal — the operation that
/// makes "the index is derived, the journal is the record" a fact rather than a claim. Reads every
/// surviving segment in order and replays each envelope through the same
/// <see cref="EventHistoryStore.AppendAsync"/> the live reader uses, so a rebuilt index and a
/// streamed one are the same rows by construction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Additive, never destructive.</b> It inserts what is missing and leaves everything else alone;
/// it does not clear the table first. The journal is pruned on age and the index is not, so a wipe
/// would destroy rows whose segments no longer exist — the exact history a rebuild is supposed to
/// protect. "Rebuild" here means "make the index contain everything the journal still has", which is
/// the only rebuild that can never lose data.
/// </para>
/// <para>
/// <b>It does not touch the cursor.</b> The live reader's position is where streaming resumes; a
/// rebuild is a separate pass over the same files and has no opinion about it. Moving the cursor
/// would make an operator's recovery action silently skip or re-stream live events.
/// </para>
/// <para>
/// <b>It does not clear recorded gaps.</b> A gap says events are permanently absent because their
/// segment was pruned — replaying the segments that <em>did</em> survive does not bring those back,
/// so erasing the gap would turn an honest "incomplete before here" into a fabricated claim of
/// coverage. A gap is only ever retired by the events it covers becoming readable again, which
/// pruning has made impossible.
/// </para>
/// <para>
/// Safe to run against a live daemon: <see cref="EventHistoryStore"/> serializes every write behind
/// its own gate, and the deterministic <see cref="AuditId.ForEvent"/> primary key means an event the
/// live reader is inserting concurrently is a duplicate here, not a conflict. One rebuild at a time
/// (<see cref="_gate"/>) — a second caller gets <see cref="EventIndexRebuildResult.Busy"/> rather
/// than a second full scan competing for the same writer.
/// </para>
/// </remarks>
public sealed class EventIndexRebuilder : IDisposable
{
    private readonly EventHistoryStore _store;
    private readonly MonitorOptions _options;
    private readonly ILogger<EventIndexRebuilder> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EventIndexRebuilder(
        EventHistoryStore store, MonitorOptions options, ILogger<EventIndexRebuilder> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Replay every surviving journal segment into the index.
    /// </summary>
    /// <returns>
    /// A measured account of the pass: how many segments were read, how many lines each category
    /// (inserted / already present / unparseable) accounted for, and the window the journal actually
    /// covers. Every number is counted, none derived — <see cref="EventIndexRebuildResult.Inserted"/>
    /// is what the index was genuinely missing.
    /// </returns>
    public async Task<EventIndexRebuildResult> RebuildAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("event index rebuild: already running; refusing a second pass");
            return EventIndexRebuildResult.Busy();
        }

        try
        {
            return await RebuildCoreAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<EventIndexRebuildResult> RebuildCoreAsync(CancellationToken ct)
    {
        string dir = _options.KgsmJournalDir;

        if (!Directory.Exists(dir))
        {
            // Not an error: a host that has emitted no event since the journal was provisioned has
            // no directory yet. Say so plainly rather than reporting a zero-row rebuild as a success
            // that implies the journal was read and found empty.
            _logger.LogWarning("event index rebuild: journal directory {Dir} does not exist", dir);
            return EventIndexRebuildResult.NoJournal(dir);
        }

        // Segment names are dates, so lexical order is chronological — the same ordering the live
        // reader walks, which is why a rebuilt index carries the same rows in the same sequence.
        string[] segments = Directory.GetFiles(dir, "*.ndjson", SearchOption.TopDirectoryOnly);
        Array.Sort(segments, StringComparer.Ordinal);

        var sw = Stopwatch.StartNew();
        long lines = 0, inserted = 0, duplicates = 0, malformed = 0;

        foreach (string segment in segments)
        {
            ct.ThrowIfCancellationRequested();

            long segmentLines = 0, segmentInserted = 0, segmentMalformed = 0;

            // FileShare.ReadWrite | Delete so a rebuild never blocks the engine appending to today's
            // segment, nor the pruner deleting an old one mid-pass.
            await using var stream = new FileStream(
                segment, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                // A trailing partial line (the engine mid-append) reads as a short line here. It
                // fails to parse and is counted as malformed rather than guessed at — the live
                // reader will deliver it once the newline lands.
                if (line.Length == 0)
                    continue;

                segmentLines++;

                EventWrapper? wrapper;
                try
                {
                    wrapper = JsonSerializer.Deserialize(line, KgsmJsonContext.Default.EventWrapper);
                }
                catch (JsonException)
                {
                    wrapper = null;
                }

                if (wrapper is null || string.IsNullOrEmpty(wrapper.EventType))
                {
                    segmentMalformed++;
                    continue;
                }

                if (await _store.AppendAsync(wrapper, ct).ConfigureAwait(false))
                    segmentInserted++;
            }

            lines += segmentLines;
            inserted += segmentInserted;
            duplicates += segmentLines - segmentInserted - segmentMalformed;
            malformed += segmentMalformed;

            _logger.LogDebug(
                "event index rebuild: {Segment} — {Lines} lines, {Inserted} inserted, {Malformed} unparseable",
                Path.GetFileName(segment), segmentLines, segmentInserted, segmentMalformed);
        }

        sw.Stop();

        var result = new EventIndexRebuildResult(
            Status: "ok",
            JournalDirectory: dir,
            Segments: segments.Length,
            OldestSegment: segments.Length > 0 ? Path.GetFileName(segments[0]) : null,
            NewestSegment: segments.Length > 0 ? Path.GetFileName(segments[^1]) : null,
            Lines: lines,
            Inserted: inserted,
            Duplicates: duplicates,
            Malformed: malformed,
            ElapsedMs: sw.ElapsedMilliseconds);

        _logger.LogInformation(
            "event index rebuild: {Segments} segments, {Lines} lines — {Inserted} inserted, {Duplicates} already present, "
            + "{Malformed} unparseable ({ElapsedMs}ms)",
            result.Segments, result.Lines, result.Inserted, result.Duplicates, result.Malformed, result.ElapsedMs);

        if (malformed > 0)
        {
            // Worth a warning of its own: unparseable lines are events the index will never hold, and
            // unlike a pruned segment nothing else records that they were dropped.
            _logger.LogWarning(
                "event index rebuild: {Malformed} journal line(s) could not be parsed and are absent from the index",
                malformed);
        }

        return result;
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>
/// The measured outcome of one rebuild pass, served from <c>POST /events/rebuild</c>.
/// </summary>
/// <param name="Status">
/// <c>ok</c> when the journal was read; <c>busy</c> when a rebuild was already running; <c>no_journal</c>
/// when the journal directory does not exist. Distinguished so "nothing to do" never looks like
/// "read the journal and found nothing".
/// </param>
/// <param name="JournalDirectory">The directory read — the index's provenance, stated rather than assumed.</param>
/// <param name="Segments">Segments read this pass.</param>
/// <param name="OldestSegment">
/// Oldest surviving segment, or <see langword="null"/> when there are none. This is the real floor of
/// what a rebuild can restore: nothing before it exists to be read.
/// </param>
/// <param name="NewestSegment">Newest segment read, or <see langword="null"/> when there are none.</param>
/// <param name="Lines">Non-empty lines encountered.</param>
/// <param name="Inserted">Events the index did not already hold.</param>
/// <param name="Duplicates">Events already present (the expected bulk of any rebuild of a healthy index).</param>
/// <param name="Malformed">Lines that could not be parsed as an event envelope — dropped, never guessed at.</param>
/// <param name="ElapsedMs">Wall-clock duration of the pass.</param>
public sealed record EventIndexRebuildResult(
    string Status,
    string JournalDirectory,
    int Segments,
    string? OldestSegment,
    string? NewestSegment,
    long Lines,
    long Inserted,
    long Duplicates,
    long Malformed,
    long ElapsedMs)
{
    public static EventIndexRebuildResult Busy() =>
        new("busy", string.Empty, 0, null, null, 0, 0, 0, 0, 0);

    public static EventIndexRebuildResult NoJournal(string dir) =>
        new("no_journal", dir, 0, null, null, 0, 0, 0, 0, 0);
}
