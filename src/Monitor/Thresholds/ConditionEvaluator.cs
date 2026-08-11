using System.Collections.Concurrent;
using TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.KGSM.Monitor.Thresholds;

/// <summary>
/// Decides, once per sample, which threshold rules are currently over their line and have been for long
/// enough to say so. The output is the <see cref="Snapshot.Conditions"/> array — a verdict, not a reading,
/// which is the whole reason this lives beside the sampler: it sees every sample, so "sustained" is
/// something it can actually establish rather than infer from a slower scrape.
/// </summary>
/// <remarks>
/// <para><b>Two dwells and a deadband.</b> A breach must hold for <see cref="ThresholdRule.FireForSec"/>
/// before a condition opens, which kills a spike. A clear must drop <see cref="ThresholdRule.ClearMargin"/>
/// below <see cref="ThresholdRule.Warn"/> <em>and</em> hold there for <see cref="ThresholdRule.ClearForSec"/>
/// before it closes. Between the two lies the deadband: a value hovering right at the line neither opens
/// nor closes anything, so it cannot flap.</para>
/// <para><b>An episode is one continuous breach.</b> Its id is fixed when it opens and never reused, so a
/// consumer can tell "still the same problem" from "it cleared and came back" without diffing values. It
/// opens stamped with the time of the <em>first</em> reading over the line, not the moment the dwell
/// completed — "how long has this been wrong" is answered from when it started being wrong.</para>
/// <para><b>Not evaluable is not clear.</b> A target that yields no observation this tick (a null field, no
/// swap, no cpu-info) simply is not reconciled: an open condition holds, a closed one stays closed. A target
/// that <em>vanishes</em> — a server that stopped, a mount that unmounted — is different, and runs the
/// normal clear dwell before closing, because that is a real transition rather than an absent measurement.</para>
/// <para><b>State lives for as long as the sampler does.</b> A restart drops it, so dwells re-accumulate
/// from the first sample after start. That is the honest answer: a process that has been running for two
/// seconds cannot claim a value held for thirty.</para>
/// <para><b>Threading.</b> <see cref="Evaluate"/> runs only on the sampler tick and is the sole writer of
/// <see cref="_targets"/>. Resets arrive from other threads through a queue drained at the top of the tick,
/// so applying a new policy never touches the state off-thread and this stays lock-free.</para>
/// </remarks>
public sealed class ConditionEvaluator
{
    private readonly Dictionary<string, TargetState> _targets = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _pendingResets = new();

    /// <summary>
    /// Drop every open condition and dwell clock belonging to <paramref name="ruleKey"/>. Called when a rule's
    /// terms change, because a dwell measured against the old thresholds says nothing about the new ones.
    /// Thread-safe: the reset is queued and applied at the top of the next tick, so the state stays owned by
    /// one thread. Rules whose terms did NOT change are deliberately left alone — re-arming every clock on
    /// any edit would silently delay an unrelated breach that was seconds from opening.
    /// </summary>
    public void ResetRule(string ruleKey)
    {
        if (!string.IsNullOrEmpty(ruleKey)) _pendingResets.Enqueue(ruleKey);
    }

    /// <summary>
    /// Reconcile every enabled rule against <paramref name="snap"/> and return the conditions currently open.
    /// <paramref name="snap"/> is the frame being built this tick; its <see cref="Snapshot.Ts"/> is the clock,
    /// so a condition's timestamps line up with the frame that carries it.
    /// </summary>
    public ConditionReading[] Evaluate(MetricsThresholdPolicy policy, Snapshot snap)
    {
        ApplyPendingResets();

        long nowMs = snap.Ts;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (ThresholdRule rule in policy.Rules)
        {
            if (!rule.Enabled) continue;
            foreach (MetricObservation obs in ThresholdMetrics.Observe(rule, snap))
            {
                string key = TargetKey(rule.Key, TargetRef(obs));
                seen.Add(key);
                Reconcile(key, rule, obs, nowMs);
            }
        }

        SweepUnobserved(policy, seen, nowMs);

        return Emit();
    }

    // One target's reconcile against its rule for this tick: the fire-dwell / hysteresis-clear / deadband
    // state machine. The three branches are exhaustive over where the value sits relative to the two lines.
    private void Reconcile(string key, ThresholdRule rule, MetricObservation obs, long nowMs)
    {
        if (!_targets.TryGetValue(key, out TargetState? st))
        {
            st = new TargetState(rule.Key, rule.Metric, obs.RefKey, obs.ServerId);
            _targets[key] = st;
        }

        st.LastValue = obs.Value;
        string? band = Classify(rule, obs.Value);

        if (band is not null)
        {
            // Over the line. Cancel any pending clear, arm or hold the fire clock, and track the peak.
            st.ClearSinceMs = null;
            if (st.BreachSinceMs is null)
            {
                st.BreachSinceMs = nowMs;
                st.WindowMax = obs.Value;
            }
            else if (obs.Value > st.WindowMax)
            {
                st.WindowMax = obs.Value;
            }

            st.Band = band;
            st.Threshold = band == ConditionBand.Danger ? rule.Danger!.Value : rule.Warn;

            if (!st.Open && nowMs - st.BreachSinceMs.Value >= (long)rule.FireForSec * 1000)
            {
                st.Open = true;
                st.OpenedAtMs = st.BreachSinceMs.Value;
                st.EpisodeId = MakeEpisodeId(rule.Key, TargetRef(obs), st.OpenedAtMs);
            }
            return;
        }

        if (obs.Value <= rule.Warn - rule.ClearMargin)
        {
            // Past the deadband — a real clear. Start or hold the clear clock.
            st.BreachSinceMs = null;
            if (!st.Open)
            {
                st.ClearSinceMs = null;
                return;
            }
            st.ClearSinceMs ??= nowMs;
            if (nowMs - st.ClearSinceMs.Value >= (long)rule.ClearForSec * 1000)
                Close(st);
            return;
        }

        // Deadband: below the line but not far enough below to count as recovered. Neither clock runs — the
        // fire clock resets because the value is no longer breaching, and the clear clock resets because the
        // rule's contract is that the value stays below the CLEAR threshold for the dwell, and it is not.
        // An open condition therefore stays open, which is the point of the deadband.
        st.BreachSinceMs = null;
        st.ClearSinceMs = null;
    }

    // Targets that produced no observation this tick, plus rules that left the policy. There are three
    // reasons a target can go quiet and they do NOT get the same answer — see ThresholdMetrics.FansOut for
    // the one that carries the real weight.
    private void SweepUnobserved(MetricsThresholdPolicy policy, HashSet<string> seen, long nowMs)
    {
        List<string>? drop = null;

        foreach ((string key, TargetState st) in _targets)
        {
            if (seen.Contains(key)) continue;

            ThresholdRule? rule = FindEnabledRule(policy, st.RuleKey);
            if (rule is null)
            {
                // The rule was disabled or removed. Its conditions are not "cleared", they are no longer
                // being asked — so they stop being reported rather than closing with a recovery.
                (drop ??= []).Add(key);
                continue;
            }

            if (!st.Open)
            {
                (drop ??= []).Add(key);
                continue;
            }

            if (!ThresholdMetrics.FansOut(st.Metric))
            {
                // A singleton whose value could not be established this tick. Holding is the honest answer:
                // nothing measured it, so nothing can say it recovered. It resumes reconciling the moment
                // the field is readable again.
                continue;
            }

            // A member of a set that is no longer in the set — it went away. Real transition, normal dwell.
            st.BreachSinceMs = null;
            st.ClearSinceMs ??= nowMs;
            if (nowMs - st.ClearSinceMs.Value >= (long)rule.ClearForSec * 1000)
            {
                Close(st);
                (drop ??= []).Add(key);
            }
        }

        if (drop is null) return;
        foreach (string key in drop) _targets.Remove(key);
    }

    private ConditionReading[] Emit()
    {
        int open = 0;
        foreach (TargetState st in _targets.Values)
            if (st.Open) open++;

        if (open == 0) return [];

        var readings = new ConditionReading[open];
        int i = 0;
        foreach (TargetState st in _targets.Values)
        {
            if (!st.Open) continue;
            readings[i++] = new ConditionReading(
                EpisodeId: st.EpisodeId,
                RuleKey: st.RuleKey,
                Metric: ThresholdMetrics.WireName(st.Metric),
                Scope: ThresholdMetrics.ScopeName(st.Metric),
                Ref: st.RefKey,
                ServerId: st.ServerId,
                Band: st.Band,
                Value: st.LastValue,
                WindowMax: st.WindowMax,
                Threshold: st.Threshold,
                Since: st.OpenedAtMs);
        }
        return readings;
    }

    private void ApplyPendingResets()
    {
        while (_pendingResets.TryDequeue(out string? ruleKey))
        {
            List<string>? drop = null;
            foreach ((string key, TargetState st) in _targets)
                if (string.Equals(st.RuleKey, ruleKey, StringComparison.Ordinal))
                    (drop ??= []).Add(key);

            if (drop is null) continue;
            foreach (string key in drop) _targets.Remove(key);
        }
    }

    private static void Close(TargetState st)
    {
        st.Open = false;
        st.ClearSinceMs = null;
        st.BreachSinceMs = null;
        st.EpisodeId = string.Empty;
        st.Band = string.Empty;
        st.WindowMax = 0;
    }

    private static string? Classify(ThresholdRule rule, double value) =>
        rule.Danger is { } danger && value >= danger ? ConditionBand.Danger
        : value >= rule.Warn ? ConditionBand.Warn
        : null;

    private static ThresholdRule? FindEnabledRule(MetricsThresholdPolicy policy, string ruleKey)
    {
        foreach (ThresholdRule rule in policy.Rules)
            if (rule.Enabled && string.Equals(rule.Key, ruleKey, StringComparison.Ordinal))
                return rule;
        return null;
    }

    private static string? TargetRef(MetricObservation obs) => obs.RefKey ?? obs.ServerId;

    // Internal keying only. The separator is one a rule key cannot contain (keys are [a-z0-9-]) and a mount
    // path or sensor label will not collide across, because the rule key is always the first segment.
    private static string TargetKey(string ruleKey, string? targetRef) =>
        string.IsNullOrEmpty(targetRef) ? ruleKey : $"{ruleKey} {targetRef}";

    /// <summary>The episode id carried on the wire: rule, target and open time. The open time is what makes
    /// it unique across a clear-and-recur on the same target.</summary>
    internal static string MakeEpisodeId(string ruleKey, string? targetRef, long openedAtMs) =>
        $"{ruleKey}:{targetRef}:{openedAtMs}";

    // Everything known about one target of one rule between ticks. Mutable and never published — Emit()
    // projects immutable readings from it.
    private sealed class TargetState(string ruleKey, ThresholdMetric metric, string? refKey, string? serverId)
    {
        public string RuleKey { get; } = ruleKey;
        public ThresholdMetric Metric { get; } = metric;
        public string? RefKey { get; } = refKey;
        public string? ServerId { get; } = serverId;

        public long? BreachSinceMs { get; set; }
        public long? ClearSinceMs { get; set; }
        public bool Open { get; set; }
        public long OpenedAtMs { get; set; }
        public string EpisodeId { get; set; } = string.Empty;
        public string Band { get; set; } = string.Empty;
        public double WindowMax { get; set; }
        public double LastValue { get; set; }
        public double Threshold { get; set; }
    }
}

/// <summary>The two bands a rule defines. A condition is always in one of them while it is open.</summary>
public static class ConditionBand
{
    public const string Warn = "warn";
    public const string Danger = "danger";
}
