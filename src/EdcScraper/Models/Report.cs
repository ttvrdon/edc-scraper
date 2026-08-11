using System.Text.Json.Serialization;

namespace EdcScraper.Models;

public record Report
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("reportType")]
    public string ReportType { get; init; } = "";

    [JsonPropertyName("reportState")]
    public string ReportState { get; init; } = "";

    [JsonPropertyName("requested")]
    public DateTime Requested { get; init; }

    [JsonPropertyName("generated")]
    public DateTime? Generated { get; init; }
}

public record ReportListResponse
{
    [JsonPropertyName("content")]
    public Report[] Content { get; init; } = [];

    [JsonPropertyName("page")]
    public ReportPage Page { get; init; } = new();
}

public record ReportPage
{
    [JsonPropertyName("size")]
    public int Size { get; init; }

    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("totalElements")]
    public int TotalElements { get; init; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; init; }
}

