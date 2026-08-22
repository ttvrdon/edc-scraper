namespace EdcScraper.Worker.Data;

/// <summary>
/// One row per 15-minute interval per EAN. Primary key is
/// (Date, TimeFrom, Ean) so re-runs upsert instead of duplicating.
/// </summary>
public sealed class EnergyIntervalRow
{
    public int SharingGroupId { get; init; }
    public DateTime Date { get; init; }
    public TimeSpan TimeFrom { get; init; }
    public TimeSpan TimeTo { get; init; }
    public string Ean { get; init; } = string.Empty;
    public string? Suffix { get; init; }
    public string Kind { get; init; } = string.Empty;
    public decimal In { get; init; }
    public decimal Out { get; init; }
    public decimal Shared { get; init; }
    public DateTime FetchedAt { get; init; }
}

/// <summary>
/// One aggregated row per day per sharing group. Primary key is
/// (Date, SharingGroupId).
/// </summary>
public sealed class DailySummaryRow
{
    public int SharingGroupId { get; init; }
    public DateTime Date { get; init; }
    public decimal TotalProducedToGrid { get; init; }
    public decimal TotalSoldToProvider { get; init; }
    public decimal TotalSharedProduction { get; init; }
    public decimal TotalConsumedFromGrid { get; init; }
    public decimal TotalTakenFromGrid { get; init; }
    public decimal TotalSharedConsumption { get; init; }
    public int IntervalCount { get; init; }
    public DateTime FetchedAt { get; init; }
}

/// <summary>
/// Tracks the last successful fetch for a sharing group so that subsequent runs
/// can resume and fetch only the missing days.
/// </summary>
public sealed class FetchStateRow
{
    public int SharingGroupId { get; init; }

    /// <summary>The most recent day (date) that was successfully fetched and stored.</summary>
    public DateTime LastFetchedDate { get; init; }

    /// <summary>UTC timestamp of when the last successful fetch completed.</summary>
    public DateTime LastFetchedAt { get; init; }
}
