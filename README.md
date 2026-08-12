# EdcScraper

A C# HTTP client library for the [EDC CR portal](https://portal.edc-cr.cz) that can:

- **Log in** using email + password (OpenID Connect authorization code + PKCE flow, no browser required)
- **Schedule CSV exports** — either by EAN list or by sharing group ID
- **Poll** until the export is ready
- **Download** the resulting CSV file

## Repository layout

- [.NET library (`src/`, `tests/`, `samples/`)](C:/Projects/Personal/edc-scraper.worktrees/update-energydatarecord-calculations/src/)
- [Home Assistant integration (`homeassistant/`)](C:/Projects/Personal/edc-scraper.worktrees/update-energydatarecord-calculations/homeassistant/)

The Home Assistant/HACS files are intentionally isolated under [homeassistant/](C:/Projects/Personal/edc-scraper.worktrees/update-energydatarecord-calculations/homeassistant/) so the C# and Python parts do not mix at the repository root.

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
    Console.WriteLine($"  Shared production:  {record.TotalSharedProduction:F2} kWh");
    Console.WriteLine($"  Shared consumption: {record.TotalSharedConsumption:F2} kWh");
    foreach (var meter in record.Eans.Values)
    {
        Console.WriteLine($"  {meter.Ean} ({meter.Kind}): IN={meter.In:F2} OUT={meter.Out:F2} SHARED={meter.Shared:F2}");
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

Represents a single row (one 15-minute interval) from an EDC energy data export CSV. Returned by `WaitAndParseAsync()` or `ParseEnergyDataCsv()`.

```csharp
public record EnergyDataRecord
{
    public required DateTime Date { get; init; }                              // dd.MM.yyyy
    public required TimeSpan TimeFrom { get; init; }                          // HH:mm
    public required TimeSpan TimeTo { get; init; }                            // HH:mm

    // Per-EAN data, keyed by the bare EAN identifier (no -D/-O suffix)
    public required IReadOnlyDictionary<string, EanEnergyData> Eans { get; init; }

    // Convenience views
    public IEnumerable<EanEnergyData> Production { get; }                     // -D EANs
    public IEnumerable<EanEnergyData> Consumption { get; }                    // -O EANs

    // Aggregated interval totals (kWh)
    public decimal TotalProducedToGrid { get; }                              // Σ production IN
    public decimal TotalSoldToProvider { get; }                             // Σ production OUT
    public decimal TotalSharedProduction { get; }                           // Σ (IN - OUT)
    public decimal TotalConsumedFromGrid { get; }                           // Σ consumption IN
    public decimal TotalTakenFromGrid { get; }                              // Σ consumption OUT
    public decimal TotalSharedConsumption { get; }                          // Σ (IN - OUT)
}
```

### `EanEnergyData`

The energy values and derived sharing figures for a single EAN in one interval.

```csharp
public sealed record EanEnergyData
{
    public required string Ean { get; init; }        // e.g. "859182400221784180"
    public string? Suffix { get; init; }             // "D", "O", or null
    public required EanKind Kind { get; init; }      // Production, Consumption, Unknown
    public required decimal In { get; init; }        // raw IN- column
    public required decimal Out { get; init; }       // raw OUT- column
    public decimal Shared => In - Out;               // energy shared within the group
}
```

The meaning of each value depends on `Kind`:

| Kind (suffix) | `In` | `Out` | `Shared` (`In - Out`) |
|---|---|---|---|
| `Production` (`-D`) | Total energy sent to the grid | Not shared, sold to the provider | Energy provided to the group for sharing |
| `Consumption` (`-O`) | Total energy consumed from the grid | Still had to be taken from the grid | Energy received from sharing |

Example:
```csharp
var record = records[0];
Console.WriteLine($"Date: {record.Date:dd.MM.yyyy}");
Console.WriteLine($"Time: {record.TimeFrom:hh\\:mm} - {record.TimeTo:hh\\:mm}");
Console.WriteLine($"Shared production:  {record.TotalSharedProduction:F2} kWh");
Console.WriteLine($"Shared consumption: {record.TotalSharedConsumption:F2} kWh");

foreach (var meter in record.Eans.Values)
{
    Console.WriteLine($"  {meter.Ean} ({meter.Kind}): IN={meter.In:F2} OUT={meter.Out:F2} SHARED={meter.Shared:F2}");
}
// Output example:
// Date: 07.08.2026
// Time: 00:00 - 00:15
// Shared production:  0.00 kWh
// Shared consumption: 0.00 kWh
//   859182400221784180 (Production): IN=0.00 OUT=0.00 SHARED=0.00
//   859182400204460056 (Consumption): IN=0.00 OUT=0.00 SHARED=0.00
//   859182400611332328 (Consumption): IN=-0.02 OUT=-0.02 SHARED=0.00
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
  - Suffix `-D` = production/generation EAN, `-O` = consumption/utilization EAN
  - Values: Decimal numbers with comma as separator (Czech format, e.g., `0,5` = 0.5)
  - Production `IN` = total sent to grid, `OUT` = part sold to the provider (not shared)
  - Consumption `IN` = total consumed from grid, `OUT` = part still taken from the grid
  - Shared energy (both kinds) = `IN - OUT`

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
