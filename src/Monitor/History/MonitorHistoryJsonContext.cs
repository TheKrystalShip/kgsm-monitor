using System.Text.Json.Serialization;

namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// Source-generation context for the daemon-local history DTO — kept in the <c>Monitor</c> project,
/// NOT in <c>Monitor.Contracts</c> (the shared <c>Snapshot</c> contract does not change). Keeps the
/// history endpoint reflection-free so the daemon stays Native-AOT/trim-clean, and mirrors the
/// snapshot context's camelCase naming for the SPA.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MetricsHistoryResponse))]
public sealed partial class MonitorHistoryJsonContext : JsonSerializerContext;
