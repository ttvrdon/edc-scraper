using System.Text;
using EdcScraper.Models;
using EdcScraper.Worker.Configuration;
using EdcScraper.Worker.Data;
using Microsoft.Extensions.Options;

namespace EdcScraper.Worker.Services;

/// <summary>
/// Runs the scrape once on startup: logs in, fetches each day in the configured
/// window, stores per-EAN intervals and a daily summary in SQLite, then stops the host.
/// External scheduling (Docker/K8s cron) drives the daily cadence.
/// </summary>
public sealed class ScraperJob : BackgroundService
{
    private readonly ScraperDatabase _database;
    private readonly EdcOptions _edcOptions;
    private readonly FetchOptions _fetchOptions;
    private readonly DatabaseOptions _databaseOptions;
    private readonly ILogger<ScraperJob> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public ScraperJob(
        ScraperDatabase database,
        IOptions<EdcOptions> edcOptions,
        IOptions<FetchOptions> fetchOptions,
        IOptions<DatabaseOptions> databaseOptions,
        ILogger<ScraperJob> logger,
        IHostApplicationLifetime lifetime)
    {
        _database = database;
        _edcOptions = edcOptions.Value;
        _fetchOptions = fetchOptions.Value;
        _databaseOptions = databaseOptions.Value;
        _logger = logger;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
            Environment.ExitCode = 0;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("Scrape cancelled.");
            Environment.ExitCode = 130;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scrape failed: {Message}", ex.Message);
            Environment.ExitCode = 1;
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_edcOptions.Username) || string.IsNullOrWhiteSpace(_edcOptions.Password))
            throw new InvalidOperationException("Edc:Username and Edc:Password must be configured.");
        if (_edcOptions.SharingGroupId <= 0)
            throw new InvalidOperationException("Edc:SharingGroupId must be configured.");

        _logger.LogInformation("Initializing DB at path: {Path}", _databaseOptions.Path);
        await _database.InitializeAsync(cancellationToken);

        var lastDay = _fetchOptions.LookbackFromDate ?? DateTime.Today.AddDays(-1);
        var earliestAllowed = lastDay.AddDays(-(FetchOptions.MaxLookbackDays - 1));
        DateTime firstDay;

        if (_fetchOptions.LookbackDays.HasValue)
        {
            var lookback = Math.Clamp(_fetchOptions.LookbackDays ?? 1, 1, FetchOptions.MaxLookbackDays);

            firstDay = lastDay.AddDays(-(lookback - 1));
            _logger.LogInformation(
                "Using explicit lookback of {Days} day(s).", lookback);
        }
        else
        {
            var lastFetched = await _database.GetLastFetchedDateAsync(_edcOptions.SharingGroupId, cancellationToken);
            if (lastFetched is null)
            {
                firstDay = earliestAllowed;
                _logger.LogInformation(
                    "No previous fetch found; fetching up to {Days} day(s) of history.",
                    FetchOptions.MaxLookbackDays);
            }
            else
            {
                firstDay = lastFetched.Value.AddDays(1);
                _logger.LogInformation(
                    "Resuming after last fetched day {Last:yyyy-MM-dd}.", lastFetched.Value);
            }

            // Never go further back than the allowed window.
            if (firstDay < earliestAllowed)
                firstDay = earliestAllowed;
        }

        if (firstDay > lastDay)
        {
            _logger.LogInformation("Data is already up to date; nothing to fetch.");
            return;
        }

        var dayCount = (lastDay - firstDay).Days + 1;
        _logger.LogInformation(
            "Starting scrape for sharing group {Group}, {Days} day(s): {From:yyyy-MM-dd}..{To:yyyy-MM-dd}",
            _edcOptions.SharingGroupId, dayCount, firstDay, lastDay);

        await using var client = new EdcScraperClient();

        _logger.LogInformation("Logging in as {User}…", _edcOptions.Username);
        await client.LoginAsync(_edcOptions.Username, _edcOptions.Password, cancellationToken);

        await RequestAndProcessDataAsync(client, firstDay, lastDay, cancellationToken);

        await client.LogoutAsync(cancellationToken);

        _logger.LogInformation("Scrape completed successfully.");
    }

    private async Task RequestAndProcessDataAsync(EdcScraperClient client, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Requesting export for {From:yyyy-MM-dd}..{To:yyyy-MM-dd}…", from, to);

        var export = await client.CreateExportAsync(
            ExportRequest.BySharingGroup(
                sharingGroupId: _edcOptions.SharingGroupId,
                dateFrom: from,
                dateTo: to,
                viewType: ViewType.Daily),
            cancellationToken);

        var records = await client.WaitAndParseAsync(export.Id, cancellationToken: cancellationToken);
        if (records.Count == 0)
        {
            _logger.LogWarning("No records returned for {From:yyyy-MM-dd}..{To:yyyy-MM-dd}; skipping.", from, to);
            return;
        }

        var fetchedAt = DateTime.UtcNow;

        var dayGroups = records.GroupBy(r => r.Date).OrderBy(g => g.Key);
        foreach (var dayGroup in dayGroups)
        {
            var day = dayGroup.Key;
            var dayRecords = dayGroup.ToList();
            _logger.LogInformation(
                "Processing {Count} record(s) for {Day:yyyy-MM-dd}…", dayRecords.Count, day);

            var intervals = MapIntervals(dayRecords, fetchedAt);
            var summary = BuildSummary(day, dayRecords, intervals.Count, fetchedAt);

            await _database.UpsertIntervalsAsync(intervals, cancellationToken);
            await _database.UpsertDailySummaryAsync(summary, cancellationToken);

            _logger.LogInformation(
                "Stored {Intervals} interval rows and daily summary for {Day:yyyy-MM-dd}.",
                intervals.Count, day);
        }

        await _database.UpsertFetchStateAsync(
            new FetchStateRow
            {
                SharingGroupId = _edcOptions.SharingGroupId,
                LastFetchedDate = records.Max(r => r.Date),
                LastFetchedAt = fetchedAt,
            },
            cancellationToken);
    }

    private List<EnergyIntervalRow> MapIntervals(IReadOnlyList<EnergyDataRecord> records, DateTime fetchedAt)
    {
        var rows = new List<EnergyIntervalRow>();
        foreach (var record in records)
        {
            foreach (var ean in record.Eans.Values)
            {
                rows.Add(new EnergyIntervalRow
                {
                    SharingGroupId = _edcOptions.SharingGroupId,
                    Date = record.Date,
                    TimeFrom = record.TimeFrom,
                    TimeTo = record.TimeTo,
                    Ean = ean.Ean,
                    Suffix = ean.Suffix,
                    Kind = ean.Kind.ToString(),
                    In = ean.In,
                    Out = ean.Out,
                    Shared = ean.Shared,
                    FetchedAt = fetchedAt,
                });
            }
        }

        return rows;
    }

    private DailySummaryRow BuildSummary(
        DateTime day,
        IReadOnlyList<EnergyDataRecord> records,
        int intervalCount,
        DateTime fetchedAt) =>
        new()
        {
            SharingGroupId = _edcOptions.SharingGroupId,
            Date = day,
            TotalProducedToGrid = records.Sum(r => r.TotalProducedToGrid),
            TotalSoldToProvider = records.Sum(r => r.TotalSoldToProvider),
            TotalSharedProduction = records.Sum(r => r.TotalSharedProduction),
            TotalConsumedFromGrid = records.Sum(r => r.TotalConsumedFromGrid),
            TotalTakenFromGrid = records.Sum(r => r.TotalTakenFromGrid),
            TotalSharedConsumption = records.Sum(r => r.TotalSharedConsumption),
            IntervalCount = intervalCount,
            FetchedAt = fetchedAt,
        };
}
