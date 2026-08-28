using Microsoft.Data.Sqlite;

namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>One raw sample row to persist: an (entity, metric, ts) point.</summary>
public readonly record struct HistoryRow(string Kind, string Id, string Metric, long Ts, double Value);

/// <summary>
/// One instance's accumulated memory footprint — the durable half of what the sampler measures.
/// </summary>
/// <remarks>
/// <b>This outlives the series it is built from.</b> Samples are kept for a day and rollups for a
/// month; an instance's footprint is a statement about its whole observed life, so it accumulates in
/// place and is exempt from both prunes. Nothing here is re-derivable from the history tables once
/// they have rolled past their horizon.
/// <para>
/// The four <c>Last*</c> fields are not part of the answer — they are the accumulator's own state,
/// persisted so that a daemon restart does not re-count a counter it has already folded in or invent a
/// run boundary out of its own downtime.
/// </para>
/// </remarks>
/// <param name="InstanceId">The instance this record is about — the join key every per-server figure uses.</param>
/// <param name="FirstSeen">Unix ms of the first observation behind this record.</param>
/// <param name="LastSeen">Unix ms of the most recent observation.</param>
/// <param name="Runs">Run boundaries observed, i.e. how many times this instance has been seen to
/// start. Not incremented by a daemon restart, which is this daemon's event and not the instance's.</param>
/// <param name="UptimeMs">Cumulative time this instance has been observed running. Time the daemon was
/// down is not counted — the instance may well have been running, but this daemon did not see it.</param>
/// <param name="Samples">Observations that carried a working set, which is what the mean divides by.</param>
/// <param name="AnonMax">The largest working set observed (anonymous memory plus swap).</param>
/// <param name="AnonSum">Sum of every working-set observation; divided by <paramref name="Samples"/> it
/// is the mean. Held as a sum so the mean stays exact across an unbounded number of observations.</param>
/// <param name="PeakBytes">The highest <c>memory.peak</c> observed, folded across runs.</param>
/// <param name="OomKills">Total OOM kills, accumulated across the cgroup resets that zero the counter.</param>
/// <param name="MaxEvents">Total times allocation hit the memory ceiling, accumulated the same way.</param>
/// <param name="StallTotalUsec">Cumulative full-stall microseconds, accumulated the same way.</param>
/// <param name="LastPeak">Accumulator state: the previous <c>memory.peak</c>, which going backwards is
/// how a new cgroup — and therefore a new run — is recognised.</param>
/// <param name="LastOomKills">Accumulator state: the OOM counter as last read, so only what is new is banked.</param>
/// <param name="LastMaxEvents">Accumulator state: the ceiling-hit counter as last read.</param>
/// <param name="LastStallTotal">Accumulator state: the PSI stall total as last read.</param>
public readonly record struct FootprintRow(
    string InstanceId,
    long FirstSeen,
    long LastSeen,
    long Runs,
    long UptimeMs,
    long Samples,
    double? AnonMax,
    double AnonSum,
    double? PeakBytes,
    long OomKills,
    long MaxEvents,
    long StallTotalUsec,
    double? LastPeak,
    long? LastOomKills,
    long? LastMaxEvents,
    long? LastStallTotal);

/// <summary>
/// The metrics-history store — the monitor is the single source of truth for metrics history.
/// Raw <c>Microsoft.Data.Sqlite</c> (ADO, hand-written SQL): EF Core is not AOT-safe, so the daemon
/// persists via a single long-lived connection guarded by a write gate (SQLite is single-writer per
/// file). Two tables: <c>sample</c> (raw, ~15s step, 24h retention) and <c>rollup</c> (5-min buckets,
/// 30d retention). Unix-ms timestamps, composite PKs, no secondary indexes (the PK index serves the
/// left-prefix range reads). WAL + INCREMENTAL auto-vacuum (auto-vacuum set before the tables exist).
/// <para>
/// Two further tables hold what outlives a retention horizon: <c>threshold_episode</c> (what fired) and
/// <c>footprint</c> (what each instance has been measured to hold). Neither is touched by the prune
/// paths, and a <c>footprint</c> row is removed only when the instance itself is gone.
/// </para>
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
                    CREATE TABLE IF NOT EXISTS threshold_episode (
                      episode_id TEXT PRIMARY KEY,
                      rule_key TEXT NOT NULL, metric TEXT NOT NULL, scope TEXT NOT NULL,
                      ref TEXT, server_id TEXT,
                      opened_ts INTEGER NOT NULL, closed_ts INTEGER,
                      peak_band TEXT NOT NULL, peak_value REAL NOT NULL,
                      open_value REAL NOT NULL, close_value REAL, threshold REAL NOT NULL,
                      producer TEXT NOT NULL, close_reason TEXT);
                    CREATE INDEX IF NOT EXISTS ix_episode_opened ON threshold_episode (opened_ts);
                    CREATE INDEX IF NOT EXISTS ix_episode_closed ON threshold_episode (closed_ts);
                    CREATE TABLE IF NOT EXISTS footprint (
                      instance_id TEXT PRIMARY KEY,
                      first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,
                      runs INTEGER NOT NULL, uptime_ms INTEGER NOT NULL, samples INTEGER NOT NULL,
                      anon_max REAL, anon_sum REAL NOT NULL, peak_bytes REAL,
                      oom_kills INTEGER NOT NULL, max_events INTEGER NOT NULL,
                      stall_total_usec INTEGER NOT NULL,
                      last_peak REAL, last_oom_kills INTEGER, last_max_events INTEGER,
                      last_stall_total INTEGER);
                    """;
                await ddl.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            // CREATE TABLE IF NOT EXISTS does nothing to a table that already exists, so a column added
            // after the fact needs its own statement. Additive and idempotent: SQLite refuses a duplicate
            // column, which is the "already migrated" case and is swallowed.
            await using (SqliteCommand alter = _conn.CreateCommand())
            {
                alter.CommandText = "ALTER TABLE threshold_episode ADD COLUMN close_reason TEXT;";
                try { await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
                catch (SqliteException) { /* the column is already there */ }
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

    /// <summary>Read every footprint row. Small by construction — one row per instance this host has
    /// ever run — so it is returned whole rather than paged.</summary>
    public async Task<IReadOnlyList<FootprintRow>> QueryFootprintsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT instance_id, first_seen, last_seen, runs, uptime_ms, samples,
                       anon_max, anon_sum, peak_bytes, oom_kills, max_events, stall_total_usec,
                       last_peak, last_oom_kills, last_max_events, last_stall_total
                FROM footprint ORDER BY instance_id
                """;

            var rows = new List<FootprintRow>();
            await using SqliteDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(new FootprintRow(
                    InstanceId: r.GetString(0),
                    FirstSeen: r.GetInt64(1),
                    LastSeen: r.GetInt64(2),
                    Runs: r.GetInt64(3),
                    UptimeMs: r.GetInt64(4),
                    Samples: r.GetInt64(5),
                    AnonMax: r.IsDBNull(6) ? null : r.GetDouble(6),
                    AnonSum: r.GetDouble(7),
                    PeakBytes: r.IsDBNull(8) ? null : r.GetDouble(8),
                    OomKills: r.GetInt64(9),
                    MaxEvents: r.GetInt64(10),
                    StallTotalUsec: r.GetInt64(11),
                    LastPeak: r.IsDBNull(12) ? null : r.GetDouble(12),
                    LastOomKills: r.IsDBNull(13) ? null : r.GetInt64(13),
                    LastMaxEvents: r.IsDBNull(14) ? null : r.GetInt64(14),
                    LastStallTotal: r.IsDBNull(15) ? null : r.GetInt64(15)));
            }
            return rows;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Write a batch of footprint rows in one transaction, replacing each by instance id.</summary>
    public async Task WriteFootprintsAsync(IReadOnlyList<FootprintRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteTransaction tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    INSERT OR REPLACE INTO footprint
                      (instance_id, first_seen, last_seen, runs, uptime_ms, samples,
                       anon_max, anon_sum, peak_bytes, oom_kills, max_events, stall_total_usec,
                       last_peak, last_oom_kills, last_max_events, last_stall_total)
                    VALUES ($id,$first,$last,$runs,$uptime,$samples,
                            $anonMax,$anonSum,$peak,$oom,$max,$stall,
                            $lastPeak,$lastOom,$lastMax,$lastStall)
                    """;
                SqliteParameter id = cmd.Parameters.Add("$id", SqliteType.Text);
                SqliteParameter first = cmd.Parameters.Add("$first", SqliteType.Integer);
                SqliteParameter last = cmd.Parameters.Add("$last", SqliteType.Integer);
                SqliteParameter runs = cmd.Parameters.Add("$runs", SqliteType.Integer);
                SqliteParameter uptime = cmd.Parameters.Add("$uptime", SqliteType.Integer);
                SqliteParameter samples = cmd.Parameters.Add("$samples", SqliteType.Integer);
                SqliteParameter anonMax = cmd.Parameters.Add("$anonMax", SqliteType.Real);
                SqliteParameter anonSum = cmd.Parameters.Add("$anonSum", SqliteType.Real);
                SqliteParameter peak = cmd.Parameters.Add("$peak", SqliteType.Real);
                SqliteParameter oom = cmd.Parameters.Add("$oom", SqliteType.Integer);
                SqliteParameter max = cmd.Parameters.Add("$max", SqliteType.Integer);
                SqliteParameter stall = cmd.Parameters.Add("$stall", SqliteType.Integer);
                SqliteParameter lastPeak = cmd.Parameters.Add("$lastPeak", SqliteType.Real);
                SqliteParameter lastOom = cmd.Parameters.Add("$lastOom", SqliteType.Integer);
                SqliteParameter lastMax = cmd.Parameters.Add("$lastMax", SqliteType.Integer);
                SqliteParameter lastStall = cmd.Parameters.Add("$lastStall", SqliteType.Integer);

                foreach (FootprintRow row in rows)
                {
                    id.Value = row.InstanceId;
                    first.Value = row.FirstSeen;
                    last.Value = row.LastSeen;
                    runs.Value = row.Runs;
                    uptime.Value = row.UptimeMs;
                    samples.Value = row.Samples;
                    anonMax.Value = (object?)row.AnonMax ?? DBNull.Value;
                    anonSum.Value = row.AnonSum;
                    peak.Value = (object?)row.PeakBytes ?? DBNull.Value;
                    oom.Value = row.OomKills;
                    max.Value = row.MaxEvents;
                    stall.Value = row.StallTotalUsec;
                    lastPeak.Value = (object?)row.LastPeak ?? DBNull.Value;
                    lastOom.Value = (object?)row.LastOomKills ?? DBNull.Value;
                    lastMax.Value = (object?)row.LastMaxEvents ?? DBNull.Value;
                    lastStall.Value = (object?)row.LastStallTotal ?? DBNull.Value;
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Drop footprint rows for instances that no longer exist, given the ids that currently do.
    /// </summary>
    /// <remarks>
    /// <b>The caller passes what exists, not what is running.</b> A stopped instance is still an
    /// instance and its accumulated footprint is exactly what a later comparison needs; what this
    /// removes is the id of an instance that has been uninstalled.
    /// <para>
    /// <b>An empty set deletes nothing.</b> The watch-list is empty both when every instance has been
    /// removed and when the engine could not be reached to ask — and those must not look alike to a
    /// statement that drops rows. The first case costs some stale rows until an instance exists again;
    /// treating the second as authoritative would erase the record this feature is for.
    /// </para>
    /// </remarks>
    public async Task<int> ReconcileFootprintsAsync(IReadOnlyCollection<string> liveIds, CancellationToken ct = default)
    {
        if (liveIds.Count == 0) return 0;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            var names = new List<string>(liveIds.Count);
            int i = 0;
            foreach (string live in liveIds)
            {
                string name = $"$k{i++}";
                names.Add(name);
                cmd.Parameters.AddWithValue(name, live);
            }
            cmd.CommandText = $"DELETE FROM footprint WHERE instance_id NOT IN ({string.Join(",", names)})";
            int deleted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (deleted > 0)
                _logger.LogInformation("footprint: dropped {Count} row(s) for instances that no longer exist", deleted);
            return deleted;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Delete raw samples older than <paramref name="cutoffMs"/> (unix ms).</summary>
    public Task<int> PruneRawAsync(long cutoffMs, CancellationToken ct = default) =>
        DeleteOlderThanAsync("DELETE FROM sample WHERE ts < $cutoff", cutoffMs, "raw samples", ct);

    /// <summary>Delete rollup buckets older than <paramref name="cutoffMs"/> (unix ms).</summary>
    public Task<int> PruneRollupsAsync(long cutoffMs, CancellationToken ct = default) =>
        DeleteOlderThanAsync("DELETE FROM rollup WHERE bucket_ts < $cutoff", cutoffMs, "rollup rows", ct);

    /// <summary>Delete CLOSED threshold episodes that closed before <paramref name="cutoffMs"/>. An open
    /// episode is never pruned however old it is: a condition that has been firing for a month is exactly
    /// the one worth still knowing about.</summary>
    public Task<int> PruneEpisodesAsync(long cutoffMs, CancellationToken ct = default) =>
        DeleteOlderThanAsync(
            "DELETE FROM threshold_episode WHERE closed_ts IS NOT NULL AND closed_ts < $cutoff",
            cutoffMs, "threshold episodes", ct);

    /// <summary>
    /// Record a threshold episode opening. Idempotent on the episode id, so a retry cannot double-write
    /// and a restart that re-observes an open condition cannot either.
    /// </summary>
    public async Task OpenEpisodeAsync(EpisodeOpen episode, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO threshold_episode
                  (episode_id, rule_key, metric, scope, ref, server_id, opened_ts, closed_ts,
                   peak_band, peak_value, open_value, close_value, threshold, producer)
                VALUES ($id, $rule, $metric, $scope, $ref, $server, $opened, NULL,
                        $band, $peak, $open, NULL, $threshold, $producer)
                ON CONFLICT(episode_id) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("$id", episode.EpisodeId);
            cmd.Parameters.AddWithValue("$rule", episode.RuleKey);
            cmd.Parameters.AddWithValue("$metric", episode.Metric);
            cmd.Parameters.AddWithValue("$scope", episode.Scope);
            cmd.Parameters.AddWithValue("$ref", (object?)episode.Ref ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$server", (object?)episode.ServerId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$opened", episode.OpenedTs);
            cmd.Parameters.AddWithValue("$band", episode.Band);
            cmd.Parameters.AddWithValue("$peak", episode.Value);
            cmd.Parameters.AddWithValue("$open", episode.Value);
            cmd.Parameters.AddWithValue("$threshold", episode.Threshold);
            cmd.Parameters.AddWithValue("$producer", episode.Producer);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Record a threshold episode closing, and the worst it got. The peak is maxed rather than
    /// overwritten, so a value that receded before the close still leaves the reading that justified the
    /// alarm on the record. Only ever closes an OPEN episode, so a duplicate close is inert.
    /// </summary>
    public async Task CloseEpisodeAsync(string episodeId, long closedTs, double closeValue,
        double peakValue, string peakBand, string reason, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                UPDATE threshold_episode
                   SET closed_ts = $closed,
                       close_value = $closeValue,
                       peak_value = MAX(peak_value, $peak),
                       peak_band = $band,
                       close_reason = $reason
                 WHERE episode_id = $id AND closed_ts IS NULL;
                """;
            cmd.Parameters.AddWithValue("$id", episodeId);
            cmd.Parameters.AddWithValue("$closed", closedTs);
            cmd.Parameters.AddWithValue("$closeValue", closeValue);
            cmd.Parameters.AddWithValue("$peak", peakValue);
            cmd.Parameters.AddWithValue("$band", peakBand);
            cmd.Parameters.AddWithValue("$reason", reason);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Close every episode left open by a previous run, stamped with when this one started.
    /// </summary>
    /// <remarks>
    /// Dwell state lives only as long as the process, so an episode still open in this table when the daemon
    /// starts is one nothing will ever close — the evaluator has no memory of it, and the same condition
    /// coming back opens a NEW episode with a new id. Left alone they accumulate forever, each claiming a
    /// condition is still true. They are closed as <c>interrupted</c> rather than recovered because the
    /// value was never seen to come down: what ended was the recording, not necessarily the problem.
    /// <para>The close time is this run's start, not the last sample before the stop — that timestamp was
    /// never written down, and inventing one would put a duration in the record that nobody measured.</para>
    /// </remarks>
    public async Task<int> CloseOrphanedEpisodesAsync(long startedAtMs, string reason, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                UPDATE threshold_episode
                   SET closed_ts = $closed, close_reason = $reason
                 WHERE closed_ts IS NULL;
                """;
            cmd.Parameters.AddWithValue("$closed", startedAtMs);
            cmd.Parameters.AddWithValue("$reason", reason);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Episodes that opened or closed at/after <paramref name="sinceMs"/>, oldest event first. Both
    /// halves are matched because a consumer catching up after a gap needs the episodes that began in it
    /// AND the ones that ended in it, and an episode can have done only one of the two.
    /// </summary>
    public async Task<IReadOnlyList<EpisodeRow>> QueryEpisodesAsync(long sinceMs, int limit, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                SELECT episode_id, rule_key, metric, scope, ref, server_id, opened_ts, closed_ts,
                       peak_band, peak_value, open_value, close_value, threshold, producer, close_reason
                  FROM threshold_episode
                 WHERE opened_ts >= $since OR (closed_ts IS NOT NULL AND closed_ts >= $since)
                 ORDER BY opened_ts ASC
                 LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$since", sinceMs);
            cmd.Parameters.AddWithValue("$limit", limit);

            var rows = new List<EpisodeRow>();
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(new EpisodeRow(
                    EpisodeId: reader.GetString(0),
                    RuleKey: reader.GetString(1),
                    Metric: reader.GetString(2),
                    Scope: reader.GetString(3),
                    Ref: reader.IsDBNull(4) ? null : reader.GetString(4),
                    ServerId: reader.IsDBNull(5) ? null : reader.GetString(5),
                    OpenedTs: reader.GetInt64(6),
                    ClosedTs: reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    PeakBand: reader.GetString(8),
                    PeakValue: reader.GetDouble(9),
                    OpenValue: reader.GetDouble(10),
                    CloseValue: reader.IsDBNull(11) ? null : reader.GetDouble(11),
                    Threshold: reader.GetDouble(12),
                    Producer: reader.GetString(13),
                    CloseReason: reader.IsDBNull(14) ? null : reader.GetString(14)));
            }
            return rows;
        }
        finally { _gate.Release(); }
    }

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

    /// <summary>
    /// Aggregate every entity of one kind over a window — min, max, mean and the most recent value, in
    /// one query. Tier is chosen exactly as <see cref="QueryHistoryAsync"/> chooses it, so a summary and
    /// a series over the same range describe the same rows.
    /// </summary>
    /// <remarks>
    /// On the rollup tier the aggregate is taken over per-bucket extremes: a min of bucket minima is the
    /// true minimum of the samples beneath them, and the mean is weighted by each bucket's sample count
    /// rather than treating a sparse bucket as equal to a full one. An entity with nothing in the window
    /// yields no row — an absent entity is not one reading zero.
    /// </remarks>
    public async Task<MetricsSummaryResponse> QuerySummaryAsync(
        string entityKind, string? rangeStr, CancellationToken ct = default)
    {
        rangeStr ??= MetricsRange.OneHour;
        TimeSpan duration = MetricsRange.Parse(rangeStr) ?? TimeSpan.FromHours(1);

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long fromMs = nowMs - (long)duration.TotalMilliseconds;
        bool useRaw = duration.TotalHours <= _options.RawRetentionHours;

        var entries = new List<MetricsSummaryEntry>();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SqliteConnection conn = await EnsureAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = useRaw
                ? """
                  SELECT entity_id, metric, MIN(value), MAX(value), AVG(value), COUNT(*),
                         (SELECT value FROM sample s2
                           WHERE s2.entity_kind = s.entity_kind AND s2.entity_id = s.entity_id
                             AND s2.metric = s.metric AND s2.ts <= $to
                           ORDER BY s2.ts DESC LIMIT 1)
                  FROM sample s
                  WHERE entity_kind = $k AND ts >= $from AND ts <= $to
                  GROUP BY entity_id, metric
                  """
                : """
                  SELECT entity_id, metric, MIN(min), MAX(max), SUM(avg * n) / SUM(n), SUM(n),
                         (SELECT avg FROM rollup r2
                           WHERE r2.entity_kind = r.entity_kind AND r2.entity_id = r.entity_id
                             AND r2.metric = r.metric AND r2.bucket_ts <= $to
                           ORDER BY r2.bucket_ts DESC LIMIT 1)
                  FROM rollup r
                  WHERE entity_kind = $k AND bucket_ts >= $from AND bucket_ts <= $to
                  GROUP BY entity_id, metric
                  """;
            cmd.Parameters.AddWithValue("$k", entityKind);
            cmd.Parameters.AddWithValue("$from", fromMs);
            cmd.Parameters.AddWithValue("$to", nowMs);

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                entries.Add(new MetricsSummaryEntry(
                    EntityId: reader.GetString(0),
                    Metric: reader.GetString(1),
                    Min: reader.GetDouble(2),
                    Max: reader.GetDouble(3),
                    Avg: Math.Round(reader.GetDouble(4), 2),
                    Last: reader.IsDBNull(6) ? reader.GetDouble(3) : reader.GetDouble(6),
                    Samples: reader.GetInt64(5)));
            }
        }
        finally { _gate.Release(); }

        return new MetricsSummaryResponse(entityKind, rangeStr, useRaw ? "raw" : "rollup", entries);
    }

    public void Dispose()
    {
        _conn?.Dispose();
        _gate.Dispose();
    }
}
