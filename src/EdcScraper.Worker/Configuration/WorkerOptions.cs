namespace EdcScraper.Worker.Configuration;

/// <summary>Root options bound from configuration.</summary>
public sealed class WorkerOptions
{
    public const string EdcSection = "Edc";
    public const string FetchSection = "Fetch";
    public const string DatabaseSection = "Database";
}

/// <summary>Credentials and target sharing group for the EDC portal.</summary>
public sealed class EdcOptions
{
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>Sharing group to export data for.</summary>
    public int SharingGroupId { get; set; }
}

/// <summary>Controls which days are fetched on each run.</summary>
public sealed class FetchOptions
{
    public const int MaxLookbackDays = 30;

    /// <summary>
    /// Number of days to fetch from past from <see cref="LookbackFromDate"/>. 1 means only one day.
    /// When <c>null</c> (not configured) the worker fetches all days missing since the
    /// last successful fetch, capped at <see cref="MaxLookbackDays"/> days in the past.
    /// Any configured value is clamped to the range 1..<see cref="MaxLookbackDays"/>.
    /// </summary>
    public int? LookbackDays { get; set; }

    /// <summary>
    /// The date from which to start looking back. If <c>null</c>, defaults to yesterday.
    /// </summary>
    public DateTime? LookbackFromDate { get; set; }
}

/// <summary>SQLite storage configuration.</summary>
public sealed class DatabaseOptions
{
    /// <summary>
    /// Full path to the SQLite database file. In the container this points at a
    /// mounted volume (e.g. /data/edc.db).
    /// </summary>
    public string Path { get; set; } = "/data/edc.db";
}
