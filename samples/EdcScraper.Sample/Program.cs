using EdcScraper;
using EdcScraper.Models;

var email    = Environment.GetEnvironmentVariable("EDC_EMAIL")    ?? "EMAIL_ENV_VAR";
var password = Environment.GetEnvironmentVariable("EDC_PASSWORD") ?? "PASSWORD_ENV_VAR";

await using var client = new EdcScraperClient();

try
{
    Console.WriteLine("Testing EdcScraper library...\n");

    // Login
    Console.WriteLine("1. Logging in…");
    await client.LoginAsync(email, password);
    Console.WriteLine("   ✓ Login successful\n");

    // List reports
    Console.WriteLine("2. Listing existing reports…");
    var list = await client.ListReportsAsync();
    Console.WriteLine($"   ✓ Found {list.Content.Length} reports");
    var generatedReports = list.Content.Where(r => r.ReportState == "GENERATED").ToList();
    Console.WriteLine($"   ✓ {generatedReports.Count} are GENERATED\n");

    // Download and parse CSV
    if (generatedReports.Count > 0)
    {
        Console.WriteLine("3. Downloading and parsing CSV…");
        var report = generatedReports.First();
        var csv = await client.DownloadReportAsync(report.Id);
        var csvText = System.Text.Encoding.UTF8.GetString(csv);

        var records = EdcScraperClient.ParseEnergyDataCsv(csvText);
        Console.WriteLine($"   ✓ Parsed {records.Count} energy data records\n");

        // Show sample records
        Console.WriteLine("   Sample records:");
        foreach (var record in records.Take(3))
        {
            Console.WriteLine($"     {record.Date:dd.MM.yyyy} {record.TimeFrom:hh\\:mm}-{record.TimeTo:hh\\:mm}");
            foreach (var (ean, (inVal, outVal)) in record.Eans.Take(2))
            {
                Console.WriteLine($"       {ean}: IN={inVal,8:F2}, OUT={outVal,8:F2}");
            }
        }
    }

    // Test WaitAndParseAsync
    Console.WriteLine("\n4. Creating a new export and waiting for it…");
    var export = await client.CreateExportAsync(
        ExportRequest.BySharingGroup(
            sharingGroupId: 36557,
            dateFrom: DateTime.Today.AddDays(-3),
            dateTo:   DateTime.Today,
            viewType: ViewType.Daily));
    Console.WriteLine($"   ✓ Export scheduled: ID={export.Id}\n");

    Console.WriteLine("   Polling for completion (max 10 min)…");
    var energyRecords = await client.WaitAndParseAsync(export.Id);
    Console.WriteLine($"   ✓ Got {energyRecords.Count} energy records from parsed CSV\n");

    Console.WriteLine("5. Test complete! ✓\n");
}
catch (Exception ex)
{
    Console.WriteLine($"✗ Error: {ex.Message}");
    if (ex.InnerException != null)
        Console.WriteLine($"  Inner: {ex.InnerException.Message}");
}
finally
{
    Console.WriteLine("6. Logging out…");
}




