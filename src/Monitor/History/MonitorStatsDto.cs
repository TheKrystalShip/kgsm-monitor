namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// What this recorder is doing and what it has actually recorded — served from <c>GET /stats</c> and
/// relayed verbatim by kgsm-api to the Control Panel's monitor page.
/// <para>
/// Every other endpoint here answers "what are the numbers". This one answers "is the thing that
/// produces them working", which nothing else in the ecosystem could tell you: whether the sample
/// cadence is holding, how much of the host is actually covered, and — the question with no other
/// source — whether history is retaining what it was configured to retain.
/// </para>
/// </summary>
/// <param name="IntervalMs">The nominal sample interval the daemon is configured with.</param>
/// <param name="LatestSampleMs">When the newest published frame was built (unix ms), or null before the
/// first tick lands. Compared against the interval by the reader, not here.</param>
/// <param name="UptimeSec">How long this daemon process has been sampling.</param>
/// <param name="Coverage">What the newest frame actually measured.</param>
/// <param name="History">The history store, or null when history is switched off — which is a
/// configuration, not a failure, and is worded as one by the reader.</param>
public sealed record MonitorStats(
    int IntervalMs,
    long? LatestSampleMs,
    long UptimeSec,
    SampleCoverage Coverage,
    HistoryStats? History);

/// <summary>
/// How much of the host the newest frame covered, counted from the frame itself rather than from what
/// the daemon was configured to watch. A source that is switched off or found nothing reports 0 here and
/// says which through its own <c>*Enabled</c> flag — the two are separate facts, and a zero with no flag
/// beside it cannot distinguish "nothing to measure" from "not measuring".
/// </summary>
/// <param name="Servers">Game servers with a row in the newest frame.</param>
/// <param name="Leaves">KGSM leaves with a row in the newest frame (running + resolvable ones only).</param>
/// <param name="Sensors">hwmon temperature readings found on this host.</param>
/// <param name="Cores">CPU cores reported per-core in the newest frame.</param>
/// <param name="ServersEnabled">Per-server sampling is wired (the engine path is configured).</param>
/// <param name="LeavesEnabled">Per-leaf sampling is wired.</param>
public sealed record SampleCoverage(
    int Servers,
    int Leaves,
    int Sensors,
    int Cores,
    bool ServersEnabled,
    bool LeavesEnabled);

/// <summary>
/// The history store: what it was told to keep, and what it is measurably keeping.
/// <para>
/// The two are reported side by side on purpose. <see cref="RawRetentionHours"/> is an intent;
/// <see cref="RawOldestMs"/> is a measurement, and a span shorter than the window is the ordinary
/// consequence of downtime or a store younger than its retention — while a span *longer* than it means
/// maintenance is not running. Neither is derivable from the other, so both are on the wire.
/// </para>
/// </summary>
/// <param name="DbPath">Where the store lives on this host.</param>
/// <param name="DbBytes">The database plus its WAL/shm sidecars, or null when unreadable.</param>
/// <param name="RawRetentionHours">Configured raw-tier window.</param>
/// <param name="RollupStepMin">Configured rollup bucket size.</param>
/// <param name="RollupRetentionDays">Configured rollup-tier window.</param>
/// <param name="MaintenanceMs">How often rollup + prune + vacuum runs.</param>
/// <param name="LastMaintenanceMs">When that last completed (unix ms), or null when it hasn't yet.</param>
/// <param name="LastMaintenanceOk">Whether the last pass completed without throwing. Null before the
/// first pass — never an optimistic true.</param>
/// <param name="RawRows">Rows currently in the raw tier.</param>
/// <param name="RawEntities">Distinct entities in the raw tier (not rows — one server is one entity).</param>
/// <param name="RawOldestMs">Oldest raw sample actually on disk, or null when the tier is empty.</param>
/// <param name="RawNewestMs">Newest raw sample actually on disk.</param>
/// <param name="RollupRows">Rows currently in the rollup tier.</param>
/// <param name="RollupEntities">Distinct entities in the rollup tier.</param>
/// <param name="RollupOldestMs">Oldest rollup bucket actually on disk.</param>
/// <param name="RollupNewestMs">Newest rollup bucket actually on disk.</param>
public sealed record HistoryStats(
    string DbPath,
    long? DbBytes,
    int RawRetentionHours,
    int RollupStepMin,
    int RollupRetentionDays,
    int MaintenanceMs,
    long? LastMaintenanceMs,
    bool? LastMaintenanceOk,
    long RawRows,
    long RawEntities,
    long? RawOldestMs,
    long? RawNewestMs,
    long RollupRows,
    long RollupEntities,
    long? RollupOldestMs,
    long? RollupNewestMs);

/// <summary>The raw store-side half of <see cref="HistoryStats"/>, before the configured intents are
/// joined onto it. Kept separate so <see cref="HistoryStore"/> reports only what it can measure.</summary>
public sealed record HistoryStoreStats(
    string DbPath,
    long? DbBytes,
    long RawRows,
    long? RawOldestMs,
    long? RawNewestMs,
    long RawEntities,
    long RollupRows,
    long? RollupOldestMs,
    long? RollupNewestMs,
    long RollupEntities);
