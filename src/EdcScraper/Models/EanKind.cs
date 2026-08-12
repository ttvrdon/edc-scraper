namespace EdcScraper.Models;

/// <summary>
/// Identifies the role of an EAN (metering point) within a sharing group,
/// derived from the suffix on the CSV column name.
/// </summary>
public enum EanKind
{
    /// <summary>
    /// The kind could not be determined from the EAN suffix.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Production / generation metering point (CSV suffix <c>-D</c>, "dodávka").
    /// Feeds energy into the grid and offers it for sharing.
    /// </summary>
    Production,

    /// <summary>
    /// Consumption / utilization metering point (CSV suffix <c>-O</c>, "odběr").
    /// Draws energy from the grid and from the sharing group.
    /// </summary>
    Consumption,
}
