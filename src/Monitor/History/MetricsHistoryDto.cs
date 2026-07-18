namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// The history response shape served from <c>GET /metrics/history</c>. Daemon-local (the
/// monitor owns the metrics-history contract now); kgsm-api relays this JSON verbatim to the SPA,
/// so the shape is preserved end-to-end without a shared-contract package. Tier selection is
/// automatic by range: range &le; raw retention → raw (sample table, ~15s step); range &gt; raw
/// retention → rollup (rollup table, 5min step). Gaps are absent points (sparse series, no
/// carry-forward).
/// </summary>
public sealed record MetricsHistoryResponse(
    string EntityId,
    string Kind,
    string Range,
    int Step,
    string Tier,
    Dictionary<string, List<MetricsHistoryPoint>> Series);

/// <summary>A history point: raw tier carries just ts+value; rollup tier adds min/max/n.</summary>
public sealed record MetricsHistoryPoint(
    DateTimeOffset Ts,
    double Value,
    double? Min = null,
    double? Max = null,
    int? N = null);

/// <summary>Known range strings and their durations. Unknown/absent → 1h.</summary>
public static class MetricsRange
{
    public const string OneHour = "1h";
    public const string TwentyFourHours = "24h";
    public const string SevenDays = "7d";
    public const string ThirtyDays = "30d";

    public static TimeSpan? Parse(string? range) => range switch
    {
        OneHour => TimeSpan.FromHours(1),
        TwentyFourHours => TimeSpan.FromHours(24),
        SevenDays => TimeSpan.FromDays(7),
        ThirtyDays => TimeSpan.FromDays(30),
        _ => null
    };
}
