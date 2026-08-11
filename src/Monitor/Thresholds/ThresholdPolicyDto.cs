using System.Text.Json.Serialization;

namespace TheKrystalShip.KGSM.Monitor.Thresholds;

/// <summary>The policy as served by <c>GET /thresholds</c> and echoed back by <c>PUT</c>.</summary>
/// <param name="Source"><c>default</c> when this host is running the built-in baseline, <c>override</c>
/// when an operator has applied their own. The distinction is what lets a panel offer "reset" honestly
/// rather than offering it always.</param>
/// <param name="AppliedAtMs">When the override was applied, unix ms; <see langword="null"/> for the
/// baseline, which was never applied by anyone.</param>
/// <param name="Rules">The rule set being evaluated right now.</param>
public sealed record ThresholdPolicyDocument(string Source, long? AppliedAtMs, ThresholdRule[] Rules);

/// <summary>A <c>PUT /thresholds</c> body. Whole-set: what is sent is what will be evaluated, so a rule
/// left out is a rule removed. Partial updates are deliberately not offered — merging would make the
/// result depend on what was already there, and an operator editing a policy needs to see the whole of
/// what they are applying.</summary>
/// <param name="Rules">The complete rule set. <see langword="null"/> is rejected; empty is accepted and
/// means "watch nothing", which is a different statement from every rule being disabled only in that it
/// forgets their thresholds.</param>
public sealed record ThresholdPolicyRequest(ThresholdRule[]? Rules);

/// <summary>A rejected <c>PUT</c>. <paramref name="Key"/> names the offending rule when one rule is at
/// fault, so an operator is told which line to fix rather than that something, somewhere, is wrong.</summary>
public sealed record ThresholdErrorResponse(string Error, string? Key);

/// <summary>The on-disk form of an applied override. Deliberately not the same type as
/// <see cref="ThresholdPolicyDocument"/>: <c>source</c> is derived from whether this file exists, so
/// storing it would create a second answer to the same question.</summary>
public sealed record PersistedThresholdPolicy(long AppliedAtMs, ThresholdRule[] Rules);

/// <summary>
/// Source-generated JSON for the threshold surface. Daemon-local, like the history context — the shared
/// <c>MonitorJsonContext</c> stays limited to the snapshot graph that kgsm-api compiles against.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ThresholdPolicyDocument))]
[JsonSerializable(typeof(ThresholdPolicyRequest))]
[JsonSerializable(typeof(ThresholdErrorResponse))]
[JsonSerializable(typeof(PersistedThresholdPolicy))]
public sealed partial class MonitorThresholdJsonContext : JsonSerializerContext;
