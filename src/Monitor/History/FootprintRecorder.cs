using TheKrystalShip.KGSM.Monitor.Contracts;
using TheKrystalShip.KGSM.Monitor.Sampling;

namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// Accumulates each instance's memory footprint — the durable statement of what it has been measured
/// to hold, as opposed to the windowed series it is built from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> Samples last a day and rollups a month. A question like "what does
/// this world need after two weeks of progress" is asked of an instance's whole life, and the series
/// that could answer it has already been pruned. So the answer is accumulated as it is measured, in
/// place, and never re-derived.
/// </para>
/// <para>
/// <b>It reads the same frame everything else does</b> (<see cref="MetricsSampler.Latest"/>, never a
/// second scrape) on the persist cadence, and writes one batched transaction per tick — only for the
/// instances whose row actually changed.
/// </para>
/// <para>
/// <b>What it will not claim.</b> Time the daemon was down is not counted as uptime: the instance may
/// well have been running, but nothing here saw it. A counter observed for the first time is adopted as
/// a baseline rather than banked, because those events happened at a time this record cannot state.
/// And an instance that stops and starts again entirely between two ticks is one run boundary this
/// misses — both of its signals (an absence, and the kernel's high-water mark going backwards) need the
/// gap to be visible in a frame.
/// </para>
/// <para>
/// <b>A cgroup that dies inside one tick takes its counters with it.</b> They live in the cgroup and
/// are gone the moment it is torn down, so an instance killed during boot — capped below what it
/// allocates, spawned and dead in the same second — contributes nothing here. What is counted is the
/// case that sizing turns on: a server that grows into its ceiling over hours. A server that cannot
/// start under its ceiling fails its start, which the supervisor reports.
/// </para>
/// </remarks>
public sealed class FootprintRecorder : BackgroundService
{
    private readonly MetricsSampler _sampler;
    private readonly HistoryStore _store;
    private readonly MonitorOptions _options;
    private readonly ILogger<FootprintRecorder> _logger;
    private readonly MonitorJournal? _journal;
    private readonly ServerSampler? _servers;

    /// <summary>The accumulated rows, mirrored in memory so a tick costs one write and no read.</summary>
    private readonly Dictionary<string, FootprintRow> _rows = new(StringComparer.Ordinal);

    /// <summary>Which instances were in the previous frame. An id arriving that is not in here has
    /// started since, which is one of the two run-boundary signals.</summary>
    private HashSet<string> _previous = new(StringComparer.Ordinal);

    private long _lastTickMs;
    private bool _hasTicked;

    /// <summary>
    /// How often the footprint rows are reconciled against the instances that still exist, in ticks.
    /// </summary>
    /// <remarks>
    /// Removing an instance is rare and the cost of noticing late is a stale row, so this rides a slow
    /// multiple of the persist cadence rather than asking the engine on every tick.
    /// </remarks>
    private const int ReconcileEveryTicks = 240;

    private int _tick;

    public FootprintRecorder(
        MetricsSampler sampler,
        HistoryStore store,
        MonitorOptions options,
        ILogger<FootprintRecorder> logger,
        MonitorJournal? journal = null,
        ServerSampler? servers = null)
    {
        _sampler = sampler;
        _store = store;
        _options = options;
        _logger = logger;
        _journal = journal;
        _servers = servers;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The persisted rows ARE the accumulator's state, including the counter baselines. Loading them
        // before the first tick is what stops a restart from re-banking a counter or inventing a run.
        try
        {
            foreach (FootprintRow row in await _store.QueryFootprintsAsync(stoppingToken).ConfigureAwait(false))
                _rows[row.InstanceId] = row;
            _logger.LogInformation("footprint: resumed {Count} instance record(s)", _rows.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unreadable store is not a reason to stop measuring: the rows rebuild from this tick on,
            // and saying so is what keeps a restarted-from-zero record from being read as a real one.
            _logger.LogWarning(ex, "footprint: could not resume the existing records — accumulating from now");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.PersistMs));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await TickAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "footprint: tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* app stopping */ }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        Snapshot? snap = _sampler.Latest;
        if (snap is null)
            return;

        long now = snap.Ts;

        // A frame is conflated, so the same one can be read twice. Re-folding it would double-count every
        // counter delta and every millisecond of uptime.
        if (_hasTicked && now <= _lastTickMs)
            return;

        long elapsed = _hasTicked ? now - _lastTickMs : 0;
        var present = new HashSet<string>(StringComparer.Ordinal);
        var changed = new List<FootprintRow>();
        var oomEvents = new List<(string Instance, long Kills, long Total, ServerMemory Memory)>();

        foreach (ServerMetrics sm in snap.Servers)
        {
            present.Add(sm.Id);
            if (sm.Memory is not { } mem)
                continue; // measured, but this kind exposes none of the sizing terms

            bool known = _rows.TryGetValue(sm.Id, out FootprintRow row);
            if (!known)
                row = new FootprintRow(sm.Id, now, now, 0, 0, 0, null, 0, null, 0, 0, 0, null, null, null, null);

            bool wasPresent = _previous.Contains(sm.Id);

            // Two independent signals that the cgroup this instance runs in is a new one: it was not in
            // the previous frame, or the kernel's own high-water mark went backwards, which nothing but a
            // fresh cgroup does. Either means the counters below restarted from zero.
            bool peakWentBack = row.LastPeak is { } lastPeak && mem.PeakBytes is { } peak && peak < lastPeak;
            bool restarted = peakWentBack || (_hasTicked && !wasPresent);

            long runs = row.Runs + (restarted ? 1 : 0);

            // Uptime is only credited for a gap this daemon was present for at both ends. The cap keeps a
            // late tick from crediting more time than the cadence it was supposed to run at.
            long uptime = row.UptimeMs;
            if (wasPresent && elapsed > 0)
                uptime += Math.Min(elapsed, _options.PersistMs * 2L);

            long samples = row.Samples;
            double anonSum = row.AnonSum;
            double? anonMax = row.AnonMax;
            if (mem.AnonBytes is { } anon)
            {
                // The working set includes what has been pushed to swap: a server whose pages were
                // evicted still holds them, and counting only what is resident would report the eviction
                // as the workload shrinking.
                double working = anon + (mem.SwapBytes ?? 0);
                samples++;
                anonSum += working;
                anonMax = anonMax is { } m && m >= working ? m : working;
            }

            double? peakBytes = row.PeakBytes;
            if (mem.PeakBytes is { } p)
                peakBytes = peakBytes is { } q && q >= p ? q : p;

            long oomDelta = CounterDelta(row.LastOomKills, mem.OomKills, restarted);
            long maxDelta = CounterDelta(row.LastMaxEvents, mem.MaxEvents, restarted);
            long stallDelta = CounterDelta(row.LastStallTotal, mem.StallTotalUsec, restarted);

            FootprintRow updated = row with
            {
                FirstSeen = known ? row.FirstSeen : now,
                LastSeen = now,
                Runs = runs,
                UptimeMs = uptime,
                Samples = samples,
                AnonMax = anonMax,
                AnonSum = anonSum,
                PeakBytes = peakBytes,
                OomKills = row.OomKills + oomDelta,
                MaxEvents = row.MaxEvents + maxDelta,
                StallTotalUsec = row.StallTotalUsec + stallDelta,
                LastPeak = mem.PeakBytes ?? row.LastPeak,
                LastOomKills = mem.OomKills ?? row.LastOomKills,
                LastMaxEvents = mem.MaxEvents ?? row.LastMaxEvents,
                LastStallTotal = mem.StallTotalUsec ?? row.LastStallTotal,
            };

            _rows[sm.Id] = updated;
            changed.Add(updated);

            if (oomDelta > 0)
                oomEvents.Add((sm.Id, oomDelta, updated.OomKills, mem));
        }

        _previous = present;
        _lastTickMs = now;
        _hasTicked = true;

        if (changed.Count > 0)
            await _store.WriteFootprintsAsync(changed, ct).ConfigureAwait(false);

        // Recorded after the write, so the journal never announces a kill the store has not kept.
        foreach ((string instance, long kills, long total, ServerMemory mem) in oomEvents)
        {
            _logger.LogWarning(
                "{Instance}: the kernel killed {Kills} process(es) in its cgroup for want of memory "
                + "({Total} since this host started counting)", instance, kills, total);
            _journal?.RecordOom(instance, kills, total, mem);
        }

        if (++_tick % ReconcileEveryTicks == 0)
            await ReconcileAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Drop the rows of instances that no longer exist.
    /// </summary>
    /// <remarks>
    /// The watch-list, not the frame: a stopped instance is still an instance, and its accumulated
    /// footprint is exactly what a later comparison is for. Only an instance that has been removed
    /// loses its record.
    /// </remarks>
    private async Task ReconcileAsync(CancellationToken ct)
    {
        IReadOnlyCollection<string>? live = _servers?.WatchedIds;
        if (live is null || live.Count == 0)
            return;

        int dropped = await _store.ReconcileFootprintsAsync(live, ct).ConfigureAwait(false);
        if (dropped == 0)
            return;

        foreach (string id in _rows.Keys.Where(k => !live.Contains(k)).ToList())
            _rows.Remove(id);
    }

    /// <summary>
    /// How much of a monotonic cgroup counter is new since it was last read.
    /// </summary>
    /// <remarks>
    /// A counter never seen before is adopted as a baseline and contributes nothing: whatever it holds
    /// happened at a time this record cannot state, and banking it would date those events to now. After
    /// a restart the counter began again at zero, so everything it currently holds is new.
    /// </remarks>
    internal static long CounterDelta(long? last, long? current, bool restarted)
    {
        if (current is not { } c) return 0;
        if (restarted) return c;
        if (last is not { } l) return 0;
        return c >= l ? c - l : c;
    }
}
