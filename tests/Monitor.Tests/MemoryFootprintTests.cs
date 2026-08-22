using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Monitor.History;
using TheKrystalShip.KGSM.Monitor.Sampling;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// The memory sizing terms: parsing them out of the kernel's files, and accumulating them into the
/// durable per-instance record.
/// </summary>
/// <remarks>
/// The zero-valued <c>memory.events</c> and <c>memory.pressure</c> fixtures are real captures from a
/// live game-server cgroup on this host. Their non-zero twins (<c>*.oom.txt</c>, <c>*.stalled.txt</c>)
/// are hand-written in the same kernel format: reproducing the real thing would mean driving a live
/// server into an OOM kill, and the format is what is under test.
/// </remarks>
public class MemoryFootprintTests
{
    // ---- parsing ----

    [Fact]
    public void ParseMemStatAnon_reads_anon_from_a_real_memory_stat()
    {
        Assert.Equal(4869013504L, CgroupSampler.ParseMemStatAnon(Fixtures.Read("cgroup.memory.stat.txt")));
    }

    [Fact]
    public void ParseMemStatAnon_does_not_match_a_field_that_merely_starts_with_anon()
    {
        // anon_thp is a different quantity and sits below anon in the same file. Matching on a prefix
        // would return whichever came first.
        Assert.Equal(4869013504L, CgroupSampler.ParseMemStatAnon(Fixtures.Read("cgroup.memory.stat.txt")));
        Assert.Null(CgroupSampler.ParseMemStatAnon("anon_thp 815792128\nfile 42\n"));
    }

    [Fact]
    public void ParseMemStatAnon_is_null_when_the_field_is_absent()
    {
        // Null, not 0: a kernel that does not report the field has not reported a workload holding nothing.
        Assert.Null(CgroupSampler.ParseMemStatAnon("file 131567616\nslab 1431632\n"));
    }

    [Fact]
    public void ParseMemEvents_reads_zeroes_from_a_healthy_cgroup()
    {
        var (oom, max) = CgroupSampler.ParseMemEvents(Fixtures.Read("cgroup.memory.events.txt"));
        Assert.Equal(0L, oom);
        Assert.Equal(0L, max);
    }

    [Fact]
    public void ParseMemEvents_reads_oom_kill_without_folding_in_oom_group_kill()
    {
        var (oom, max) = CgroupSampler.ParseMemEvents(Fixtures.Read("cgroup.memory.events.oom.txt"));
        Assert.Equal(3L, oom);   // oom_kill, not the oom=7 above it or oom_group_kill below it
        Assert.Equal(128L, max);
    }

    [Fact]
    public void ParseMemPressureFull_reads_the_full_line_not_the_some_line()
    {
        var (avg, total) = CgroupSampler.ParseMemPressureFull(Fixtures.Read("cgroup.memory.pressure.stalled.txt"));
        Assert.Equal(4.35, avg!.Value, 3);          // full avg60 — some avg60 is 8.97
        Assert.Equal(44219501L, total);             // full total — some total is 91847263
    }

    [Fact]
    public void ParseMemPressureFull_reads_a_real_unstalled_cgroup_as_zero()
    {
        // Measured zero, not absent: this cgroup has PSI and is comfortable, which is a different fact
        // from a kernel that reports no pressure at all.
        var (avg, total) = CgroupSampler.ParseMemPressureFull(Fixtures.Read("cgroup.memory.pressure.txt"));
        Assert.Equal(0.0, avg!.Value, 3);
        Assert.Equal(29102L, total);
    }

    [Fact]
    public void ParseMemPressureFull_is_null_when_psi_is_not_compiled_in()
    {
        var (avg, total) = CgroupSampler.ParseMemPressureFull("");
        Assert.Null(avg);
        Assert.Null(total);
    }

    [Fact]
    public void ParseStatusRssAnonKb_reads_anonymous_rss_not_total_rss()
    {
        // VmRSS is 5556 kB in the same fixture; the anonymous half is what a working set is built from.
        Assert.Equal(308L, ProcTreeSampler.ParseStatusRssAnonKb(Fixtures.Read("proc.status.rssanon.txt")));
    }

    [Fact]
    public void ParseStatusRssAnonKb_is_null_when_the_kernel_does_not_break_it_out()
    {
        Assert.Null(ProcTreeSampler.ParseStatusRssAnonKb("Name:\tsh\nVmRSS:\t 5556 kB\n"));
    }

    // ---- counter accumulation ----

    [Fact]
    public void CounterDelta_banks_only_what_is_new()
    {
        Assert.Equal(2L, FootprintRecorder.CounterDelta(last: 5, current: 7, restarted: false));
        Assert.Equal(0L, FootprintRecorder.CounterDelta(last: 5, current: 5, restarted: false));
    }

    [Fact]
    public void CounterDelta_adopts_a_first_reading_as_a_baseline()
    {
        // Whatever the counter already holds happened at a time this record cannot state. Banking it
        // would date those events to now.
        Assert.Equal(0L, FootprintRecorder.CounterDelta(last: null, current: 9, restarted: false));
    }

    [Fact]
    public void CounterDelta_takes_the_whole_value_after_a_restart()
    {
        // The cgroup is new, so its counter began at zero and everything in it is new — even when the
        // value is below what the previous run had reached.
        Assert.Equal(2L, FootprintRecorder.CounterDelta(last: 5, current: 2, restarted: true));
        Assert.Equal(6L, FootprintRecorder.CounterDelta(last: 5, current: 6, restarted: true));
    }

    [Fact]
    public void CounterDelta_treats_a_value_going_backwards_as_a_reset_even_unflagged()
    {
        // A cgroup can be replaced between two frames with neither absence nor a peak drop being visible.
        // A counter that fell is still proof the thing counting it started over.
        Assert.Equal(2L, FootprintRecorder.CounterDelta(last: 5, current: 2, restarted: false));
    }

    [Fact]
    public void CounterDelta_is_zero_when_the_counter_is_not_measured()
    {
        Assert.Equal(0L, FootprintRecorder.CounterDelta(last: 5, current: null, restarted: false));
        Assert.Equal(0L, FootprintRecorder.CounterDelta(last: 5, current: null, restarted: true));
    }

    // ---- the durable record ----

    private static (HistoryStore store, string db) NewStore()
    {
        string db = Path.Combine(Path.GetTempPath(), $"kgsm-monitor-fp-{Guid.NewGuid():N}.db");
        var opts = new MonitorOptions { HistoryDbPath = db };
        return (new HistoryStore(opts, NullLogger<HistoryStore>.Instance), db);
    }

    private static void Cleanup(HistoryStore store, string db)
    {
        store.Dispose();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
            File.Delete(db + suffix);
    }

    private static FootprintRow Row(string id, long peak = 100, long oom = 0) => new(
        InstanceId: id, FirstSeen: 1000, LastSeen: 2000, Runs: 2, UptimeMs: 60_000, Samples: 4,
        AnonMax: 900, AnonSum: 3200, PeakBytes: peak, OomKills: oom, MaxEvents: 1, StallTotalUsec: 250,
        LastPeak: peak, LastOomKills: oom, LastMaxEvents: 1, LastStallTotal: 250);

    [Fact]
    public async Task Footprint_round_trips_including_the_accumulator_state()
    {
        var (store, db) = NewStore();
        try
        {
            await store.WriteFootprintsAsync([Row("factorio-a", peak: 512, oom: 3)]);

            IReadOnlyList<FootprintRow> rows = await store.QueryFootprintsAsync();
            FootprintRow row = Assert.Single(rows);

            Assert.Equal("factorio-a", row.InstanceId);
            Assert.Equal(512, row.PeakBytes);
            Assert.Equal(3, row.OomKills);
            // The baselines have to survive the trip or a restart re-banks what it already counted.
            Assert.Equal(512, row.LastPeak);
            Assert.Equal(3, row.LastOomKills);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Writing_the_same_instance_again_replaces_rather_than_duplicates()
    {
        var (store, db) = NewStore();
        try
        {
            await store.WriteFootprintsAsync([Row("terraria", peak: 100)]);
            await store.WriteFootprintsAsync([Row("terraria", peak: 400)]);

            FootprintRow row = Assert.Single(await store.QueryFootprintsAsync());
            Assert.Equal(400, row.PeakBytes);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Reconcile_drops_instances_that_no_longer_exist()
    {
        var (store, db) = NewStore();
        try
        {
            await store.WriteFootprintsAsync([Row("live-one"), Row("__bp_probe_gone__"), Row("wdtest")]);

            int dropped = await store.ReconcileFootprintsAsync(["live-one"]);

            Assert.Equal(2, dropped);
            Assert.Equal("live-one", Assert.Single(await store.QueryFootprintsAsync()).InstanceId);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Reconcile_on_an_empty_watch_list_deletes_nothing()
    {
        // An empty list means the engine did not answer just as much as it means nothing is installed.
        // Acting on it would erase the record this whole feature exists to keep.
        var (store, db) = NewStore();
        try
        {
            await store.WriteFootprintsAsync([Row("Ketchup"), Row("romestead")]);

            Assert.Equal(0, await store.ReconcileFootprintsAsync([]));
            Assert.Equal(2, (await store.QueryFootprintsAsync()).Count);
        }
        finally { Cleanup(store, db); }
    }

    [Fact]
    public async Task Pruning_the_series_never_touches_a_footprint()
    {
        // The whole point of the record: it answers questions the windows have already forgotten.
        var (store, db) = NewStore();
        try
        {
            await store.WriteFootprintsAsync([Row("Ketchup")]);
            long farFuture = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeMilliseconds();

            await store.PruneRawAsync(farFuture);
            await store.PruneRollupsAsync(farFuture);
            await store.PruneEpisodesAsync(farFuture);

            Assert.Single(await store.QueryFootprintsAsync());
        }
        finally { Cleanup(store, db); }
    }

    // ---- the rendered view ----

    [Fact]
    public void Dto_reports_a_mean_over_zero_samples_as_null()
    {
        FootprintRow row = Row("never-measured") with { Samples = 0, AnonSum = 0, AnonMax = null };
        FootprintDto dto = FootprintDto.From(row);

        Assert.Null(dto.WorkingSetAvgBytes);
        Assert.Null(dto.WorkingSetPeakBytes);
    }

    [Fact]
    public void Dto_converts_the_accumulated_units_and_drops_the_bookkeeping()
    {
        FootprintRow row = Row("necesse") with { Samples = 4, AnonSum = 4000, UptimeMs = 5_400_000, StallTotalUsec = 1_500_000 };
        FootprintDto dto = FootprintDto.From(row);

        Assert.Equal(1000, dto.WorkingSetAvgBytes);
        Assert.Equal(1.5, dto.ObservedHours);
        Assert.Equal(1.5, dto.StallSeconds);
    }
}
