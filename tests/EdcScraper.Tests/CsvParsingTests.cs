using EdcScraper;
using EdcScraper.Models;

namespace EdcScraper.Tests;

public class CsvParsingTests
{
    /// <summary>
    /// Test parsing a minimal CSV with one EAN and two records.
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_MinimalValidCsv_ReturnsCorrectRecords()
    {
        // Arrange
        var csv = """
            Datum;Cas od;Cas do;IN-859182400221784180-D;OUT-859182400221784180-D
            07.08.2026;00:00;00:15;0,5;0,0
            07.08.2026;00:15;00:30;1,25;-0,5
            """;

        // Act
        var records = EdcScraperClient.ParseEnergyDataCsv(csv);

        // Assert
        Assert.Equal(2, records.Count);

        // First record
        Assert.Equal(new DateTime(2026, 8, 7), records[0].Date);
        Assert.Equal(TimeSpan.FromMinutes(0), records[0].TimeFrom);
        Assert.Equal(TimeSpan.FromMinutes(15), records[0].TimeTo);
        Assert.Single(records[0].Eans);
        var first = records[0].Eans["859182400221784180"];
        Assert.Equal(0.5m, first.In);
        Assert.Equal(0.0m, first.Out);
        Assert.Equal(EanKind.Production, first.Kind);

        // Second record
        Assert.Equal(new DateTime(2026, 8, 7), records[1].Date);
        Assert.Equal(TimeSpan.FromMinutes(15), records[1].TimeFrom);
        Assert.Equal(TimeSpan.FromMinutes(30), records[1].TimeTo);
        var second = records[1].Eans["859182400221784180"];
        Assert.Equal(1.25m, second.In);
        Assert.Equal(-0.5m, second.Out);
    }

    /// <summary>
    /// Test parsing a CSV with multiple EANs (realistic case from portal).
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_MultipleEans_ParsesAllCorrectly()
    {
        // Arrange - realistic format from portal
        var csv = """
            Datum;Cas od;Cas do;IN-859182400221784180-D;OUT-859182400221784180-D;IN-859182400204460056-O;OUT-859182400204460056-O;IN-859182400611332328-O;OUT-859182400611332328-O
            07.08.2026;00:00;00:15;0,0;0,0;0,0;0,0;-0,02;-0,02
            07.08.2026;00:15;00:30;0,0;0,0;-0,01;-0,01;-0,02;-0,02
            """;

        // Act
        var records = EdcScraperClient.ParseEnergyDataCsv(csv);

        // Assert
        Assert.Equal(2, records.Count);
        Assert.Equal(3, records[0].Eans.Count);

        // Check kinds are derived from suffixes
        Assert.Equal(EanKind.Production, records[0].Eans["859182400221784180"].Kind);
        Assert.Equal(EanKind.Consumption, records[0].Eans["859182400204460056"].Kind);
        Assert.Equal(EanKind.Consumption, records[0].Eans["859182400611332328"].Kind);

        // Check first record values
        Assert.Equal(0.0m, records[0].Eans["859182400221784180"].In);
        Assert.Equal(-0.02m, records[0].Eans["859182400611332328"].In);
        Assert.Equal(-0.02m, records[0].Eans["859182400611332328"].Out);

        // Check second record, second EAN
        Assert.Equal(-0.01m, records[1].Eans["859182400204460056"].In);
        Assert.Equal(-0.01m, records[1].Eans["859182400204460056"].Out);
    }

    /// <summary>
    /// Production shared = IN - OUT (energy offered to the group).
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_ProductionShared_IsInMinusOut()
    {
        // Arrange
        var csv = """
            Datum;Cas od;Cas do;IN-EANP-D;OUT-EANP-D
            07.08.2026;00:00;00:15;5,0;1,5
            """;

        // Act
        var record = EdcScraperClient.ParseEnergyDataCsv(csv)[0];
        var meter = record.Eans["EANP"];

        // Assert
        Assert.Equal(EanKind.Production, meter.Kind);
        Assert.Equal(5.0m, meter.In);
        Assert.Equal(1.5m, meter.Out);
        Assert.Equal(3.5m, meter.Shared); // 5.0 - 1.5

        Assert.Equal(5.0m, record.TotalProducedToGrid);
        Assert.Equal(1.5m, record.TotalSoldToProvider);
        Assert.Equal(3.5m, record.TotalSharedProduction);
    }

    /// <summary>
    /// Consumption shared = IN - OUT (energy received from the group).
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_ConsumptionShared_IsInMinusOut()
    {
        // Arrange
        var csv = """
            Datum;Cas od;Cas do;IN-EANC-O;OUT-EANC-O
            07.08.2026;00:00;00:15;-4,0;-1,0
            """;

        // Act
        var record = EdcScraperClient.ParseEnergyDataCsv(csv)[0];
        var meter = record.Eans["EANC"];

        // Assert
        Assert.Equal(EanKind.Consumption, meter.Kind);
        Assert.Equal(-4.0m, meter.In);
        Assert.Equal(-1.0m, meter.Out);
        Assert.Equal(-3.0m, meter.Shared); // -4.0 - (-1.0)

        Assert.Equal(-4.0m, record.TotalConsumedFromGrid);
        Assert.Equal(-1.0m, record.TotalTakenFromGrid);
        Assert.Equal(-3.0m, record.TotalSharedConsumption);
    }

    /// <summary>
    /// Aggregates should sum across all EANs of the matching kind and ignore the other kind.
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_Aggregates_SumPerKind()
    {
        // Arrange
        var csv = """
            Datum;Cas od;Cas do;IN-P1-D;OUT-P1-D;IN-P2-D;OUT-P2-D;IN-C1-O;OUT-C1-O
            07.08.2026;00:00;00:15;3,0;1,0;2,0;0,5;-6,0;-2,0
            """;

        // Act
        var record = EdcScraperClient.ParseEnergyDataCsv(csv)[0];

        // Assert
        Assert.Equal(2, record.Production.Count());
        Assert.Single(record.Consumption);

        Assert.Equal(5.0m, record.TotalProducedToGrid);   // 3.0 + 2.0
        Assert.Equal(1.5m, record.TotalSoldToProvider);   // 1.0 + 0.5
        Assert.Equal(3.5m, record.TotalSharedProduction); // 2.0 + 1.5

        Assert.Equal(-6.0m, record.TotalConsumedFromGrid);
        Assert.Equal(-2.0m, record.TotalTakenFromGrid);
        Assert.Equal(-4.0m, record.TotalSharedConsumption);
    }

    /// <summary>
    /// A column with no recognizable suffix should be classified as Unknown.
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_UnknownSuffix_IsUnknownKind()
    {
        // Arrange
        var csv = """
            Datum;Cas od;Cas do;IN-PLAINEAN;OUT-PLAINEAN
            07.08.2026;00:00;00:15;1,0;0,25
            """;

        // Act
        var record = EdcScraperClient.ParseEnergyDataCsv(csv)[0];
        var meter = record.Eans["PLAINEAN"];

        // Assert
        Assert.Equal(EanKind.Unknown, meter.Kind);
        Assert.Null(meter.Suffix);
        Assert.Empty(record.Production);
        Assert.Empty(record.Consumption);
    }

    /// <summary>
    /// Test handling of empty lines in CSV.
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_WithEmptyLines_SkipsEmptyLines()
    {
        // Arrange
        var csv = """
            Datum;Cas od;Cas do;IN-859182400221784180-D;OUT-859182400221784180-D
            07.08.2026;00:00;00:15;0,5;0,0

            07.08.2026;00:15;00:30;1,0;-0,5
            
            """;

        // Act
        var records = EdcScraperClient.ParseEnergyDataCsv(csv);

        // Assert
        Assert.Equal(2, records.Count);  // Empty lines are skipped
    }

    /// <summary>
    /// Test that CSV with BOM (Byte Order Mark) is handled correctly.
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_WithBom_ParsesCorrectly()
    {
        // Arrange - CSV starting with BOM character
        var csv = "\ufeffDatum;Cas od;Cas do;IN-859182400221784180-D;OUT-859182400221784180-D\n07.08.2026;00:00;00:15;0,5;0,0";

        // Act
        var records = EdcScraperClient.ParseEnergyDataCsv(csv);

        // Assert
        Assert.Single(records);
        Assert.Equal(new DateTime(2026, 8, 7), records[0].Date);
    }

    /// <summary>
    /// Test parsing with Czech decimal separator (comma).
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_CzechDecimalFormat_ParsesCorrectly()
    {
        // Arrange - Czech format with commas as decimal separator
        var csv = """
            Datum;Cas od;Cas do;IN-859182400221784180-D;OUT-859182400221784180-D
            07.08.2026;00:00;00:15;123,456;-78,901
            """;

        // Act
        var records = EdcScraperClient.ParseEnergyDataCsv(csv);

        // Assert
        Assert.Single(records);
        Assert.Equal(123.456m, records[0].Eans["859182400221784180"].In);
        Assert.Equal(-78.901m, records[0].Eans["859182400221784180"].Out);
    }

    /// <summary>
    /// Test that rows with insufficient columns are skipped.
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_IncompleteRow_SkipsRow()
    {
        // Arrange
        var csv = """
            Datum;Cas od;Cas do;IN-859182400221784180-D;OUT-859182400221784180-D
            07.08.2026;00:00;00:15;0,5;0,0
            07.08.2026;00:15
            07.08.2026;00:30;00:45;1,0;-0,5
            """;

        // Act
        var records = EdcScraperClient.ParseEnergyDataCsv(csv);

        // Assert
        Assert.Equal(2, records.Count);  // Row with only 2 columns is skipped
    }

    /// <summary>
    /// Test that invalid date format throws EdcScraperException.
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_InvalidDateFormat_ThrowsException()
    {
        // Arrange
        var csv = """
            Datum;Cas od;Cas do;IN-859182400221784180-D;OUT-859182400221784180-D
            2026-08-07;00:00;00:15;0,5;0,0
            """;

        // Act & Assert
        var ex = Assert.Throws<EdcScraperException>(() => EdcScraperClient.ParseEnergyDataCsv(csv));
        Assert.Contains("Failed to parse CSV line", ex.Message);
    }

    /// <summary>
    /// Test that invalid time format throws EdcScraperException.
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_InvalidTimeFormat_ThrowsException()
    {
        // Arrange
        var csv = """
            Datum;Cas od;Cas do;IN-859182400221784180-D;OUT-859182400221784180-D
            07.08.2026;00-00;00:15;0,5;0,0
            """;

        // Act & Assert
        var ex = Assert.Throws<EdcScraperException>(() => EdcScraperClient.ParseEnergyDataCsv(csv));
        Assert.Contains("Failed to parse CSV line", ex.Message);
    }

    /// <summary>
    /// Test that invalid decimal value throws EdcScraperException.
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_InvalidDecimalValue_ThrowsException()
    {
        // Arrange
        var csv = """
            Datum;Cas od;Cas do;IN-859182400221784180-D;OUT-859182400221784180-D
            07.08.2026;00:00;00:15;abc;0,0
            """;

        // Act & Assert
        var ex = Assert.Throws<EdcScraperException>(() => EdcScraperClient.ParseEnergyDataCsv(csv));
        Assert.Contains("Failed to parse CSV line", ex.Message);
    }

    /// <summary>
    /// Test that insufficient header columns throws EdcScraperException.
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_InsufficientHeaderColumns_ThrowsException()
    {
        // Arrange
        var csv = """
            Datum;Cas od
            07.08.2026;00:00
            """;

        // Act & Assert
        var ex = Assert.Throws<EdcScraperException>(() => EdcScraperClient.ParseEnergyDataCsv(csv));
        Assert.Contains("must have at least 3 columns", ex.Message);
    }

    /// <summary>
    /// Test empty CSV (only header or completely empty).
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_EmptyCsv_ReturnsEmptyList()
    {
        // Arrange
        var csv = "";

        // Act
        var records = EdcScraperClient.ParseEnergyDataCsv(csv);

        // Assert
        Assert.Empty(records);
    }

    /// <summary>
    /// Test CSV with only header, no data rows.
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_HeaderOnly_ReturnsEmptyList()
    {
        // Arrange
        var csv = "Datum;Cas od;Cas do;IN-859182400221784180-D;OUT-859182400221784180-D";

        // Act
        var records = EdcScraperClient.ParseEnergyDataCsv(csv);

        // Assert
        Assert.Empty(records);
    }

    /// <summary>
    /// Test time boundary values (23:45 to 00:00 next day).
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_MidnightBoundary_ParsesCorrectly()
    {
        // Arrange
        var csv = """
            Datum;Cas od;Cas do;IN-859182400221784180-D;OUT-859182400221784180-D
            07.08.2026;23:45;23:59;0,5;0,0
            08.08.2026;00:00;00:15;1,0;-0,5
            """;

        // Act
        var records = EdcScraperClient.ParseEnergyDataCsv(csv);

        // Assert
        Assert.Equal(2, records.Count);
        Assert.Equal(TimeSpan.FromHours(23).Add(TimeSpan.FromMinutes(45)), records[0].TimeFrom);
        Assert.Equal(TimeSpan.FromHours(23).Add(TimeSpan.FromMinutes(59)), records[0].TimeTo);
        Assert.Equal(TimeSpan.Zero, records[1].TimeFrom);
        Assert.Equal(TimeSpan.FromMinutes(15), records[1].TimeTo);
    }

    /// <summary>
    /// Test negative energy values (generation/outbound).
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_NegativeValues_ParsesCorrectly()
    {
        // Arrange
        var csv = """
            Datum;Cas od;Cas do;IN-859182400221784180-D;OUT-859182400221784180-D
            07.08.2026;00:00;00:15;-1,5;-0,25
            """;

        // Act
        var records = EdcScraperClient.ParseEnergyDataCsv(csv);

        // Assert
        Assert.Single(records);
        var meter = records[0].Eans["859182400221784180"];
        Assert.Equal(-1.5m, meter.In);
        Assert.Equal(-0.25m, meter.Out);
    }

    /// <summary>
    /// Test zero values across all fields.
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_ZeroValues_ParsesCorrectly()
    {
        // Arrange
        var csv = """
            Datum;Cas od;Cas do;IN-859182400221784180-D;OUT-859182400221784180-D
            07.08.2026;00:00;00:15;0,0;0,0
            """;

        // Act
        var records = EdcScraperClient.ParseEnergyDataCsv(csv);

        // Assert
        Assert.Single(records);
        var meter = records[0].Eans["859182400221784180"];
        Assert.Equal(0.0m, meter.In);
        Assert.Equal(0.0m, meter.Out);
        Assert.Equal(0.0m, meter.Shared);
    }

    /// <summary>
    /// Test large number of EANs (realistic case with many properties).
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_ManyEans_ParsesAllCorrectly()
    {
        // Arrange
        var csv = """
            Datum;Cas od;Cas do;IN-EAN1-D;OUT-EAN1-D;IN-EAN2-O;OUT-EAN2-O;IN-EAN3-O;OUT-EAN3-O;IN-EAN4-O;OUT-EAN4-O;IN-EAN5-D;OUT-EAN5-D
            07.08.2026;00:00;00:15;1,1;1,2;2,1;2,2;3,1;3,2;4,1;4,2;5,1;5,2
            """;

        // Act
        var records = EdcScraperClient.ParseEnergyDataCsv(csv);

        // Assert
        Assert.Single(records);
        Assert.Equal(5, records[0].Eans.Count);
        Assert.Equal(1.1m, records[0].Eans["EAN1"].In);
        Assert.Equal(1.2m, records[0].Eans["EAN1"].Out);
        Assert.Equal(2.1m, records[0].Eans["EAN2"].In);
        Assert.Equal(2.2m, records[0].Eans["EAN2"].Out);
        Assert.Equal(5.1m, records[0].Eans["EAN5"].In);
        Assert.Equal(5.2m, records[0].Eans["EAN5"].Out);
    }

    /// <summary>
    /// Test mixed line endings (CRLF, LF).
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_MixedLineEndings_ParsesCorrectly()
    {
        // Arrange
        var csv = "Datum;Cas od;Cas do;IN-859182400221784180-D;OUT-859182400221784180-D\r\n07.08.2026;00:00;00:15;0,5;0,0\n07.08.2026;00:15;00:30;1,0;-0,5";

        // Act
        var records = EdcScraperClient.ParseEnergyDataCsv(csv);

        // Assert
        Assert.Equal(2, records.Count);
    }

    /// <summary>
    /// Test data row with extra whitespace around fields.
    /// </summary>
    [Fact]
    public void ParseEnergyDataCsv_ExtraWhitespace_ParsesCorrectly()
    {
        // Arrange
        var csv = """
            Datum;Cas od;Cas do;IN-859182400221784180-D;OUT-859182400221784180-D
              07.08.2026  ;  00:00  ;  00:15  ;  0,5  ;  0,0  
            """;

        // Act
        var records = EdcScraperClient.ParseEnergyDataCsv(csv);

        // Assert
        Assert.Single(records);
        Assert.Equal(new DateTime(2026, 8, 7), records[0].Date);
        var meter = records[0].Eans["859182400221784180"];
        Assert.Equal(0.5m, meter.In);
        Assert.Equal(0.0m, meter.Out);
    }
}
