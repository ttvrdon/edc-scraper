namespace EdcScraper.Models;

/// <summary>
/// Energy values for a single EAN (metering point) within one 15-minute interval,
/// together with the sharing figures derived from the raw IN/OUT columns.
/// </summary>
/// <remarks>
/// The raw CSV provides two values per EAN, whose meaning depends on the EAN <see cref="Kind"/>:
/// <list type="bullet">
///   <item>
///     <description>
///     <b>Production</b> (<c>-D</c>): <see cref="In"/> is the total energy sent to the grid in the
///     interval; <see cref="Out"/> is the part not used for sharing and sold to the electricity
///     provider. The energy actually shared is <see cref="Shared"/> = <see cref="In"/> - <see cref="Out"/>.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Consumption</b> (<c>-O</c>): <see cref="In"/> is the total energy consumed from the grid in
///     the interval; <see cref="Out"/> is the part that still had to be taken from the grid. The energy
///     covered by sharing is <see cref="Shared"/> = <see cref="In"/> - <see cref="Out"/>.
///     </description>
///   </item>
/// </list>
/// </remarks>
public sealed record EanEnergyData
{
    /// <summary>
    /// The bare EAN identifier without the kind suffix (e.g. "859182400221784180").
    /// </summary>
    public required string Ean { get; init; }

    /// <summary>
    /// The suffix that followed the EAN in the CSV column name (e.g. "D" or "O"), or <c>null</c> when
    /// the column had no suffix.
    /// </summary>
    public string? Suffix { get; init; }

    /// <summary>
    /// The role of this EAN within the sharing group, derived from <see cref="Suffix"/>.
    /// </summary>
    public required EanKind Kind { get; init; }

    /// <summary>
    /// Raw <c>IN-</c> column value in kWh.
    /// Production: total energy sent to the grid. Consumption: total energy consumed from the grid.
    /// </summary>
    public required decimal In { get; init; }

    /// <summary>
    /// Raw <c>OUT-</c> column value in kWh.
    /// Production: energy not shared and sold to the provider. Consumption: energy that had to be taken from the grid.
    /// </summary>
    public required decimal Out { get; init; }

    /// <summary>
    /// Energy shared within the group in kWh, computed as <see cref="In"/> - <see cref="Out"/>.
    /// Production: energy provided to the group for sharing. Consumption: energy received from sharing.
    /// </summary>
    public decimal Shared => In - Out;

    /// <summary>
    /// Creates an <see cref="EanEnergyData"/> from a raw CSV key (e.g. "859182400221784180-D") and its IN/OUT values.
    /// </summary>
    /// <param name="rawKey">The EAN key from the CSV column name, optionally suffixed with "-D" or "-O".</param>
    /// <param name="in">Raw IN value in kWh.</param>
    /// <param name="out">Raw OUT value in kWh.</param>
    public static EanEnergyData FromRaw(string rawKey, decimal @in, decimal @out)
    {
        var (ean, suffix) = SplitKey(rawKey);
        return new EanEnergyData
        {
            Ean = ean,
            Suffix = suffix,
            Kind = KindFromSuffix(suffix),
            In = @in,
            Out = @out,
        };
    }

    private static (string Ean, string? Suffix) SplitKey(string rawKey)
    {
        var dashIndex = rawKey.LastIndexOf('-');
        if (dashIndex <= 0 || dashIndex == rawKey.Length - 1)
            return (rawKey, null);

        return (rawKey[..dashIndex], rawKey[(dashIndex + 1)..]);
    }

    private static EanKind KindFromSuffix(string? suffix) => suffix?.ToUpperInvariant() switch
    {
        "D" => EanKind.Production,
        "O" => EanKind.Consumption,
        _ => EanKind.Unknown,
    };
}
