namespace EdcScraper.Models;

/// <summary>
/// Represents a single row from an EDC energy data export CSV file.
/// Contains the date/time interval and energy values for all EANs in the sharing group.
/// </summary>
public record EnergyDataRecord
{
    /// <summary>
    /// Date of the energy data (format: dd.MM.yyyy in CSV)
    /// </summary>
    public required DateTime Date { get; init; }

    /// <summary>
    /// Time from (inclusive) in format HH:mm
    /// </summary>
    public required TimeSpan TimeFrom { get; init; }

    /// <summary>
    /// Time to (exclusive) in format HH:mm
    /// </summary>
    public required TimeSpan TimeTo { get; init; }

    /// <summary>
    /// Energy values for each EAN. Key format: "859182400221784180", Value: (In, Out) in kWh.
    /// The EAN may have a suffix after a dash (e.g., "-D" for distribution, "-O" for other).
    /// </summary>
    public required IReadOnlyDictionary<string, (decimal In, decimal Out)> Eans { get; init; }
}

