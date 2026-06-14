using System.Text.Json.Serialization;

namespace TheKrystalShip.KGSM.Monitor.Contracts;

/// <summary>
/// System.Text.Json source-generation context for the snapshot graph. Keeps the
/// daemon reflection-free so it stays Native-AOT/trim-clean (no IL2026/IL3050)
/// and serializes on the hot path without runtime metadata building. camelCase
/// property names for the SPA.
/// </summary>
/// <remarks>
/// This context ships with the contract on purpose: the monitor serializes with it
/// and the api deserializes with the <em>same</em> context, so the wire shape and the
/// camelCase naming are one shared definition that cannot drift between producer and
/// consumer.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Snapshot))]
public sealed partial class MonitorJsonContext : JsonSerializerContext;
