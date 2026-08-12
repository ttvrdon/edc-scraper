namespace EdcScraper.Models;

/// <summary>
/// Represents a single row from an EDC energy data export CSV file.
/// Contains the date/time interval and the energy values for every EAN in the sharing group,
/// along with the sharing figures derived from those values.
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
    /// Per-EAN energy values for this interval, keyed by the bare EAN identifier
    /// (e.g. "859182400221784180"). Each entry exposes the raw IN/OUT values, the EAN
    /// <see cref="EanKind"/>, and the derived <see cref="EanEnergyData.Shared"/> amount.
    /// </summary>
    public required IReadOnlyDictionary<string, EanEnergyData> Eans { get; init; }

    /// <summary>
    /// All production EANs (CSV suffix <c>-D</c>) in this interval.
    /// </summary>
    public IEnumerable<EanEnergyData> Production => Eans.Values.Where(e => e.Kind == EanKind.Production);

    /// <summary>
    /// All consumption EANs (CSV suffix <c>-O</c>) in this interval.
    /// </summary>
    public IEnumerable<EanEnergyData> Consumption => Eans.Values.Where(e => e.Kind == EanKind.Consumption);

    /// <summary>
    /// Total energy sent to the grid by all production EANs in kWh (sum of production <see cref="EanEnergyData.In"/>).
    /// </summary>
    public decimal TotalProducedToGrid => Production.Sum(e => e.In);

    /// <summary>
    /// Total energy sold to the electricity provider in kWh (sum of production <see cref="EanEnergyData.Out"/>).
    /// This is the part of production that was not shared.
    /// </summary>
    public decimal TotalSoldToProvider => Production.Sum(e => e.Out);

    /// <summary>
    /// Total energy shared into the group by all production EANs in kWh
    /// (sum of production <see cref="EanEnergyData.Shared"/> = IN - OUT).
    /// </summary>
    public decimal TotalSharedProduction => Production.Sum(e => e.Shared);

    /// <summary>
    /// Total energy consumed from the grid by all consumption EANs in kWh (sum of consumption <see cref="EanEnergyData.In"/>).
    /// </summary>
    public decimal TotalConsumedFromGrid => Consumption.Sum(e => e.In);

    /// <summary>
    /// Total energy that still had to be taken from the grid in kWh (sum of consumption <see cref="EanEnergyData.Out"/>).
    /// This is the part of consumption that was not covered by sharing.
    /// </summary>
    public decimal TotalTakenFromGrid => Consumption.Sum(e => e.Out);

    /// <summary>
    /// Total energy received from the group by all consumption EANs in kWh
    /// (sum of consumption <see cref="EanEnergyData.Shared"/> = IN - OUT).
    /// </summary>
    public decimal TotalSharedConsumption => Consumption.Sum(e => e.Shared);
}

