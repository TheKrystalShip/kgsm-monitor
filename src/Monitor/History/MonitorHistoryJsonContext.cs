using System.Text.Json.Serialization;

namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// Source-generation context for the daemon-local history DTOs — kept in the <c>Monitor</c> project,
/// NOT in <c>Monitor.Contracts</c> (the shared <c>Snapshot</c> contract does not change). Keeps the
/// history endpoints reflection-free so the daemon stays Native-AOT/trim-clean, and mirrors the
/// snapshot context's camelCase naming for the SPA. Covers both the metrics-history response
/// (<c>GET /metrics/history</c>) and the event-history response (<c>GET /events</c>).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MetricsHistoryResponse))]
[JsonSerializable(typeof(EventHistoryResponse))]
public sealed partial class MonitorHistoryJsonContext : JsonSerializerContext;
