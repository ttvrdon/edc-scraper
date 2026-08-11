# EdcScraper

A C# HTTP client library for the [EDC CR portal](https://portal.edc-cr.cz) that can:

- **Log in** using email + password (OpenID Connect authorization code + PKCE flow, no browser required)
- **Schedule CSV exports** — either by EAN list or by sharing group ID
- **Poll** until the export is ready
- **Download** the resulting CSV file

## Requirements

- .NET 10+

## Quick start

```csharp
using EdcScraper;

await using var client = new EdcScraperClient();

// 1. Login
await client.LoginAsync("user@example.com", "MyPassword123");

// 2a. Export by sharing group ID
var export = await client.CreateExportAsync(
    ExportRequest.BySharingGroup(
        sharingGroupId: 36557,
        dateFrom: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Local),
        dateTo:   new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Local),
        viewType: ViewType.Daily));

// 2b. — or — Export by individual EANs
var export2 = await client.CreateExportAsync(
    ExportRequest.ByEans(
        eans: ["859182400123456789", "859182400987654321"],
        dateFrom: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Local),
        dateTo:   new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Local)));

// 3. Wait for the server to generate the file, then download
byte[] csvBytes = await client.WaitAndDownloadAsync(export.Id);
await File.WriteAllBytesAsync("export.csv", csvBytes);
Console.WriteLine($"Downloaded {csvBytes.Length} bytes");

// 3b. — Or — Wait, download, AND parse CSV into energy data objects
var energyRecords = await client.WaitAndParseAsync(export.Id);
foreach (var record in energyRecords)
{
    Console.WriteLine($"{record.Date:dd.MM.yyyy} {record.TimeFrom:hh\\:mm}-{record.TimeTo:hh\\:mm}");
    foreach (var (ean, (inVal, outVal)) in record.Eans)
    {
        Console.WriteLine($"  {ean}: IN={inVal:F2} kWh, OUT={outVal:F2} kWh");
    }
}

// --- Or manage polling manually ---
// List all reports (server keeps reports for 14 days)
ReportListResponse reports = await client.ListReportsAsync();
foreach (var report in reports.Content)
    Console.WriteLine($"[{report.Id}] {report.Name} — {report.ReportState} @ {report.Requested}");

// Download a specific report by ID
string csv = await client.DownloadReportAsTextAsync(reportId: 875808);
```

## API reference

### `EdcScraperClient`

| Method | Description |
|---|---|
| `LoginAsync(email, password)` | Authenticates via Keycloak PKCE. Tokens are refreshed automatically. |
| `CreateExportAsync(request)` | Schedules a CSV export. Returns immediately with report `Id`. |
| `ListReportsAsync(page, perPage)` | Lists export reports (newest first, max 14 days old). |
| `DownloadReportAsync(reportId)` | Downloads a completed report as `byte[]`. |
| `DownloadReportAsTextAsync(reportId)` | Downloads a completed report as a UTF-8 string. |
| `WaitAndDownloadAsync(reportId, pollInterval, timeout)` | Polls until ready, then downloads as `byte[]`. Convenience wrapper. |
| `WaitAndParseAsync(reportId, pollInterval, timeout)` | Polls until ready, downloads, and parses CSV into `EnergyDataRecord[]`. |
| `ParseEnergyDataCsv(csvContent)` | Static method: parses CSV content into `EnergyDataRecord[]`. |

### `ExportRequest`

Use the static factory methods:

```csharp
// By sharing group
ExportRequest.BySharingGroup(sharingGroupId, dateFrom, dateTo, ...)

// By one or more EANs
ExportRequest.ByEans(eans, dateFrom, dateTo, ...)
```

Key options:

| Property | Default | Description |
|---|---|---|
| `ProfileType` | `Standard` | `Standard` (per-EAN) or `Pair` (EANd↔EANo pairs) |
| `ViewType` | `Daily` | `Daily`, `Monthly`, or `Current` (snapshot) |
| `IncludeMeasuredData` | `true` | Include distributor-measured values |
| `IncludeEvaluationResults` | `true` | Include sharing evaluation results |
| `FileName` | auto-generated | Custom name for the report file (without `.csv`) |

### `EnergyDataRecord`

Represents a single row from an EDC energy data export CSV. Returned by `WaitAndParseAsync()` or `ParseEnergyDataCsv()`.

```csharp
public record EnergyDataRecord
{
    public required DateTime Date { get; init; }                                   // dd.MM.yyyy
    public required TimeSpan TimeFrom { get; init; }                              // HH:mm
    public required TimeSpan TimeTo { get; init; }                                // HH:mm
    public required IReadOnlyDictionary<string, (decimal In, decimal Out)> Eans { get; init; }
}
```

Example:
```csharp
var record = records[0];
Console.WriteLine($"Date: {record.Date:dd.MM.yyyy}");
Console.WriteLine($"Time: {record.TimeFrom:hh\\:mm} - {record.TimeTo:hh\\:mm}");

foreach (var (ean, (inKwh, outKwh)) in record.Eans)
{
    Console.WriteLine($"  {ean}: IN={inKwh:F2} OUT={outKwh:F2}");
}
// Output example:
// Date: 07.08.2026
// Time: 00:00 - 00:15
//   859182400221784180-D: IN=0.00 OUT=0.00
//   859182400204460056-O: IN=0.00 OUT=0.00
//   859182400611332328-O: IN=-0.02 OUT=-0.02
```

## CSV format

The EDC CR portal exports energy data in semicolon-delimited CSV format (Czech locale):

**Header:**
```
Datum;Cas od;Cas do;IN-{EAN}-{SUFFIX};OUT-{EAN}-{SUFFIX};...
```

**Data rows:**
```
07.08.2026;00:00;00:15;0,5;-0,25;...
07.08.2026;00:15;00:30;1,0;-0,50;...
```

- **Datum**: Date in `dd.MM.yyyy` format
- **Cas od / Cas do**: Time range in `HH:mm` format (15-minute intervals)
- **EAN columns**: Dynamic number of IN/OUT pairs for each EAN
  - Column name: `IN-{EAN}-{SUFFIX}` or `OUT-{EAN}-{SUFFIX}`
  - Values: Decimal numbers with comma as separator (Czech format, e.g., `0,5` = 0.5)
  - Positive = consumption (IN) / generation (OUT)
  - Negative = typically reverse flows

The parser handles:
- Any number of EANs in the sharing group
- BOM (Byte Order Mark) if present
- Empty lines
- Extra whitespace around fields
- Mixed line endings (CRLF, LF)


The portal uses Keycloak with the **authorization code + PKCE** flow:

1. `GET` the SSO login page → parse the signed form `action` URL
2. `POST` credentials (username + password) → get a `302` redirect containing `?code=…`
3. `POST` to the token endpoint with the code + PKCE verifier → receive `access_token` + `refresh_token`
4. All API calls carry `Authorization: Bearer {access_token}`, `edc-contract-type: STANDARD`, and a random `x-correlation-id`

Tokens are refreshed automatically before they expire.
