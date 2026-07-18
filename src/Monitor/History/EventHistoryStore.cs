using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TheKrystalShip.KGSM.Events;

namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// The engine event-history store — the monitor is the single source of truth for KGSM engine
/// events (<c>docs/event-history-plan.md</c>). Raw <c>Microsoft.Data.Sqlite</c> (ADO, hand-written
/// SQL; EF Core is not AOT-safe), mirroring <see cref="HistoryStore"/>'s shape but in its own file
/// (<c>events.db</c>, own WAL, own single-writer gate) — engine events are higher-rate and discrete,
/// not sampled series, so they get no rollup tier and don't contend with the 15s metrics flusher.
/// One table (<c>event</c>), unix-ms <c>ts</c>, deterministic content id
/// (<see cref="AuditId.ForEvent"/>) as the primary key so a redelivered event can't double-insert
/// (<c>INSERT OR IGNORE</c>). WAL + INCREMENTAL auto-vacuum (auto-vacuum set before the table exists).
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
                      actor       TEXT,
                      origin      TEXT,
                      hostname    TEXT,
                      data        TEXT
                    );
                    CREATE INDEX IF NOT EXISTS ix_event_ts          ON event(ts);
                    CREATE INDEX IF NOT EXISTS ix_event_instance_ts ON event(instance, ts);
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
    /// (best-effort socket, monitor restart mid-flight) never double-inserts. <c>ts</c> comes from the
    /// envelope's own <see cref="EventWrapper.Timestamp"/>; a pre-enrichment KGSM that supplies none
    /// falls back to receipt time, logged (never silently substituted). <c>instance</c>/<c>actor</c>/
    /// <c>origin</c>/<c>hostname</c>/<c>data</c> are stored as SQL <c>NULL</c> when the envelope
    /// carries none — never fabricated.
    /// </summary>
    public async Task AppendAsync(EventWrapper wrapper, CancellationToken ct = default)
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
                INSERT OR IGNORE INTO event (id, ts, event_type, instance, actor, origin, hostname, data)
                VALUES ($id, $ts, $type, $instance, $actor, $origin, $hostname, $data)
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$ts", ts);
            cmd.Parameters.AddWithValue("$type", wrapper.EventType);
            cmd.Parameters.AddWithValue("$instance", (object?)instance ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$actor", (object?)wrapper.Actor ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$origin", (object?)wrapper.Origin ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hostname", (object?)wrapper.Hostname ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$data", (object?)data ?? DBNull.Value);

            int rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (rows == 0)
                _logger.LogDebug("event history: duplicate id {Id} ignored ({EventType})", id, wrapper.EventType);
        }
        finally { _gate.Release(); }
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
    public async Task<EventHistoryResponse> QueryEventsAsync(
        string? instance,
        string? type,
        long? sinceMs,
        long? untilMs,
        long? beforeTs,
        string? beforeId,
        int limit,
        CancellationToken ct = default)
    {
        instance = string.IsNullOrEmpty(instance) ? null : instance;
        type = string.IsNullOrEmpty(type) ? null : type;
        beforeId = string.IsNullOrEmpty(beforeId) ? null : beforeId;

        int cappedLimit = limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);

        var sql = new System.Text.StringBuilder(
            "SELECT id, ts, event_type, instance, actor, origin, data FROM event WHERE 1 = 1");
        if (instance is not null) sql.Append(" AND instance = $instance");
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
                string? actor = reader.IsDBNull(4) ? null : reader.GetString(4);
                string? origin = reader.IsDBNull(5) ? null : reader.GetString(5);
                string? dataText = reader.IsDBNull(6) ? null : reader.GetString(6);

                JsonElement? data = null;
                if (dataText is not null)
                {
                    using JsonDocument doc = JsonDocument.Parse(dataText);
                    // Clone so the element outlives this using block (detaches from the pooled
                    // JsonDocument buffer) — the response is serialized after this reader loop.
                    data = doc.RootElement.Clone();
                }

                items.Add(new EventHistoryItem(
                    id, DateTimeOffset.FromUnixTimeMilliseconds(ts), eventType, rowInstance, actor, origin, data));
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

        return new EventHistoryResponse(items.Count, nextTs, nextId, items);
    }

    public void Dispose()
    {
        _conn?.Dispose();
        _gate.Dispose();
    }
}
