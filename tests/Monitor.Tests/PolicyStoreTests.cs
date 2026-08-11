using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Monitor.Thresholds;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// The policy store: what a host is evaluating, and what happens when somebody changes it. The load-bearing
/// properties are that a refused policy leaves the running one untouched, that a policy which could not be
/// written is not reported as applied, and that applying one re-arms the dwell clocks of the rules that
/// actually changed and no others.
/// </summary>
public class PolicyStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kgsm-monitor-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    // --- baseline / override ------------------------------------------------------------------------

    [Fact]
    public void A_host_with_no_policy_file_runs_the_built_in_defaults()
    {
        PolicyStore store = Store();

        Assert.Equal("default", store.Document().Source);
        Assert.Null(store.Document().AppliedAtMs);
        Assert.Equal(MetricsThresholdPolicy.Default.Rules.Count, store.Current.Rules.Count);
    }

    [Fact]
    public async Task An_applied_policy_survives_a_restart()
    {
        PolicyStore store = Store();
        await store.ApplyAsync([Rule("host-temp", warn: 70)], CancellationToken.None);

        // A second store over the same path is what the next daemon start sees.
        PolicyStore restarted = Store();
        Assert.Equal("override", restarted.Document().Source);
        ThresholdRule rule = Assert.Single(restarted.Current.Rules);
        Assert.Equal("host-temp", rule.Key);
        Assert.Equal(70, rule.Warn);
    }

    [Fact]
    public async Task Resetting_returns_to_the_defaults_and_removes_the_file()
    {
        PolicyStore store = Store();
        await store.ApplyAsync([Rule("host-temp", warn: 70)], CancellationToken.None);
        Assert.True(File.Exists(PolicyPath));

        await store.ResetAsync(CancellationToken.None);

        Assert.Equal("default", store.Document().Source);
        Assert.False(File.Exists(PolicyPath));
        Assert.Equal(MetricsThresholdPolicy.Default.Rules.Count, store.Current.Rules.Count);
    }

    [Fact]
    public async Task Resetting_a_host_that_was_already_on_defaults_succeeds()
    {
        PolicyStore store = Store();

        // The caller asked for this host to be on the defaults. It is. That is not an error.
        PolicyStore.ApplyResult result = await store.ResetAsync(CancellationToken.None);
        Assert.True(result.Ok);
    }

    [Fact]
    public void A_corrupt_policy_file_falls_back_to_the_defaults_rather_than_failing_to_start()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PolicyPath, "{ this is not json");

        // Refusing to start over a bad policy file would take metrics down with it, which is a far worse
        // outcome than watching the default thresholds.
        PolicyStore store = Store();
        Assert.Equal("default", store.Document().Source);
    }

    [Fact]
    public void A_policy_file_carrying_invalid_rules_falls_back_to_the_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PolicyPath,
            """{"appliedAtMs":1,"rules":[{"key":"bad","metric":"HostTempC","warn":10,"danger":5,"fireForSec":0,"clearForSec":0,"clearMargin":0,"enabled":true}]}""");

        PolicyStore store = Store();
        Assert.Equal("default", store.Document().Source);
    }

    // --- validation ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_null_rules_array_is_refused()
    {
        PolicyStore store = Store();
        PolicyStore.ApplyResult result = await store.ApplyAsync(null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(result.Retryable);   // the caller has something to fix
    }

    [Theory]
    [InlineData("Host-Mem")]   // uppercase
    [InlineData("host mem")]   // space
    [InlineData("host:mem")]   // the alert-id separator
    [InlineData("")]
    public async Task An_unusable_rule_key_is_refused(string key)
    {
        PolicyStore store = Store();
        PolicyStore.ApplyResult result = await store.ApplyAsync([Rule(key, warn: 70)], CancellationToken.None);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task A_duplicate_rule_key_is_refused_and_names_it()
    {
        PolicyStore store = Store();
        PolicyStore.ApplyResult result = await store.ApplyAsync(
            [Rule("host-temp", warn: 70), Rule("host-temp", warn: 80)], CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("host-temp", result.Key);
    }

    [Fact]
    public async Task A_danger_threshold_below_warn_is_refused()
    {
        PolicyStore store = Store();
        PolicyStore.ApplyResult result = await store.ApplyAsync(
            [Rule("host-temp", warn: 80, danger: 70)], CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("host-temp", result.Key);
    }

    [Fact]
    public async Task A_clear_margin_at_or_above_warn_is_refused()
    {
        PolicyStore store = Store();

        // The clear threshold would be zero or negative, so nothing could ever clear — a rule that fires
        // once and stays firing forever is worse than one that was rejected.
        PolicyStore.ApplyResult result = await store.ApplyAsync(
            [Rule("host-temp", warn: 80, clearMargin: 80)], CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task The_shipped_defaults_round_trip_through_validation()
    {
        PolicyStore store = Store();

        // The per-server rules ship disabled with a placeholder threshold, waiting for a number that only
        // makes sense per game. A validator that refused them would refuse the daemon's own defaults — which
        // is exactly what a panel does the first time somebody edits anything.
        PolicyStore.ApplyResult result = await store.ApplyAsync(
            [.. MetricsThresholdPolicy.Default.Rules], CancellationToken.None);

        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public async Task A_disabled_rule_is_not_held_to_thresholds_it_will_never_evaluate()
    {
        PolicyStore store = Store();

        // Saving a half-finished rule is what somebody does while switching it off, so the checks about
        // whether a rule can behave apply only once it runs.
        PolicyStore.ApplyResult result = await store.ApplyAsync(
            [Rule("srv-mem", warn: 0, clearMargin: 0) with { Enabled = false }], CancellationToken.None);

        Assert.True(result.Ok, result.Error);
    }

    [Fact]
    public async Task The_same_rule_enabled_is_refused()
    {
        PolicyStore store = Store();
        PolicyStore.ApplyResult result = await store.ApplyAsync(
            [Rule("srv-mem", warn: 0, clearMargin: 0)], CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task A_negative_dwell_is_refused()
    {
        PolicyStore store = Store();
        PolicyStore.ApplyResult result = await store.ApplyAsync(
            [Rule("host-temp", warn: 80) with { FireForSec = -1 }], CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task A_refused_policy_leaves_the_running_one_untouched()
    {
        PolicyStore store = Store();
        await store.ApplyAsync([Rule("host-temp", warn: 70)], CancellationToken.None);

        await store.ApplyAsync([Rule("host-temp", warn: 60), Rule("BAD KEY", warn: 10)], CancellationToken.None);

        // Validated whole, applied whole: the good rule in a rejected set must not have landed either.
        ThresholdRule rule = Assert.Single(store.Current.Rules);
        Assert.Equal(70, rule.Warn);
    }

    [Fact]
    public async Task An_empty_rule_set_is_accepted()
    {
        PolicyStore store = Store();

        // "Watch nothing" is a policy somebody can legitimately want, and it is distinguishable from a
        // malformed body, which sends no rules array at all.
        PolicyStore.ApplyResult result = await store.ApplyAsync([], CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Empty(store.Current.Rules);
    }

    // --- dwell clocks -------------------------------------------------------------------------------

    [Fact]
    public async Task Editing_one_rule_leaves_another_rules_dwell_clock_running()
    {
        var evaluator = new ConditionEvaluator();
        PolicyStore store = Store(evaluator);
        CancellationToken ct = CancellationToken.None;

        await store.ApplyAsync([TempRule("host-temp", warn: 70), LoadRule("host-load", warn: 2)], ct);

        // Both rules start breaching at T0. Neither has met its 30s dwell yet.
        Assert.Empty(evaluator.Evaluate(store.Current, Frame(T0, tempC: 90, loadFive: 40)));

        // Retune host-temp only. host-load came back byte-identical.
        await store.ApplyAsync([TempRule("host-temp", warn: 60), LoadRule("host-load", warn: 2)], ct);

        ConditionReadingSet at31 = new(evaluator.Evaluate(store.Current, Frame(T0 + 31_000, tempC: 90, loadFive: 40)));

        // host-load kept the clock it armed at T0 and opens on schedule; host-temp lost its, because a dwell
        // measured against a 70° line says nothing about a 60° one. Re-arming both would have silently
        // delayed a breach that was one second from opening.
        Assert.Contains("host-load", at31.Keys);
        Assert.DoesNotContain("host-temp", at31.Keys);

        // host-temp opens a full dwell after the edit, dated from the edit rather than from T0.
        ConditionReadingSet at62 = new(evaluator.Evaluate(store.Current, Frame(T0 + 62_000, tempC: 90, loadFive: 40)));
        Assert.Contains("host-temp", at62.Keys);
    }

    [Fact]
    public async Task A_rule_added_by_an_edit_starts_its_dwell_from_the_edit()
    {
        var evaluator = new ConditionEvaluator();
        PolicyStore store = Store(evaluator);
        CancellationToken ct = CancellationToken.None;

        await store.ApplyAsync([TempRule("host-temp", warn: 70)], ct);
        Assert.Empty(evaluator.Evaluate(store.Current, Frame(T0, tempC: 90, loadFive: 40)));

        await store.ApplyAsync([TempRule("host-temp", warn: 70), LoadRule("host-load", warn: 2)], ct);

        // host-temp has been breaching since T0 and opens at T0+31. host-load has only existed since the
        // edit, and must not inherit a dwell from a period nobody was watching it.
        ConditionReadingSet at31 = new(evaluator.Evaluate(store.Current, Frame(T0 + 31_000, tempC: 90, loadFive: 40)));
        Assert.Contains("host-temp", at31.Keys);
        Assert.DoesNotContain("host-load", at31.Keys);
    }

    // --- helpers ------------------------------------------------------------------------------------

    private string PolicyPath => Path.Combine(_dir, "thresholds.json");

    private PolicyStore Store(ConditionEvaluator? evaluator = null) =>
        new(new MonitorOptions { ThresholdPolicyPath = PolicyPath },
            NullLogger<PolicyStore>.Instance, evaluator);

    private static ThresholdRule Rule(string key, double warn, double? danger = null, double clearMargin = 0) =>
        new(key, ThresholdMetric.HostTempC, warn, danger, FireForSec: 30, ClearForSec: 60, clearMargin, Enabled: true);

    private const long T0 = 1_767_225_600_000; // 2026-01-01T00:00:00Z, unix ms

    private static ThresholdRule TempRule(string key, double warn) =>
        new(key, ThresholdMetric.HostTempC, warn, null, FireForSec: 30, ClearForSec: 60, ClearMargin: 5, Enabled: true);

    private static ThresholdRule LoadRule(string key, double warn) =>
        new(key, ThresholdMetric.HostLoadPerCore, warn, null, FireForSec: 30, ClearForSec: 60, ClearMargin: 0.3, Enabled: true);

    // A frame breaching both a temperature rule and a load rule, so one edit's effect on the other's clock
    // is observable in a single evaluate.
    private static Contracts.Snapshot Frame(long ts, double tempC, double loadFive) =>
        new(Ts: ts, IntervalMs: 1000, Hostname: "test", UptimeSec: 1,
            Cpu: new Contracts.CpuMetrics(0, [], new Contracts.LoadAvg(0, loadFive, 0),
                new Contracts.CpuInfo("test", 4, 8, null)),
            Mem: new Contracts.MemoryMetrics(1000, 500, 500, 10, 0, 0, 0, 0),
            Disk: new Contracts.DiskMetrics([], new Contracts.DiskIo(0, 0)),
            Net: new Contracts.NetworkMetrics([]),
            Sensors: [new Contracts.SensorReading("k10temp", "Tctl", tempC)],
            Servers: [], Leaves: [], Conditions: []);

    // The rule keys open in one evaluate, for readable set assertions.
    private sealed class ConditionReadingSet(Contracts.ConditionReading[] readings)
    {
        public HashSet<string> Keys { get; } = [.. readings.Select(r => r.RuleKey)];
    }
}
