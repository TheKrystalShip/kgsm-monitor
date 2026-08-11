using System.Text.Json.Serialization;

namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// A threshold episode opening — everything known about it at the moment it starts. <c>Producer</c> is
/// the leaf id of the daemon that established it, recorded so this store's rows are self-describing at
/// rest: a reader holding only the database can still say who measured this.
/// </summary>
public readonly record struct EpisodeOpen(
    string EpisodeId,
    string RuleKey,
    string Metric,
    string Scope,
    string? Ref,
    string? ServerId,
    long OpenedTs,
    string Band,
    double Value,
    double Threshold,
    string Producer);

/// <summary>
/// One recorded threshold episode. <c>ClosedTs</c> null means it is still firing. <c>PeakValue</c> is the
/// worst reading across the whole episode — the honest justification for it having existed, as opposed to
/// whatever the value happened to be at either end. <c>CloseReason</c> says WHY it ended — a value that
/// came back under its line and a rule that stopped being evaluated are not the same event, and calling the
/// second a recovery would claim a measurement nobody took.
/// </summary>
public sealed record EpisodeRow(
    string EpisodeId,
    string RuleKey,
    string Metric,
    string Scope,
    string? Ref,
    string? ServerId,
    long OpenedTs,
    long? ClosedTs,
    string PeakBand,
    double PeakValue,
    double OpenValue,
    double? CloseValue,
    double Threshold,
    string Producer,
    string? CloseReason);

/// <summary>The <c>GET /thresholds/episodes</c> response.</summary>
public sealed record EpisodesResponse(IReadOnlyList<EpisodeRow> Episodes);

/// <summary>Source-generated JSON for the episode surface. Daemon-local, like the rest of the history
/// serialization — the shared contracts package stays limited to the snapshot graph.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EpisodesResponse))]
public sealed partial class MonitorEpisodeJsonContext : JsonSerializerContext;
