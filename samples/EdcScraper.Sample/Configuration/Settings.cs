namespace EdcScraper.Sample.Configuration;

public sealed class Settings
{
    public const string SectionName = "EdcScraperSample";

    public string Username { get; set; } = null!;

    public string Password { get; set; } = null!;

    /// <summary>Sharing group to export data for.</summary>
    public int SharingGroupId { get; set; } = 0;

    /// <summary>Start of the export range. If not set, yesterday is used.</summary>
    public DateTime? DateFrom { get; set; }

    /// <summary>End of the export range. If not set, yesterday is used.</summary>
    public DateTime? DateTo { get; set; }
}
