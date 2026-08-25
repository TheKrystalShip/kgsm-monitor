namespace TheKrystalShip.KGSM.Monitor.History;

/// <summary>
/// The history response shape served from <c>GET /metrics/history</c>. Daemon-local (the
/// monitor owns the metrics-history contract now); kgsm-api relays this JSON verbatim to the SPA,
/// so the shape is preserved end-to-end without a shared-contract package. Tier selection is
/// automatic by range: range ≤ raw retention → raw (sample table, ~15s step); range &gt; raw
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

/// <summary>
/// The range summary served from <c>GET /metrics/history/summary</c>: one aggregate row per entity and
/// metric of a kind, rather than a series each.
/// </summary>
/// <remarks>
/// <para>Exists because a panel that draws N ranges wants N triples, not N curves. Reading a range bar
/// for every hwmon channel through the per-entity endpoint is one request per channel, each returning a
/// full window of points so the client can reduce it to three numbers it could have been handed.</para>
/// <para>Aggregated over whichever tier covers the range, so a summary is exact for the window it
/// names: the rollup tier already stores per-bucket min/max, and a min-of-mins over buckets is the same
/// figure as a min over the raw samples beneath them. An entity with no rows in the window is absent
/// from <see cref="Entries"/> rather than present with zeros.</para>
/// </remarks>
public sealed record MetricsSummaryResponse(
    string Kind,
    string Range,
    string Tier,
    List<MetricsSummaryEntry> Entries);

/// <summary>One entity's aggregate for one metric over the window.</summary>
/// <param name="Samples">How many rows the figures were taken over — what makes a thin window visible
/// as thin, instead of a range that looks authoritative because it is drawn the same as any other.</param>
public sealed record MetricsSummaryEntry(
    string EntityId,
    string Metric,
    double Min,
    double Max,
    double Avg,
    double Last,
    long Samples);
