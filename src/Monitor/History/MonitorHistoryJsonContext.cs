using System.Text.Json.Serialization;

namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// Source-generation context for the daemon-local history DTOs — kept in the <c>Monitor</c> project,
/// NOT in <c>Monitor.Contracts</c> (the shared <c>Snapshot</c> contract does not change). Keeps the
/// history endpoints reflection-free so the daemon stays Native-AOT/trim-clean, and mirrors the
/// snapshot context's camelCase naming for the SPA. Covers the metrics-history response
/// (<c>GET /metrics/history</c>), the event-history response (<c>GET /events</c>) and the index
/// rebuild report (<c>POST /events/rebuild</c>).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MetricsHistoryResponse))]
[JsonSerializable(typeof(EventHistoryResponse))]
[JsonSerializable(typeof(EventIndexRebuildResult))]
public sealed partial class MonitorHistoryJsonContext : JsonSerializerContext;
