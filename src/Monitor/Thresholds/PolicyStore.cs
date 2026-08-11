using System.Text.Json;
using System.Text.RegularExpressions;

namespace TheKrystalShip.KGSM.Monitor.Thresholds;

/// <summary>
/// Holds the rule set the daemon is evaluating, and applies a new one without a restart.
/// </summary>
/// <remarks>
/// <para><b>The baseline is code; the override is a file.</b> A host with no override file runs
/// <see cref="MetricsThresholdPolicy.Default"/>, which is why a monitor that has never met a control panel
/// still watches its thresholds. Applying a policy writes the file and swaps the in-memory set; deleting it
/// (<see cref="ResetAsync"/>) returns to the baseline. The file's existence IS the answer to "is this host
/// running its own policy", so that fact is not also stored inside it.</para>
/// <para><b>Whole-set, validated whole, applied atomically.</b> Every rule is checked before anything is
/// written, so a rejected policy leaves the running one exactly as it was. The file is written before the
/// swap: if persisting fails the operator is told and the daemon keeps evaluating what it was already
/// evaluating, rather than running a policy that will vanish on restart.</para>
/// <para><b>Only rules whose terms changed lose their dwell clocks.</b> A dwell measured against the old
/// thresholds says nothing about new ones, but re-arming every clock on any edit would silently delay an
/// unrelated breach that was seconds from opening — so the diff is per rule.</para>
/// <para><b>Threading.</b> <see cref="Current"/> is a volatile reference to an immutable policy, read by
/// the sampler thread and swapped by whichever request thread applies one. <see cref="_applyGate"/>
/// serializes applies so two concurrent writes cannot interleave file and swap. The evaluator's resets go
/// through its own queue, so nothing here touches its state off-thread.</para>
/// </remarks>
public sealed class PolicyStore
{
    // A rule key becomes part of an alert id downstream, so it is held to something that survives being
    // put in one: lowercase, digits and dashes.
    private static readonly Regex KeyPattern = new("^[a-z0-9-]+$", RegexOptions.Compiled);

    private readonly MonitorOptions _options;
    private readonly ConditionEvaluator? _evaluator;
    private readonly ILogger<PolicyStore> _logger;
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    private volatile MetricsThresholdPolicy _current = MetricsThresholdPolicy.Default;
    private volatile PolicySource _source = new("default", null);

    public PolicyStore(MonitorOptions options, ILogger<PolicyStore> logger, ConditionEvaluator? evaluator = null)
    {
        _options = options;
        _evaluator = evaluator;
        _logger = logger;
        Load();
    }

    /// <summary>The rule set being evaluated right now.</summary>
    public MetricsThresholdPolicy Current => _current;

    /// <summary>The policy as the HTTP surface reports it.</summary>
    public ThresholdPolicyDocument Document()
    {
        PolicySource source = _source;
        return new ThresholdPolicyDocument(source.Kind, source.AppliedAtMs, [.. _current.Rules]);
    }

    /// <summary>
    /// Validate and apply <paramref name="rules"/>. Returns the applied policy, or the reason it was
    /// refused — never a partial application.
    /// </summary>
    public async Task<ApplyResult> ApplyAsync(ThresholdRule[]? rules, CancellationToken ct)
    {
        if (rules is null)
            return ApplyResult.Rejected("A rules array is required. Send the whole rule set — a policy is applied as a whole, never merged.", null);

        if (Validate(rules) is { } rejection) return rejection;

        await _applyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long appliedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var persisted = new PersistedThresholdPolicy(appliedAt, rules);

            try
            {
                await PersistAsync(persisted, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Deliberately NOT swapping: a policy that cannot be persisted would disappear on the next
                // restart, and reporting it as applied would make the panel and the daemon disagree the
                // moment anything bounced.
                _logger.LogError(ex, "threshold policy: could not write {Path} — keeping the running policy", _options.ThresholdPolicyPath);
                return ApplyResult.Failed($"Could not write the policy file: {ex.Message}");
            }

            SwapTo(new MetricsThresholdPolicy(rules), new PolicySource("override", appliedAt));
            _logger.LogInformation("threshold policy: applied {Count} rule(s), {Enabled} enabled",
                rules.Length, rules.Count(r => r.Enabled));
            return ApplyResult.Applied(Document());
        }
        finally { _applyGate.Release(); }
    }

    /// <summary>
    /// Drop the override and return to the built-in baseline. Deleting a file that is not there is a
    /// success: the caller asked for this host to be on the defaults, and it is.
    /// </summary>
    public async Task<ApplyResult> ResetAsync(CancellationToken ct)
    {
        await _applyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                if (File.Exists(_options.ThresholdPolicyPath))
                    File.Delete(_options.ThresholdPolicyPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "threshold policy: could not delete {Path}", _options.ThresholdPolicyPath);
                return ApplyResult.Failed($"Could not delete the policy file: {ex.Message}");
            }

            SwapTo(MetricsThresholdPolicy.Default, new PolicySource("default", null));
            _logger.LogInformation("threshold policy: reset to the built-in defaults");
            return ApplyResult.Applied(Document());
        }
        finally { _applyGate.Release(); }
    }

    // Swap the running policy and re-arm only the rules whose terms moved. Record equality does the diff:
    // a rule that came back identical keeps its clocks, and one that changed in any way loses them.
    private void SwapTo(MetricsThresholdPolicy next, PolicySource source)
    {
        MetricsThresholdPolicy previous = _current;
        _current = next;
        _source = source;

        if (_evaluator is null) return;

        foreach (ThresholdRule rule in next.Rules)
        {
            ThresholdRule? before = previous.Rules.FirstOrDefault(r => string.Equals(r.Key, rule.Key, StringComparison.Ordinal));
            if (before is null || before != rule) _evaluator.ResetRule(rule.Key);
        }
    }

    // Every reason a policy can be refused, checked before anything is written. Values are rejected rather
    // than clamped: an operator who typed a threshold their host will not honour should be told so, not
    // have it quietly moved.
    private static ApplyResult? Validate(ThresholdRule[] rules)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (ThresholdRule rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Key))
                return ApplyResult.Rejected("Every rule needs a key.", null);

            if (!KeyPattern.IsMatch(rule.Key))
                return ApplyResult.Rejected(
                    $"'{rule.Key}' is not a usable rule key. Use lowercase letters, digits and dashes — the key becomes part of an alert id.", rule.Key);

            if (!seen.Add(rule.Key))
                return ApplyResult.Rejected($"Two rules both use the key '{rule.Key}'.", rule.Key);

            if (double.IsNaN(rule.Warn) || double.IsInfinity(rule.Warn))
                return ApplyResult.Rejected("The warn threshold has to be a real number.", rule.Key);

            if (rule.Danger is { } danger && (double.IsNaN(danger) || double.IsInfinity(danger)))
                return ApplyResult.Rejected("The danger threshold has to be a real number.", rule.Key);

            if (rule.FireForSec < 0 || rule.ClearForSec < 0)
                return ApplyResult.Rejected("A dwell cannot be negative.", rule.Key);

            if (rule.ClearMargin < 0 || double.IsNaN(rule.ClearMargin) || double.IsInfinity(rule.ClearMargin))
                return ApplyResult.Rejected("The clear margin cannot be negative.", rule.Key);

            // The remaining checks are about whether a rule can BEHAVE, and a disabled rule does not run.
            // Holding one to them would refuse the daemon's own shipped defaults, where the per-server rules
            // sit disabled with a placeholder threshold waiting for someone to fill in a number that only
            // makes sense per game — and would stop an editor saving a half-finished rule it has switched
            // off, which is exactly when a person wants to save one.
            if (!rule.Enabled) continue;

            if (rule.Danger is { } enabledDanger && enabledDanger <= rule.Warn)
                return ApplyResult.Rejected(
                    $"The danger threshold ({enabledDanger}) has to be above the warn threshold ({rule.Warn}), or left out for a warn-only rule.", rule.Key);

            if (rule.ClearMargin >= rule.Warn)
                return ApplyResult.Rejected(
                    $"The clear margin ({rule.ClearMargin}) has to be below the warn threshold ({rule.Warn}); a condition would otherwise never be able to clear.", rule.Key);
        }

        return null;
    }

    private async Task PersistAsync(PersistedThresholdPolicy policy, CancellationToken ct)
    {
        string path = _options.ThresholdPolicyPath;
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write beside the target and move into place, so a policy file is never observed half-written —
        // including by this daemon's own next start.
        string temp = path + ".new";
        await using (FileStream stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, policy,
                MonitorThresholdJsonContext.Default.PersistedThresholdPolicy, ct).ConfigureAwait(false);
        }
        File.Move(temp, path, overwrite: true);
    }

    // Read the override at construction. Anything wrong with the file — missing, unreadable, malformed,
    // or carrying rules this build would refuse — falls back to the baseline with a log naming the reason.
    // The daemon has to come up: refusing to start over a bad policy file would take metrics down with it.
    private void Load()
    {
        string path = _options.ThresholdPolicyPath;
        if (!File.Exists(path))
        {
            _logger.LogInformation("threshold policy: no override at {Path} — running the built-in defaults", path);
            return;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            PersistedThresholdPolicy? persisted = JsonSerializer.Deserialize(
                stream, MonitorThresholdJsonContext.Default.PersistedThresholdPolicy);

            if (persisted?.Rules is null)
            {
                _logger.LogWarning("threshold policy: {Path} carries no rules — running the built-in defaults", path);
                return;
            }

            if (Validate(persisted.Rules) is { } rejection)
            {
                _logger.LogWarning("threshold policy: {Path} is not valid ({Reason}) — running the built-in defaults",
                    path, rejection.Error);
                return;
            }

            _current = new MetricsThresholdPolicy(persisted.Rules);
            _source = new PolicySource("override", persisted.AppliedAtMs);
            _logger.LogInformation("threshold policy: loaded {Count} rule(s) from {Path}", persisted.Rules.Length, path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "threshold policy: could not read {Path} — running the built-in defaults", path);
        }
    }

    private sealed record PolicySource(string Kind, long? AppliedAtMs);

    /// <summary>What came of an apply: the policy now running, or why it was refused.</summary>
    /// <param name="Document">The applied policy, when <paramref name="Ok"/>.</param>
    /// <param name="Error">Why it was refused, otherwise.</param>
    /// <param name="Key">The rule at fault, when one rule is.</param>
    /// <param name="Ok">Whether it was applied.</param>
    /// <param name="Retryable"><see langword="true"/> when the request was fine and this host could not
    /// carry it out (a failed write), which is a 500 rather than a 400 — the caller has nothing to fix.</param>
    public sealed record ApplyResult(ThresholdPolicyDocument? Document, string? Error, string? Key, bool Ok, bool Retryable)
    {
        public static ApplyResult Applied(ThresholdPolicyDocument doc) => new(doc, null, null, true, false);
        public static ApplyResult Rejected(string error, string? key) => new(null, error, key, false, false);
        public static ApplyResult Failed(string error) => new(null, error, null, false, true);
    }
}
