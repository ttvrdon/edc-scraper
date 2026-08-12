using EdcScraper;
using EdcScraper.Models;
using EdcScraper.Sample.Configuration;

var builder = Host.CreateApplicationBuilder(args);

var settings = builder.Configuration.GetSection(Settings.SectionName).Get<Settings>() ?? new Settings();

var email    = settings.Username;
var password = settings.Password;

// Resolve the export date range. If not configured, default to yesterday.
var yesterday = DateTime.Today.AddDays(-1);
var dateFrom  = settings.DateFrom ?? yesterday;
var dateTo    = settings.DateTo   ?? yesterday;

await using var client = new EdcScraperClient();

try
{
    Console.WriteLine("EdcScraper sample\n");

    // 1. Login
    Console.WriteLine("1. Logging in…");
    await client.LoginAsync(email, password);
    Console.WriteLine("   ✓ Login successful\n");

    // 2. Request a new export for the configured (or default) date range
    Console.WriteLine($"2. Requesting a new report for {dateFrom:dd.MM.yyyy}–{dateTo:dd.MM.yyyy} " +
                      $"(sharing group {settings.SharingGroupId})…");
    var export = await client.CreateExportAsync(
        ExportRequest.BySharingGroup(
            sharingGroupId: settings.SharingGroupId,
            dateFrom: dateFrom,
            dateTo:   dateTo,
            viewType: ViewType.Daily));
    Console.WriteLine($"   ✓ Export scheduled: ID={export.Id}\n");

    // 3. Wait for the report to be generated and download the CSV
    Console.WriteLine("3. Waiting for the report to be generated (max 10 min)…");
    var csvBytes = await client.WaitAndDownloadAsync(export.Id);

    var outputPath = Path.Combine(
        AppContext.BaseDirectory,
        $"export_{dateFrom:yyyyMMdd}_{dateTo:yyyyMMdd}_{export.Id}.csv");
    await File.WriteAllBytesAsync(outputPath, csvBytes);
    Console.WriteLine($"   ✓ Downloaded {csvBytes.Length:N0} bytes");
    Console.WriteLine($"   ✓ Saved CSV to: {outputPath}\n");

    Console.WriteLine("4. Done! ✓\n");
}
catch (Exception ex)
{
    Console.WriteLine($"✗ Error: {ex.Message}");
    if (ex.InnerException != null)
        Console.WriteLine($"  Inner: {ex.InnerException.Message}");
}
finally
{
    Console.WriteLine("Logging out…");
}
