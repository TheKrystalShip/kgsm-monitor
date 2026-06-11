using System.Text.Json.Serialization;

namespace TheKrystalShip.KGSM.Monitor.Model;

/// <summary>
/// System.Text.Json source-generation context for the snapshot graph. Keeps the
/// daemon reflection-free so it stays Native-AOT/trim-clean (no IL2026/IL3050)
/// and serializes on the hot path without runtime metadata building. camelCase
/// property names for the SPA.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Snapshot))]
internal sealed partial class MonitorJsonContext : JsonSerializerContext;
