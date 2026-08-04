using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// The queryable <b>index</b> over KGSM engine events. The <em>record</em> is the engine's append-only
/// journal (<c>/var/lib/kgsm/events/YYYY-MM-DD.ndjson</c>); this database is derived from it and can be
/// rebuilt from it (<see cref="EventIndexRebuilder"/>) — it exists because NDJSON cannot answer
/// "the last 50 events for this instance, paged" in bounded time, not because it holds anything the
/// journal does not. What the monitor owns is therefore the index and the query surface, not the
/// authority for what happened.
/// <para>
/// One consequence is load-bearing: the index is only as complete as the journal it was built from,
/// and the journal is pruned on age alone. Journal retention must stay <b>≥</b>
/// <see cref="MonitorOptions.EventRetentionDays"/>, or a rebuild silently yields less than the index
/// already held — checked and reported at startup by <see cref="EventPersistService"/>.
/// </para>
/// <para>
/// Raw <c>Microsoft.Data.Sqlite</c> (ADO, hand-written SQL; EF Core is not AOT-safe), mirroring
/// <see cref="HistoryStore"/>'s shape but in its own file (<c>events.db</c>, own WAL, own
/// single-writer gate) — engine events are discrete, not sampled series, so they get no rollup tier
/// and don't contend with the 15s metrics flusher. One table (<c>event</c>), unix-ms <c>ts</c>,
/// deterministic content id (<see cref="AuditId.ForEvent"/>) as the primary key so a redelivered
/// event can't double-insert (<c>INSERT OR IGNORE</c>) — which is also what makes a rebuild safe to
/// run against a live index. WAL + INCREMENTAL auto-vacuum (auto-vacuum set before the table exists).
/// </para>
/// </summary>
public sealed class EventHistoryStore : IDisposable
{
    /// <summary>Default/soft-cap page size for <see cref="QueryEventsAsync"/> when the caller passes
    /// a non-positive or absent limit.</summary>
    public const int DefaultLimit = 200;

    /// <summary>Hard cap — a caller-supplied limit above this is clamped down.</summary>
    public const int MaxLimit = 1000;

    private readonly MonitorOptions _options;
    private readonly ILogger<EventHistoryStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _conn;
    private bool _ensured;

    public EventHistoryStore(MonitorOptions options, ILogger<EventHistoryStore> logger)
    {
        _options = options;
        _logger = logger;
    }

    // Caller must hold _gate. Opens the connection lazily and creates the schema once — mirrors
    // HistoryStore.EnsureAsync; there is no separate startup call, the same lazy-create-on-first-use
    // pattern the metrics store already uses.
    private async Task<SqliteConnection> EnsureAsync(CancellationToken ct)
    {
        if (_conn is not null && _ensured)
            return _conn;

        if (_conn is null)
        {
            string? dir = Path.GetDirectoryName(_options.EventsDbPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            _conn = new SqliteConnection($"Data Source={_options.EventsDbPath}");
            await _conn.OpenAsync(ct).ConfigureAwait(false);
        }

        if (!_ensured)
        {
            // auto_vacuum MUST be set BEFORE the table exists (SQLite ignores it after); WAL any time.
            await using (SqliteCommand pragma = _conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA auto_vacuum=INCREMENTAL; PRAGMA journal_mode=WAL;";
                await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using (SqliteCommand ddl = _conn.CreateCommand())
            {
                ddl.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS event (
                      id          TEXT    NOT NULL PRIMARY KEY,
                      ts          INTEGER NOT NULL,
                      event_type  TEXT    NOT NULL,
                      instance    TEXT,
                      blueprint   TEXT,
                      actor       TEXT,
                      origin      TEXT,
                      hostname    TEXT,
                      data        TEXT
                    );
                    CREATE INDEX IF NOT EXISTS ix_event_ts            ON event(ts);
                    CREATE INDEX IF NOT EXISTS ix_event_instance_ts   ON event(instance, ts);
                    CREATE INDEX IF NOT EXISTS ix_event_blueprint_ts  ON event(blueprint, ts);

                    CREATE TABLE IF NOT EXISTS meta (
                      key   TEXT NOT NULL PRIMARY KEY,
                      value TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS gap (
                      ts             INTEGER NOT NULL PRIMARY KEY,
                      reason         TEXT    NOT NULL,
                      lost_segment   TEXT    NOT NULL,
                      lost_offset    INTEGER NOT NULL,
                      resumed_at     TEXT,
                      resumed_offset INTEGER NOT NULL
                    );
                    """;
                await ddl.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            _ensured = true;
        }

        return _conn;
    }

    /// <summary>
    /// Persist one engine event envelope. Idempotent: the id (<see cref="AuditId.ForEvent"/>) is the
    /// primary key and the insert is <c>INSERT OR IGNORE</c>, so a redelivered/duplicate envelope
    /// (at-least-once journal delivery, a monitor restart mid-flight, or a full
    /// <see cref="EventIndexRebuilder">rebuild</see> replaying events the index already holds) never
    /// double-inserts. <c>ts</c> comes from the envelope's own <see cref="EventWrapper.Timestamp"/>; a
    /// pre-enrichment KGSM that supplies none falls back to receipt time, logged (never silently
    /// substituted). <c>instance</c>/<c>actor</c>/<c>origin</c>/<c>hostname</c>/<c>data</c> are stored
    /// as SQL <c>NULL</c> when the envelope carries none — never fabricated.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the row was newly inserted, <see langword="false"/> when an event
    /// with this id was already present. The rebuilder reports the two separately so an operator can
    /// see what the index was actually missing rather than a count that says only "events replayed".
    /// </returns>
    public async Task<bool> AppendAsync(EventWrapper wrapper, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wrapper);

        string id = AuditId.ForEvent(wrapper);

        long ts;
        if (wrapper.Timestamp is { } t)
        {
            ts = t.ToUnixTimeMilliseconds();
        }
        else
        {
            ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _logger.LogDebug(
                "event history: {EventType} envelope carried no Timestamp; using receipt time {Ts}",
                wrapper.EventType, ts);
        }

        string? instance = ExtractInstanceName(wrapper.Data);
        string? blueprint = ExtractBlueprintName(wrapper.Data);
        string? data = wrapper.Data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? null
            : wrapper.Data.GetRawText();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT OR IGNORE INTO event (id, ts, event_type, instance, blueprint, actor, origin, hostname, data)
                VALUES ($id, $ts, $type, $instance, $blueprint, $actor, $origin, $hostname, $data)
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.Parameters.AddWithValue("$type", wrapper.EventType);
            cmd.Parameters.AddWithValue("$instance", (object?)instance ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$blueprint", (object?)blueprint ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$actor", (object?)wrapper.Actor ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$origin", (object?)wrapper.Origin ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hostname", (object?)wrapper.Hostname ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$data", (object?)data ?? DBNull.Value);

            int rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (rows == 0)
                _logger.LogDebug("event history: duplicate id {Id} ignored ({EventType})", id, wrapper.EventType);
            return rows > 0;
        }
        finally { _gate.Release(); }
    }

    // Cursor key in the meta table. The monitor keeps its journal position in the same database
    // as the events it derives from them, so the index and the position it was built from can
    // never end up in different places.
    private const string CursorKey = "journal_cursor";

    /// <summary>
    /// Read the stored journal position, or <see langword="null"/> if the monitor has never
    /// recorded one (a fresh database, or one written before the journal transport).
    /// </summary>
    public async Task<EventCursor?> LoadCursorAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = $key";
            cmd.Parameters.AddWithValue("$key", CursorKey);

            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string raw)
                return null;

            // "<segment>\t<offset>" — a tab cannot appear in a segment file name, so the split
            // is unambiguous without pulling JSON into a two-field value.
            int tab = raw.IndexOf('\t', StringComparison.Ordinal);
            if (tab <= 0 || !long.TryParse(raw.AsSpan(tab + 1), CultureInfo.InvariantCulture, out long offset))
            {
                _logger.LogWarning("event history: stored journal cursor {Raw} is malformed; starting cold", raw);
                return null;
            }

            return new EventCursor { Segment = raw[..tab], Offset = offset };
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Record the journal position to resume from.
    /// </summary>
    /// <remarks>
    /// Written after the events before it have been persisted, not with them, so delivery is
    /// at-least-once: a crash between the two costs a re-read, never a lost event. That is safe
    /// here precisely because <see cref="AppendAsync"/> is idempotent — the deterministic
    /// <see cref="AuditId.ForEvent"/> primary key plus <c>INSERT OR IGNORE</c> turns a replayed
    /// event into a no-op.
    /// </remarks>
    public async Task SaveCursorAsync(EventCursor cursor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO meta (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """;
            cmd.Parameters.AddWithValue("$key", CursorKey);
            cmd.Parameters.AddWithValue(
                "$value",
                string.Create(CultureInfo.InvariantCulture, $"{cursor.Segment}\t{cursor.Offset}"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Record that the journal could not be resumed where the monitor left off, so events
    /// happened in the interval that this history does not contain.
    /// </summary>
    /// <remarks>
    /// This is the never-fabricate rule reaching the audit trail. A store that silently resumed
    /// after a gap would return a partial history indistinguishable from a complete one; a
    /// recorded gap lets <c>GET /events</c> state plainly that coverage before a point is
    /// incomplete. The socket transport could not do this at all — a missed event looked exactly
    /// like an event that never happened.
    /// </remarks>
    public async Task RecordGapAsync(EventJournalGap gap, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gap);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT OR REPLACE INTO gap (ts, reason, lost_segment, lost_offset, resumed_at, resumed_offset)
                VALUES ($ts, $reason, $lostSegment, $lostOffset, $resumedAt, $resumedOffset)
                """;
            cmd.Parameters.AddWithValue("$ts", gap.DetectedAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$reason", gap.Reason.ToString());
            cmd.Parameters.AddWithValue("$lostSegment", gap.LostSegment);
            cmd.Parameters.AddWithValue("$lostOffset", gap.LostOffset);
            cmd.Parameters.AddWithValue("$resumedAt", (object?)gap.ResumedAtSegment ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$resumedOffset", gap.ResumedAtOffset);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The recorded gaps overlapping a query window, newest first — the honest caveat that goes
    /// with a page of events.
    /// </summary>
    public async Task<IReadOnlyList<EventHistoryGap>> QueryGapsAsync(
        long? sinceMs, long? untilMs, CancellationToken ct = default)
    {
        var gaps = new List<EventHistoryGap>();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();

            var sql = new System.Text.StringBuilder(
                "SELECT ts, reason, lost_segment, lost_offset, resumed_at FROM gap WHERE 1 = 1");
            if (sinceMs is not null) sql.Append(" AND ts >= $since");
            if (untilMs is not null) sql.Append(" AND ts <= $until");
            sql.Append(" ORDER BY ts DESC");

            cmd.CommandText = sql.ToString();
            if (sinceMs is not null) cmd.Parameters.AddWithValue("$since", sinceMs.Value);
            if (untilMs is not null) cmd.Parameters.AddWithValue("$until", untilMs.Value);

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                gaps.Add(new EventHistoryGap(
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }
        finally { _gate.Release(); }

        return gaps;
    }

    private static string? ExtractInstanceName(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("InstanceName", out JsonElement instanceName)
            && instanceName.ValueKind == JsonValueKind.String)
        {
            string? name = instanceName.GetString();
            return string.IsNullOrEmpty(name) ? null : name;
        }

        return null;
    }

    /// <summary>
    /// Mirror of <see cref="ExtractInstanceName"/> for blueprint-scoped events. A blueprint event's
    /// subject is a <c>BlueprintName</c>, not an <see cref="EventDataBase.InstanceName"/>: the engine
    /// emits these with no instance relationship (Phase 2 of <c>blueprint-editor-plan.md</c>), and
    /// forcing them through <c>InstanceName</c> would invent one. Only <c>blueprint_*</c> envelopes
    /// carry this key, so the <c>blueprint</c> column is <see langword="null"/> for every other event —
    /// never fabricated, the same honest-null contract as the <c>instance</c> column.
    /// </summary>
    private static string? ExtractBlueprintName(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("BlueprintName", out JsonElement blueprintName)
            && blueprintName.ValueKind == JsonValueKind.String)
        {
            string? name = blueprintName.GetString();
            return string.IsNullOrEmpty(name) ? null : name;
        }

        return null;
    }

    /// <summary>Delete rows with <c>ts</c> older than <paramref name="cutoffMs"/> (unix ms).</summary>
    public async Task<int> PruneOlderThanAsync(long cutoffMs, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM event WHERE ts < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", cutoffMs);
            int deleted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (deleted > 0)
                _logger.LogDebug("event history: pruned {Count} events older than {CutoffMs}", deleted, cutoffMs);
            return deleted;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Windowed/filtered read, ts-DESC, keyset-paginated. Every filter is optional (absent = no
    /// constraint). The composite cursor <c>(beforeTs, beforeId)</c> means "rows strictly before this
    /// (ts, id) pair" — <c>ts &lt; beforeTs OR (ts = beforeTs AND id &lt; beforeId)</c> — matching the
    /// ts-DESC/id-DESC row order, so paging never skips or repeats a row at a ts tie. When only
    /// <paramref name="beforeTs"/> is supplied (no <paramref name="beforeId"/>) the cursor degrades to
    /// a plain <c>ts &lt; beforeTs</c> bound. <paramref name="limit"/> is clamped to
    /// [1, <see cref="MaxLimit"/>]; a non-positive/absent value uses <see cref="DefaultLimit"/>.
    /// <see cref="EventHistoryResponse.NextCursorTs"/>/<see cref="EventHistoryResponse.NextCursorId"/>
    /// are set from the last row only when the page came back full (a partial page means "no more
    /// rows", so the cursor is honestly <see langword="null"/>).
    /// </summary>
    /// <remarks>
    /// <paramref name="blueprint"/> filters on the <c>blueprint</c> column that blueprint-scoped events
    /// (Phase 2 of <c>blueprint-editor-plan.md</c>) populate with the event's <c>BlueprintName</c>. An
    /// instance-scoped and a blueprint-scoped row for the same name are distinct and never conflated —
    /// a <c>?blueprint=factorio</c> query returns blueprint file edits, an <c>?instance=factorio</c>
    /// query returns that server instance's lifecycle, and never the other way around.
    /// </remarks>
    public async Task<EventHistoryResponse> QueryEventsAsync(
        string? instance,
        string? type,
        long? sinceMs,
        long? untilMs,
        long? beforeTs,
        string? beforeId,
        int limit,
        string? blueprint = null,
        CancellationToken ct = default)
    {
        instance = string.IsNullOrEmpty(instance) ? null : instance;
        type = string.IsNullOrEmpty(type) ? null : type;
        beforeId = string.IsNullOrEmpty(beforeId) ? null : beforeId;
        blueprint = string.IsNullOrEmpty(blueprint) ? null : blueprint;

        int cappedLimit = limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);

        var sql = new System.Text.StringBuilder(
            "SELECT id, ts, event_type, instance, blueprint, actor, origin, data FROM event WHERE 1 = 1");
        if (instance is not null) sql.Append(" AND instance = $instance");
        if (blueprint is not null) sql.Append(" AND blueprint = $blueprint");
        if (type is not null) sql.Append(" AND event_type = $type");
        if (sinceMs is not null) sql.Append(" AND ts >= $since");
        if (untilMs is not null) sql.Append(" AND ts <= $until");
        if (beforeTs is not null && beforeId is not null)
            sql.Append(" AND (ts < $beforeTs OR (ts = $beforeTs AND id < $beforeId))");
        else if (beforeTs is not null)
            sql.Append(" AND ts < $beforeTs");
        sql.Append(" ORDER BY ts DESC, id DESC LIMIT $limit");

        var items = new List<EventHistoryItem>(cappedLimit);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = sql.ToString();
            if (instance is not null) cmd.Parameters.AddWithValue("$instance", instance);
            if (blueprint is not null) cmd.Parameters.AddWithValue("$blueprint", blueprint);
            if (type is not null) cmd.Parameters.AddWithValue("$type", type);
            if (sinceMs is not null) cmd.Parameters.AddWithValue("$since", sinceMs.Value);
            if (untilMs is not null) cmd.Parameters.AddWithValue("$until", untilMs.Value);
            if (beforeTs is not null) cmd.Parameters.AddWithValue("$beforeTs", beforeTs.Value);
            if (beforeTs is not null && beforeId is not null) cmd.Parameters.AddWithValue("$beforeId", beforeId);
            cmd.Parameters.AddWithValue("$limit", cappedLimit);

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string id = reader.GetString(0);
                long ts = reader.GetInt64(1);
                string eventType = reader.GetString(2);
                string? rowInstance = reader.IsDBNull(3) ? null : reader.GetString(3);
                string? rowBlueprint = reader.IsDBNull(4) ? null : reader.GetString(4);
                string? actor = reader.IsDBNull(5) ? null : reader.GetString(5);
                string? origin = reader.IsDBNull(6) ? null : reader.GetString(6);
                string? dataText = reader.IsDBNull(7) ? null : reader.GetString(7);

                JsonElement? data = null;
                if (dataText is not null)
                {
                    using JsonDocument doc = JsonDocument.Parse(dataText);
                    // Clone so the element outlives this using block (detaches from the pooled
                    // JsonDocument buffer) — the response is serialized after this reader loop.
                    data = doc.RootElement.Clone();
                }

                items.Add(new EventHistoryItem(
                    id, DateTimeOffset.FromUnixTimeMilliseconds(ts), eventType, rowInstance, rowBlueprint,
                    actor, origin, data));
            }
        }
        finally { _gate.Release(); }

        string? nextTs = null;
        string? nextId = null;
        if (items.Count == cappedLimit && items.Count > 0)
        {
            EventHistoryItem last = items[^1];
            nextTs = last.Ts.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            nextId = last.Id;
        }

        // Gaps are scoped to the same window as the rows, so a page that covers an intact stretch
        // reports none even when the store has recorded one elsewhere.
        IReadOnlyList<EventHistoryGap> gaps =
            await QueryGapsAsync(sinceMs, untilMs, ct).ConfigureAwait(false);

        return new EventHistoryResponse(items.Count, nextTs, nextId, items, gaps);
    }

    public void Dispose()
    {
        _conn?.Dispose();
        _gate.Dispose();
    }
}
