using TheKrystalShip.KGSM.Monitor.Contracts;
using TheKrystalShip.KGSM.Monitor.Thresholds;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// The threshold state machine, driven with crafted frames and controlled time. What is pinned here is
/// the anti-flap behaviour, because it is the whole reason the evaluator is not a plain comparison: a
/// spike under the fire dwell never opens; a sustained breach opens once and keeps its identity across
/// band changes; a value in the hysteresis deadband neither opens nor closes; a not-evaluable field is
/// held rather than treated as clear; and a target that vanishes runs the clear dwell before closing.
/// <para>
/// Frames are synthetic and time is passed in through <see cref="Snapshot.Ts"/>, so a dwell measured in
/// minutes is tested in microseconds and nothing here depends on the host it runs on.
/// </para>
/// </summary>
public class ConditionEvaluatorTests
{
    private const long T0 = 1_767_225_600_000; // 2026-01-01T00:00:00Z, unix ms

    // --- fire dwell ---------------------------------------------------------------------------------

    [Fact]
    public void Spike_under_the_fire_dwell_never_opens()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(MemRule(warn: 90, fireForSec: 60));

        Assert.Empty(evaluator.Evaluate(policy, MemFrame(95, T0)));                 // breach begins
        Assert.Empty(evaluator.Evaluate(policy, MemFrame(95, T0 + Secs(30))));      // still short of 60s
        Assert.Empty(evaluator.Evaluate(policy, MemFrame(10, T0 + Secs(35))));      // recovered first
        Assert.Empty(evaluator.Evaluate(policy, MemFrame(95, T0 + Secs(40))));      // clock restarts
    }

    [Fact]
    public void Sustained_breach_opens_after_the_dwell_and_is_dated_from_its_first_reading()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(MemRule(warn: 90, fireForSec: 60));

        Assert.Empty(evaluator.Evaluate(policy, MemFrame(95, T0)));

        ConditionReading c = Assert.Single(evaluator.Evaluate(policy, MemFrame(95, T0 + Secs(61))));
        Assert.Equal("host-mem", c.RuleKey);
        Assert.Equal("HostMemUsedPct", c.Metric);
        Assert.Equal("host", c.Scope);
        Assert.Null(c.Ref);
        Assert.Null(c.ServerId);
        Assert.Equal(ConditionBand.Warn, c.Band);
        Assert.Equal(90, c.Threshold);

        // Dated from when it STARTED being wrong, not from when the dwell completed. This is the field an
        // operator reads as "how long has this been going on", and the answer is 61s, not 1s.
        Assert.Equal(T0, c.Since);
    }

    [Fact]
    public void A_zero_fire_dwell_opens_on_the_first_reading_over_the_line()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(MemRule(warn: 90, fireForSec: 0));

        ConditionReading c = Assert.Single(evaluator.Evaluate(policy, MemFrame(95, T0)));
        Assert.Equal(T0, c.Since);
    }

    // --- windowMax ----------------------------------------------------------------------------------

    [Fact]
    public void WindowMax_reports_the_peak_since_the_breach_opened_not_the_latest_reading()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(MemRule(warn: 90, fireForSec: 0));

        evaluator.Evaluate(policy, MemFrame(91, T0));
        evaluator.Evaluate(policy, MemFrame(99, T0 + Secs(1)));
        ConditionReading c = Assert.Single(evaluator.Evaluate(policy, MemFrame(92, T0 + Secs(2))));

        // The distinction the whole relocation exists for: a scraper polling slower than the sample rate
        // would have seen 92 and never known about 99.
        Assert.Equal(92, c.Value);
        Assert.Equal(99, c.WindowMax);
    }

    [Fact]
    public void WindowMax_resets_when_a_new_episode_opens()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(MemRule(warn: 90, fireForSec: 0, clearForSec: 0, clearMargin: 5));

        evaluator.Evaluate(policy, MemFrame(99, T0));                       // peak 99
        Assert.Empty(evaluator.Evaluate(policy, MemFrame(50, T0 + Secs(1)))); // cleared

        ConditionReading c = Assert.Single(evaluator.Evaluate(policy, MemFrame(91, T0 + Secs(2))));
        Assert.Equal(91, c.WindowMax); // not 99 — that was a different episode
    }

    // --- bands and episode identity -----------------------------------------------------------------

    [Fact]
    public void Crossing_into_danger_changes_band_but_keeps_the_same_episode()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(MemRule(warn: 90, danger: 97, fireForSec: 0));

        ConditionReading warn = Assert.Single(evaluator.Evaluate(policy, MemFrame(95, T0)));
        Assert.Equal(ConditionBand.Warn, warn.Band);
        Assert.Equal(90, warn.Threshold);

        ConditionReading danger = Assert.Single(evaluator.Evaluate(policy, MemFrame(98, T0 + Secs(1))));
        Assert.Equal(ConditionBand.Danger, danger.Band);
        Assert.Equal(97, danger.Threshold);

        // Same problem getting worse, not a new problem — a consumer upserts on this id rather than
        // raising a second alert beside the first.
        Assert.Equal(warn.EpisodeId, danger.EpisodeId);
        Assert.Equal(warn.Since, danger.Since);
    }

    [Fact]
    public void An_episode_that_clears_and_recurs_gets_a_new_id()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(MemRule(warn: 90, fireForSec: 0, clearForSec: 0, clearMargin: 5));

        ConditionReading first = Assert.Single(evaluator.Evaluate(policy, MemFrame(95, T0)));
        Assert.Empty(evaluator.Evaluate(policy, MemFrame(50, T0 + Secs(1))));
        ConditionReading second = Assert.Single(evaluator.Evaluate(policy, MemFrame(95, T0 + Secs(2))));

        Assert.NotEqual(first.EpisodeId, second.EpisodeId);
    }

    // --- clear dwell and deadband -------------------------------------------------------------------

    [Fact]
    public void Clearing_needs_both_the_margin_and_the_dwell()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(MemRule(warn: 90, fireForSec: 0, clearForSec: 60, clearMargin: 5));

        Assert.Single(evaluator.Evaluate(policy, MemFrame(95, T0)));
        Assert.Single(evaluator.Evaluate(policy, MemFrame(80, T0 + Secs(1))));   // past margin, dwell armed
        Assert.Single(evaluator.Evaluate(policy, MemFrame(80, T0 + Secs(30))));  // dwell not met
        Assert.Empty(evaluator.Evaluate(policy, MemFrame(80, T0 + Secs(62))));   // met → closed
    }

    [Fact]
    public void A_value_in_the_deadband_holds_an_open_condition_without_flapping()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(MemRule(warn: 90, fireForSec: 0, clearForSec: 1, clearMargin: 10));

        Assert.Single(evaluator.Evaluate(policy, MemFrame(95, T0)));

        // 85 is below warn but inside the 10-wide deadband: not breaching, not recovered. It stays open
        // however long it sits there, which is exactly what stops a value parked on the line from
        // alternating open/closed forever.
        for (int i = 1; i <= 10; i++)
            Assert.Single(evaluator.Evaluate(policy, MemFrame(85, T0 + Secs(i))));

        Assert.Single(evaluator.Evaluate(policy, MemFrame(79, T0 + Secs(20))));  // past the margin, arming
        Assert.Empty(evaluator.Evaluate(policy, MemFrame(79, T0 + Secs(22))));   // dwell met → closed
    }

    [Fact]
    public void The_clear_dwell_restarts_if_the_value_comes_back_into_the_deadband()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(MemRule(warn: 90, fireForSec: 0, clearForSec: 60, clearMargin: 10));

        Assert.Single(evaluator.Evaluate(policy, MemFrame(95, T0)));
        Assert.Single(evaluator.Evaluate(policy, MemFrame(70, T0 + Secs(1))));   // recovered, dwell armed
        Assert.Single(evaluator.Evaluate(policy, MemFrame(85, T0 + Secs(30))));  // back into the deadband

        // The rule's contract is that the value stays below the CLEAR threshold for the dwell. It did not,
        // so the clock starts again from here rather than counting the deadband time toward recovery.
        Assert.Single(evaluator.Evaluate(policy, MemFrame(70, T0 + Secs(62))));
        Assert.Empty(evaluator.Evaluate(policy, MemFrame(70, T0 + Secs(123))));
    }

    [Fact]
    public void A_breach_during_the_clear_dwell_cancels_the_clear()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(MemRule(warn: 90, fireForSec: 0, clearForSec: 60, clearMargin: 5));

        ConditionReading opened = Assert.Single(evaluator.Evaluate(policy, MemFrame(95, T0)));
        Assert.Single(evaluator.Evaluate(policy, MemFrame(80, T0 + Secs(1))));   // clearing
        ConditionReading again = Assert.Single(evaluator.Evaluate(policy, MemFrame(95, T0 + Secs(30))));
        Assert.Single(evaluator.Evaluate(policy, MemFrame(95, T0 + Secs(120))));  // would have closed by now

        Assert.Equal(opened.EpisodeId, again.EpisodeId); // never closed, so never a second episode
    }

    // --- honest-unknown -----------------------------------------------------------------------------

    [Fact]
    public void A_field_that_is_not_evaluable_holds_rather_than_clearing()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(new ThresholdRule("host-swap", ThresholdMetric.HostSwapUsedPct,
            Warn: 50, Danger: null, FireForSec: 0, ClearForSec: 0, ClearMargin: 5, Enabled: true));

        Snapshot withSwap = FrameWith(T0, mem: Mem(usedPct: 10, swapTotalKb: 1000, swapUsedKb: 900));
        Assert.Single(evaluator.Evaluate(policy, withSwap));

        // Swap turned off entirely: the rule is no longer evaluable. That is not the same statement as
        // "swap usage is fine", so the condition is neither closed nor advanced toward closing.
        Snapshot noSwap = FrameWith(T0 + Secs(600), mem: Mem(usedPct: 10, swapTotalKb: 0, swapUsedKb: 0));
        Assert.Single(evaluator.Evaluate(policy, noSwap));
    }

    [Fact]
    public void A_rule_with_no_targets_produces_nothing_rather_than_failing()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(new ThresholdRule("host-temp", ThresholdMetric.HostTempC,
            Warn: 85, Danger: null, FireForSec: 0, ClearForSec: 0, ClearMargin: 5, Enabled: true));

        Assert.Empty(evaluator.Evaluate(policy, FrameWith(T0, sensors: [])));
    }

    // --- fan-out ------------------------------------------------------------------------------------

    [Fact]
    public void A_fan_out_rule_tracks_each_target_independently()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(new ThresholdRule("host-disk", ThresholdMetric.HostDiskUsedPct,
            Warn: 90, Danger: null, FireForSec: 0, ClearForSec: 0, ClearMargin: 3, Enabled: true));

        Snapshot frame = FrameWith(T0, mounts:
        [
            new MountUsage("/", "ext4", 100, 95, 95, null),
            new MountUsage("/data", "ext4", 100, 50, 50, null),
        ]);

        ConditionReading c = Assert.Single(evaluator.Evaluate(policy, frame));
        Assert.Equal("/", c.Ref);

        Snapshot both = FrameWith(T0 + Secs(1), mounts:
        [
            new MountUsage("/", "ext4", 100, 95, 95, null),
            new MountUsage("/data", "ext4", 100, 99, 99, null),
        ]);

        ConditionReading[] two = evaluator.Evaluate(policy, both);
        Assert.Equal(2, two.Length);
        Assert.Equal(2, two.Select(x => x.EpisodeId).Distinct().Count());
        Assert.Contains(two, x => x.Ref == "/");
        Assert.Contains(two, x => x.Ref == "/data");
    }

    [Fact]
    public void Sensors_are_referenced_by_chip_and_label()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(new ThresholdRule("host-temp", ThresholdMetric.HostTempC,
            Warn: 85, Danger: null, FireForSec: 0, ClearForSec: 0, ClearMargin: 5, Enabled: true));

        Snapshot frame = FrameWith(T0, sensors:
        [
            new SensorReading("k10temp", "Tctl", 90),
            new SensorReading("nvme", null, 91),
        ]);

        ConditionReading[] conditions = evaluator.Evaluate(policy, frame);
        Assert.Equal(2, conditions.Length);
        Assert.Contains(conditions, c => c.Ref == "k10temp/Tctl");
        Assert.Contains(conditions, c => c.Ref == "nvme");   // no label — the chip alone
    }

    // --- per-server ---------------------------------------------------------------------------------

    [Fact]
    public void A_server_rule_carries_the_instance_and_the_server_scope()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(new ThresholdRule("srv-pids", ThresholdMetric.ServerPids,
            Warn: 100, Danger: null, FireForSec: 0, ClearForSec: 0, ClearMargin: 0, Enabled: true));

        ConditionReading c = Assert.Single(evaluator.Evaluate(policy, FrameWith(T0, servers: [Server("factorio-test", pids: 500)])));
        Assert.Equal("server", c.Scope);
        Assert.Equal("factorio-test", c.ServerId);
        Assert.Null(c.Ref);
    }

    [Fact]
    public void A_vanished_target_runs_the_clear_dwell_before_closing()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(new ThresholdRule("srv-pids", ThresholdMetric.ServerPids,
            Warn: 100, Danger: null, FireForSec: 0, ClearForSec: 60, ClearMargin: 0, Enabled: true));

        Assert.Single(evaluator.Evaluate(policy, FrameWith(T0, servers: [Server("factorio-test", pids: 500)])));

        // The server stopped. The monitor is up and this row is genuinely gone, which is a transition —
        // so it clears, but on the same dwell as a value that receded rather than instantly.
        Assert.Single(evaluator.Evaluate(policy, FrameWith(T0 + Secs(1), servers: [])));
        Assert.Empty(evaluator.Evaluate(policy, FrameWith(T0 + Secs(62), servers: [])));
    }

    // --- policy changes -----------------------------------------------------------------------------

    [Fact]
    public void Disabling_a_rule_stops_reporting_its_conditions()
    {
        var evaluator = new ConditionEvaluator();
        var on = Policy(MemRule(warn: 90, fireForSec: 0));
        var off = Policy(MemRule(warn: 90, fireForSec: 0) with { Enabled = false });

        Assert.Single(evaluator.Evaluate(on, MemFrame(95, T0)));

        // Switched off is not recovered: the condition stops being reported because nobody is asking any
        // more, and it does not close through the clear dwell as though the value had come down.
        Assert.Empty(evaluator.Evaluate(off, MemFrame(95, T0 + Secs(1))));
    }

    [Fact]
    public void Disabling_a_rule_closes_its_open_episode_as_unwatched()
    {
        var evaluator = new ConditionEvaluator();
        var on = Policy(MemRule(warn: 90, fireForSec: 0));
        var off = Policy(MemRule(warn: 90, fireForSec: 0) with { Enabled = false });

        evaluator.Evaluate(on, MemFrame(95, T0));
        evaluator.DrainTransitions();   // the opening

        evaluator.Evaluate(off, MemFrame(95, T0 + Secs(1)));

        // Dropping the state without closing left the episode open in the durable record forever while the
        // live feed showed it gone — the two halves disagreeing about the same condition. It ends as
        // UNWATCHED, not recovered: the value was never observed to come down, and calling that a recovery
        // would report a measurement nobody took.
        EpisodeTransition t = Assert.Single(evaluator.DrainTransitions());
        Assert.Equal(T0 + Secs(1), t.ClosedTs);
        Assert.Equal(EpisodeEnd.Unwatched, t.EndReason);
    }

    [Fact]
    public void Retuning_a_rule_closes_its_open_episode_as_unwatched()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(MemRule(warn: 90, fireForSec: 0));

        evaluator.Evaluate(policy, MemFrame(95, T0));
        evaluator.DrainTransitions();

        // The path an operator actually takes: edit a threshold while something is firing against it.
        evaluator.ResetRule("host-mem");
        evaluator.Evaluate(Policy(MemRule(warn: 50, fireForSec: 0)), MemFrame(95, T0 + Secs(1)));

        List<EpisodeTransition> transitions = [.. evaluator.DrainTransitions()];
        EpisodeTransition closed = Assert.Single(transitions, x => x.ClosedTs is not null);
        Assert.Equal(EpisodeEnd.Unwatched, closed.EndReason);

        // And a new episode opens against the new line, rather than the old one silently carrying on.
        Assert.Single(transitions, x => x.ClosedTs is null);
    }

    [Fact]
    public void A_recovered_episode_says_so()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(MemRule(warn: 90, fireForSec: 0, clearForSec: 0, clearMargin: 5));

        evaluator.Evaluate(policy, MemFrame(95, T0));
        evaluator.DrainTransitions();
        evaluator.Evaluate(policy, MemFrame(50, T0 + Secs(1)));

        EpisodeTransition t = Assert.Single(evaluator.DrainTransitions());
        Assert.Equal(EpisodeEnd.Recovered, t.EndReason);
    }

    [Fact]
    public void Resetting_a_rule_drops_its_dwell_clocks()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(MemRule(warn: 90, fireForSec: 60));

        evaluator.Evaluate(policy, MemFrame(95, T0));            // fire clock armed at T0
        evaluator.ResetRule("host-mem");

        // Without the reset this would have opened: 61s have passed since the original breach. A dwell
        // measured against the old thresholds says nothing about the new ones.
        Assert.Empty(evaluator.Evaluate(policy, MemFrame(95, T0 + Secs(61))));
        Assert.Single(evaluator.Evaluate(policy, MemFrame(95, T0 + Secs(122))));
    }

    [Fact]
    public void Resetting_one_rule_leaves_another_rules_clock_alone()
    {
        var evaluator = new ConditionEvaluator();
        var policy = Policy(
            MemRule(warn: 90, fireForSec: 60),
            new ThresholdRule("host-load", ThresholdMetric.HostLoadPerCore,
                Warn: 1.5, Danger: null, FireForSec: 60, ClearForSec: 0, ClearMargin: 0, Enabled: true));

        Snapshot t0 = FrameWith(T0, mem: Mem(usedPct: 95), load: new LoadAvg(8, 8, 8));
        evaluator.Evaluate(policy, t0);

        evaluator.ResetRule("host-mem");

        // host-load's clock was armed at T0 and nothing touched it, so it opens on schedule. Re-arming
        // every clock on any edit would have silently delayed this by a full dwell.
        Snapshot t61 = FrameWith(T0 + Secs(61), mem: Mem(usedPct: 95), load: new LoadAvg(8, 8, 8));
        ConditionReading c = Assert.Single(evaluator.Evaluate(policy, t61));
        Assert.Equal("host-load", c.RuleKey);
    }

    // --- helpers ------------------------------------------------------------------------------------

    private static long Secs(int n) => n * 1000L;

    private static MetricsThresholdPolicy Policy(params ThresholdRule[] rules) => new(rules);

    private static ThresholdRule MemRule(double warn, double? danger = null, int fireForSec = 60,
        int clearForSec = 0, double clearMargin = 0) =>
        new("host-mem", ThresholdMetric.HostMemUsedPct, warn, danger, fireForSec, clearForSec, clearMargin, true);

    private static Snapshot MemFrame(double usedPct, long ts) => FrameWith(ts, mem: Mem(usedPct));

    private static MemoryMetrics Mem(double usedPct, long swapTotalKb = 0, long swapUsedKb = 0) =>
        new(TotalKb: 1000, AvailableKb: 500, UsedKb: 500, UsedPct: usedPct,
            SwapTotalKb: swapTotalKb, SwapUsedKb: swapUsedKb, CachedKb: 0, BuffersKb: 0);

    private static ServerMetrics Server(string id, int pids) =>
        new(id, id, "native", CpuPctCore: 0, MemBytes: 0, IoReadBps: null, IoWriteBps: null,
            Pids: pids, DiskBytes: null, RxBps: null, TxBps: null);

    private static Snapshot FrameWith(
        long ts,
        MemoryMetrics? mem = null,
        LoadAvg? load = null,
        MountUsage[]? mounts = null,
        SensorReading[]? sensors = null,
        ServerMetrics[]? servers = null) =>
        new(Ts: ts,
            IntervalMs: 1000,
            Hostname: "test",
            UptimeSec: 1,
            Cpu: new CpuMetrics(0, [], load ?? new LoadAvg(0, 0, 0), new CpuInfo("test", 4, 8, null)),
            Mem: mem ?? Mem(0),
            Disk: new DiskMetrics(mounts ?? [], new DiskIo(0, 0)),
            Net: new NetworkMetrics([]),
            Sensors: sensors ?? [],
            Servers: servers ?? [],
            Leaves: [],
            Conditions: []);
}
