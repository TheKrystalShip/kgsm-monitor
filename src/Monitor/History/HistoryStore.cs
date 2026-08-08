using Microsoft.Data.Sqlite;

namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>One raw sample row to persist: an (entity, metric, ts) point.</summary>
public readonly record struct HistoryRow(string Kind, string Id, string Metric, long Ts, double Value);

/// <summary>
/// The metrics-history store — the monitor is now the single source of truth for metrics history.
/// Raw <c>Microsoft.Data.Sqlite</c> (ADO, hand-written SQL): EF Core is not AOT-safe, so the daemon
/// persists via a single long-lived connection guarded by a write gate (SQLite is single-writer per
/// file). Two tables: <c>sample</c> (raw, ~15s step, 24h retention) and <c>rollup</c> (5-min buckets,
/// 30d retention). Unix-ms timestamps, composite PKs, no secondary indexes (the PK index serves the
/// left-prefix range reads). WAL + INCREMENTAL auto-vacuum (auto-vacuum set before the tables exist).
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private readonly MonitorOptions _options;
    private readonly ILogger<HistoryStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _conn;
    private bool _ensured;

    public HistoryStore(MonitorOptions options, ILogger<HistoryStore> logger)
    {
        _options = options;
        _logger = logger;
    }

    // Caller must hold _gate. Opens the connection lazily and creates the schema once.
    private async Task<SqliteConnection> EnsureAsync(CancellationToken ct)
    {
        if (_conn is not null && _ensured)
            return _conn;

        if (_conn is null)
        {
            string? dir = Path.GetDirectoryName(_options.HistoryDbPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            _conn = new SqliteConnection($"Data Source={_options.HistoryDbPath}");
            await _conn.OpenAsync(ct).ConfigureAwait(false);
        }

        if (!_ensured)
        {
            // auto_vacuum MUST be set BEFORE the tables exist (SQLite ignores it after); WAL any time.
            await using (SqliteCommand pragma = _conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA auto_vacuum=INCREMENTAL; PRAGMA journal_mode=WAL;";
                await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using (SqliteCommand ddl = _conn.CreateCommand())
            {
                ddl.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS sample (
                      entity_kind TEXT NOT NULL, entity_id TEXT NOT NULL, metric TEXT NOT NULL,
                      ts INTEGER NOT NULL, value REAL NOT NULL,
                      PRIMARY KEY (entity_kind, entity_id, metric, ts));
                    CREATE TABLE IF NOT EXISTS rollup (
                      entity_kind TEXT NOT NULL, entity_id TEXT NOT NULL, metric TEXT NOT NULL,
                      bucket_ts INTEGER NOT NULL, avg REAL NOT NULL, min REAL NOT NULL,
                      max REAL NOT NULL, n INTEGER NOT NULL,
                      PRIMARY KEY (entity_kind, entity_id, metric, bucket_ts));
                    """;
                await ddl.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            _ensured = true;
        }

        return _conn;
    }

    /// <summary>Write a batch of raw sample rows in one transaction. Duplicate PKs are replaced
    /// (idempotent re-flush of the same conflated frame — never a fabricated carry-forward point).</summary>
    public async Task WriteSamplesAsync(IReadOnlyList<HistoryRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteTransaction tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "INSERT OR REPLACE INTO sample (entity_kind, entity_id, metric, ts, value) VALUES ($k, $i, $m, $t, $v)";
            SqliteParameter pKind = cmd.Parameters.Add("$k", SqliteType.Text);
            SqliteParameter pId = cmd.Parameters.Add("$i", SqliteType.Text);
            SqliteParameter pMetric = cmd.Parameters.Add("$m", SqliteType.Text);
            SqliteParameter pTs = cmd.Parameters.Add("$t", SqliteType.Integer);
            SqliteParameter pVal = cmd.Parameters.Add("$v", SqliteType.Real);
            cmd.Prepare();

            foreach (HistoryRow row in rows)
            {
                pKind.Value = row.Kind;
                pId.Value = row.Id;
                pMetric.Value = row.Metric;
                pTs.Value = row.Ts;
                pVal.Value = row.Value;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            _logger.LogDebug("metrics history: wrote {Count} sample rows", rows.Count);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Roll up complete Tier-1 buckets into Tier-2. Only fully-closed buckets
    /// (bucket_start + step ≤ now) are processed — the current open bucket is never touched.
    /// Idempotent (INSERT OR REPLACE).</summary>
    public async Task RollupAsync(int stepMinutes, long nowMs, CancellationToken ct = default)
    {
        long stepMs = stepMinutes * 60_000L;
        long currentBucketStart = (nowMs / stepMs) * stepMs;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT OR REPLACE INTO rollup (entity_kind, entity_id, metric, bucket_ts, avg, min, max, n)
                SELECT entity_kind, entity_id, metric,
                       (ts / $step) * $step AS bucket_ts,
                       AVG(value), MIN(value), MAX(value), COUNT(*)
                FROM sample
                WHERE ts < $current_bucket
                GROUP BY entity_kind, entity_id, metric, bucket_ts
                """;
            cmd.Parameters.AddWithValue("$step", stepMs);
            cmd.Parameters.AddWithValue("$current_bucket", currentBucketStart);
            int rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (rows > 0)
                _logger.LogDebug("metrics history: rolled up {Count} buckets (step={StepMin}min)", rows, stepMinutes);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Delete raw samples older than <paramref name="cutoffMs"/> (unix ms).</summary>
    public Task<int> PruneRawAsync(long cutoffMs, CancellationToken ct = default) =>
        DeleteOlderThanAsync("DELETE FROM sample WHERE ts < $cutoff", cutoffMs, "raw samples", ct);

    /// <summary>Delete rollup buckets older than <paramref name="cutoffMs"/> (unix ms).</summary>
    public Task<int> PruneRollupsAsync(long cutoffMs, CancellationToken ct = default) =>
        DeleteOlderThanAsync("DELETE FROM rollup WHERE bucket_ts < $cutoff", cutoffMs, "rollup rows", ct);

    private async Task<int> DeleteOlderThanAsync(string sql, long cutoffMs, string what, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$cutoff", cutoffMs);
            int deleted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (deleted > 0)
                _logger.LogDebug("metrics history: pruned {Count} {What} older than {CutoffMs}", deleted, what, cutoffMs);
            return deleted;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// What the store actually holds right now: row counts, the distinct entities covered, and the span
    /// each tier really spans on disk.
    /// </summary>
    /// <remarks>
    /// The measured span is the point. Retention is configuration — an intent — and the two diverge for
    /// ordinary reasons (the daemon was down, maintenance hasn't run since a retention change, the store
    /// is younger than its window). Nothing else in the ecosystem can tell you that history is not
    /// actually retaining what it was asked to, so this reports both and lets the reader compare.
    /// <para>
    /// Both tiers are counted in one pass under the write gate, so the numbers describe one consistent
    /// moment rather than two. An unreadable store yields <see langword="null"/> — never zeroes, which
    /// would read as an empty database rather than an unanswered question.
    /// </para>
    /// </remarks>
    public async Task<HistoryStoreStats?> StatsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);

            (long rows, long? oldest, long? newest, long entities) raw =
                await TierStatsAsync(conn, "sample", "ts", ct).ConfigureAwait(false);
            (long rows, long? oldest, long? newest, long entities) roll =
                await TierStatsAsync(conn, "rollup", "bucket_ts", ct).ConfigureAwait(false);

            return new HistoryStoreStats(
                DbPath: _options.HistoryDbPath,
                DbBytes: DbFileBytes(),
                RawRows: raw.rows,
                RawOldestMs: raw.oldest,
                RawNewestMs: raw.newest,
                RawEntities: raw.entities,
                RollupRows: roll.rows,
                RollupOldestMs: roll.oldest,
                RollupNewestMs: roll.newest,
                RollupEntities: roll.entities);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "metrics history: stats read failed");
            return null;
        }
        finally { _gate.Release(); }
    }

    private static async Task<(long Rows, long? Oldest, long? Newest, long Entities)> TierStatsAsync(
        SqliteConnection conn, string table, string tsColumn, CancellationToken ct)
    {
        await using SqliteCommand cmd = conn.CreateCommand();
        // COUNT(DISTINCT entity_kind || entity_id) counts entities, not rows: one server contributes a
        // dozen metrics and would otherwise look like a dozen things being recorded.
        cmd.CommandText =
            $"SELECT COUNT(*), MIN({tsColumn}), MAX({tsColumn}), "
            + $"COUNT(DISTINCT entity_kind || '' || entity_id) FROM {table};";
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return (0, null, null, 0);

        long rows = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
        long? oldest = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        long? newest = reader.IsDBNull(2) ? null : reader.GetInt64(2);
        long entities = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
        return (rows, oldest, newest, entities);
    }

    // The database plus its WAL and shared-memory sidecars — what the store actually costs on disk. A
    // busy WAL is a real several megabytes, and reporting the main file alone understates it.
    private long? DbFileBytes()
    {
        try
        {
            long total = 0;
            bool any = false;
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                var f = new FileInfo(_options.HistoryDbPath + suffix);
                if (f.Exists) { total += f.Length; any = true; }
            }
            return any ? total : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Run PRAGMA incremental_vacuum to reclaim free pages after a prune.</summary>
    public async Task VacuumAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA incremental_vacuum;";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Read a history window for an entity and shape it into the response DTO. Tier is chosen by
    /// range: range ≤ raw retention → raw (sample), else → rollup. Series are sparse (a metric with
    /// no rows in the window is simply absent). An unreadable/empty store yields empty series, never a
    /// fabricated curve.
    /// </summary>
    public async Task<MetricsHistoryResponse> QueryHistoryAsync(
        string entityKind, string entityId, string? rangeStr, CancellationToken ct = default)
    {
        rangeStr ??= MetricsRange.OneHour;
        TimeSpan duration = MetricsRange.Parse(rangeStr) ?? TimeSpan.FromHours(1);

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long fromMs = nowMs - (long)duration.TotalMilliseconds;
        bool useRaw = duration.TotalHours <= _options.RawRetentionHours;

        var series = new Dictionary<string, List<MetricsHistoryPoint>>();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            if (useRaw)
                cmd.CommandText =
                    "SELECT metric, ts, value FROM sample WHERE entity_kind = $k AND entity_id = $i AND ts >= $from AND ts <= $to ORDER BY ts";
            else
                cmd.CommandText =
                    "SELECT metric, bucket_ts, avg, min, max, n FROM rollup WHERE entity_kind = $k AND entity_id = $i AND bucket_ts >= $from AND bucket_ts <= $to ORDER BY bucket_ts";
            cmd.Parameters.AddWithValue("$k", entityKind);
            cmd.Parameters.AddWithValue("$i", entityId);
            cmd.Parameters.AddWithValue("$from", fromMs);
            cmd.Parameters.AddWithValue("$to", nowMs);

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string metric = reader.GetString(0);
                if (!series.TryGetValue(metric, out List<MetricsHistoryPoint>? list))
                {
                    list = [];
                    series[metric] = list;
                }

                DateTimeOffset ts = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));
                if (useRaw)
                    list.Add(new MetricsHistoryPoint(ts, reader.GetDouble(2)));
                else
                    list.Add(new MetricsHistoryPoint(ts, reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4), reader.GetInt32(5)));
            }
        }
        finally { _gate.Release(); }

        int step = useRaw ? _options.PersistMs / 1000 : _options.RollupStepMin * 60;
        return new MetricsHistoryResponse(entityId, entityKind, rangeStr, step, useRaw ? "raw" : "rollup", series);
    }

    public void Dispose()
    {
        _conn?.Dispose();
        _gate.Dispose();
    }
}
