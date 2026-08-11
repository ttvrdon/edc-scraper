using System.Text.Json.Serialization;

namespace EdcScraper.Models;

/// <summary>How the input EAN/group is selected.</summary>
public enum InputParameterType { Ean, SharingGroup }

/// <summary>Profile view type — individual EANs or EANd/EANo pairs.</summary>
public enum ProfileType { Standard, Pair }

/// <summary>Time granularity of the exported data.</summary>
public enum ViewType
{
    /// <summary>Quarter-hour values for daily settlement.</summary>
    Daily,
    /// <summary>Quarter-hour values for monthly settlement.</summary>
    Monthly,
    /// <summary>Current (live/snapshot) values at a given moment.</summary>
    Current
}

/// <summary>
/// Parameters for requesting a scheduled CSV export.
/// Use the static factory methods <see cref="ByEans"/> and <see cref="BySharingGroup"/>.
/// </summary>
public record ExportRequest
{
    // --- input selection ---
    public string[]? Eans { get; init; }
    public int? SharingGroupId { get; init; }

    // --- time range ---
    public DateTime DateFrom { get; init; }
    public DateTime DateTo { get; init; }

    // --- view options ---
    public ProfileType ProfileType { get; init; } = ProfileType.Standard;
    public ViewType ViewType { get; init; } = ViewType.Daily;

    /// <summary>Snapshot timestamp; required when <see cref="ViewType"/> is <see cref="ViewType.Current"/>.</summary>
    public DateTime? CurrentEnteredDateTime { get; init; }

    // --- data columns ---
    public bool IncludeMeasuredData { get; init; } = true;
    public bool IncludeEvaluationResults { get; init; } = true;

    /// <summary>Name of the resulting report file (without extension). Auto-generated if null.</summary>
    public string? FileName { get; init; }

    // ----------------------------------------------------------------
    // Factory helpers
    // ----------------------------------------------------------------

    /// <summary>Creates a request filtered to one or more specific EANs.</summary>
    public static ExportRequest ByEans(
        string[] eans,
        DateTime dateFrom,
        DateTime dateTo,
        ProfileType profileType = ProfileType.Standard,
        ViewType viewType = ViewType.Daily,
        bool includeMeasuredData = true,
        bool includeEvaluationResults = true,
        string? fileName = null) =>
        new()
        {
            Eans = eans,
            DateFrom = dateFrom,
            DateTo = dateTo,
            ProfileType = profileType,
            ViewType = viewType,
            IncludeMeasuredData = includeMeasuredData,
            IncludeEvaluationResults = includeEvaluationResults,
            FileName = fileName
        };

    /// <summary>Creates a request for an entire sharing group by its numeric ID.</summary>
    public static ExportRequest BySharingGroup(
        int sharingGroupId,
        DateTime dateFrom,
        DateTime dateTo,
        ProfileType profileType = ProfileType.Standard,
        ViewType viewType = ViewType.Daily,
        bool includeMeasuredData = true,
        bool includeEvaluationResults = true,
        string? fileName = null) =>
        new()
        {
            SharingGroupId = sharingGroupId,
            DateFrom = dateFrom,
            DateTo = dateTo,
            ProfileType = profileType,
            ViewType = viewType,
            IncludeMeasuredData = includeMeasuredData,
            IncludeEvaluationResults = includeEvaluationResults,
            FileName = fileName
        };
}

/// <summary>Response returned by the export scheduling endpoint.</summary>
public record ExportResponse
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("reportState")]
    public string ReportState { get; init; } = "";
}

// Internal DTO sent to the API — mapped from ExportRequest
internal record ApiExportRequest
{
    [JsonPropertyName("eans")]
    public string[]? Eans { get; init; }

    [JsonPropertyName("sseId")]
    public int? SseId { get; init; }

    [JsonPropertyName("profileType")]
    public string ProfileType { get; init; } = "STANDARD";

    [JsonPropertyName("calculationType")]
    public string? CalculationType { get; init; }

    [JsonPropertyName("currentEnteredDateTime")]
    public string? CurrentEnteredDateTime { get; init; }

    [JsonPropertyName("inputData")]
    public bool InputData { get; init; }

    [JsonPropertyName("outputData")]
    public bool OutputData { get; init; }

    [JsonPropertyName("dateFrom")]
    public string DateFrom { get; init; } = "";

    [JsonPropertyName("dateTo")]
    public string DateTo { get; init; } = "";

    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = "";
}
