using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdcScraper.Internal;
using EdcScraper.Models;

namespace EdcScraper;

/// <summary>
/// HTTP client for the EDC CR portal (https://portal.edc-cr.cz).
/// Supports PKCE login, listing reports, scheduling CSV exports, and downloading the results.
/// </summary>
/// <example>
/// <code>
/// // Always use 'await using' to ensure proper server-side logout,
/// // even if an exception occurs.
/// await using var client = new EdcScraperClient();
/// await client.LoginAsync("user@example.com", "password");
/// try
/// {
///     // Schedule an export for a sharing group
///     var export = await client.CreateExportAsync(
///         ExportRequest.BySharingGroup(sharingGroupId: 36557,
///             dateFrom: new DateTime(2026, 8, 1),
///             dateTo:   new DateTime(2026, 8, 11)));
///
///     // Poll until the report is generated (usually a few seconds to minutes)
///     Report? report = null;
///     while (report?.ReportState != "GENERATED")
///     {
///         await Task.Delay(TimeSpan.FromSeconds(5));
///         var list = await client.ListReportsAsync();
///         report = Array.Find(list.Content, r => r.Id == export.Id);
///     }
///
///     // Download the CSV bytes
///     var csv = await client.DownloadReportAsync(export.Id);
///     await File.WriteAllBytesAsync("export.csv", csv);
/// }
/// finally
/// {
///     // Server-side logout is automatically called when the using block exits.
///     // This invalidates the session on the server, even if an exception occurred above.
/// }
/// </code>
/// </example>
public sealed class EdcScraperClient : IAsyncDisposable, IDisposable
{
    private const string ApiBaseUrl = "https://api.portal.edc-cr.cz/api/v0";
    private const string ContractType = "STANDARD";

    private readonly HttpClient _apiHttpClient;
    private readonly AuthService _authService;
    private readonly JsonSerializerOptions _jsonOptions;

    public EdcScraperClient()
    {
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        _authService = new AuthService(_jsonOptions);

        _apiHttpClient = new HttpClient();
        BrowserHeaders.AddToBrowserHeaders(_apiHttpClient);
    }

    // ----------------------------------------------------------------
    // Authentication
    // ----------------------------------------------------------------

    /// <summary>
    /// Authenticates with the EDC portal using the provided credentials.
    /// Uses the OpenID Connect authorization code + PKCE flow.
    /// </summary>
    public Task LoginAsync(string email, string password, CancellationToken cancellationToken = default)
        => _authService.LoginAsync(email, password, cancellationToken);

    /// <summary>
    /// Performs proper server-side logout via Keycloak.
    /// This invalidates the session on the server and clears local tokens.
    /// Always call this or use the client with 'await using' to ensure logout happens.
    /// </summary>
    public Task LogoutAsync(CancellationToken cancellationToken = default)
        => _authService.LogoutAsync(cancellationToken);

    // ----------------------------------------------------------------
    // Reports
    // ----------------------------------------------------------------

    /// <summary>
    /// Lists scheduled export reports, newest first.
    /// The portal only shows reports younger than 14 days.
    /// </summary>
    /// <param name="page">Zero-based page number.</param>
    /// <param name="perPage">Number of records per page (max 100).</param>
    public async Task<ReportListResponse> ListReportsAsync(
        int page = 0,
        int perPage = 25,
        CancellationToken cancellationToken = default)
    {
        var url = $"{ApiBaseUrl}/report?page={page}&perPage={perPage}&sortBy=requested&sortOrder=desc";
        var json = await GetStringAsync(url, cancellationToken);
        return Deserialize<ReportListResponse>(json);
    }

    // ----------------------------------------------------------------
    // Export creation
    // ----------------------------------------------------------------

    /// <summary>
    /// Schedules an async CSV export on the server.
    /// Returns immediately with a report ID; the actual file is generated in the background.
    /// Use <see cref="ListReportsAsync"/> to poll the status, then <see cref="DownloadReportAsync"/> to fetch the file.
    /// </summary>
    public async Task<ExportResponse> CreateExportAsync(
        ExportRequest request,
        CancellationToken cancellationToken = default)
    {
        var apiRequest = MapToApiRequest(request);
        var endpoint = request.ProfileType == ProfileType.Pair
            ? "/profiles-data/pair/export"
            : "/profiles-data/standard/export";

        var json = JsonSerializer.Serialize(apiRequest, _jsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await PostAsync($"{ApiBaseUrl}{endpoint}", content, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        return Deserialize<ExportResponse>(responseJson);
    }

    // ----------------------------------------------------------------
    // Download
    // ----------------------------------------------------------------

    /// <summary>
    /// Downloads the CSV file for a completed report.
    /// </summary>
    /// <param name="reportId">The <see cref="Report.Id"/> or <see cref="ExportResponse.Id"/>.</param>
    /// <returns>Raw CSV bytes (UTF-8, semicolon-delimited).</returns>
    public async Task<byte[]> DownloadReportAsync(int reportId, CancellationToken cancellationToken = default)
    {
        var url = $"{ApiBaseUrl}/report/{reportId}/download";
        using var request = BuildRequest(HttpMethod.Get, url);
        var response = await _apiHttpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>
    /// Downloads the CSV for a completed report and returns it as a UTF-8 string.
    /// </summary>
    public async Task<string> DownloadReportAsTextAsync(int reportId, CancellationToken cancellationToken = default)
    {
        var bytes = await DownloadReportAsync(reportId, cancellationToken);
        return Encoding.UTF8.GetString(bytes);
    }

    // ----------------------------------------------------------------
    // Convenience: wait + download in one call
    // ----------------------------------------------------------------

    /// <summary>
    /// Polls until the given report has finished generating, then downloads and returns the CSV bytes.
    /// </summary>
    /// <param name="reportId">Report ID returned by <see cref="CreateExportAsync"/>.</param>
    /// <param name="pollInterval">How often to check status. Default is 5 seconds.</param>
    /// <param name="timeout">Maximum time to wait. Default is 10 minutes.</param>
    public async Task<byte[]> WaitAndDownloadAsync(
        int reportId,
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(10));

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var list = await ListReportsAsync(cancellationToken: cancellationToken);
            var report = Array.Find(list.Content, r => r.Id == reportId);

            if (report != null && IsCompleted(report.ReportState))
                return await DownloadReportAsync(reportId, cancellationToken);

            if (report != null && IsFailed(report.ReportState))
                throw new EdcScraperException($"Report {reportId} failed with state '{report.ReportState}'.");

            await Task.Delay(interval, cancellationToken);
        }

        throw new TimeoutException($"Report {reportId} was not ready within {timeout ?? TimeSpan.FromMinutes(10)}.");
    }

    /// <summary>
    /// Polls until the given report has finished generating, downloads the CSV, parses it,
    /// and returns an in-memory collection of energy data records.
    /// </summary>
    /// <param name="reportId">Report ID returned by <see cref="CreateExportAsync"/>.</param>
    /// <param name="pollInterval">How often to check status. Default is 5 seconds.</param>
    /// <param name="timeout">Maximum time to wait. Default is 10 minutes.</param>
    public async Task<IReadOnlyList<EnergyDataRecord>> WaitAndParseAsync(
        int reportId,
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var csv = await DownloadReportAsTextAsync(
            reportId: await WaitForReportAsync(reportId, pollInterval, timeout, cancellationToken),
            cancellationToken);
        return ParseEnergyDataCsv(csv);
    }

    /// <summary>
    /// Parses a CSV string from an EDC energy data export into a collection of EnergyDataRecord objects.
    /// Expected CSV format: Datum;Cas od;Cas do;IN-{EAN}-{SUFFIX};OUT-{EAN}-{SUFFIX};...
    /// Date format: dd.MM.yyyy, Time format: HH:mm, Decimal separator: comma (Czech format)
    /// </summary>
    public static IReadOnlyList<EnergyDataRecord> ParseEnergyDataCsv(string csvContent)
    {
        var records = new List<EnergyDataRecord>();
        
        if (string.IsNullOrWhiteSpace(csvContent))
            return records.AsReadOnly();

        var lines = csvContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        if (lines.Length < 1)
            return records.AsReadOnly();

        // Parse header to identify EAN columns and their positions
        var header = lines[0];
        if (header.StartsWith("\ufeff"))  // Remove BOM if present
            header = header[1..];
        
        if (string.IsNullOrWhiteSpace(header))
            return records.AsReadOnly();

        var headerFields = header.Split(';');
        if (headerFields.Length < 3)
            throw new EdcScraperException("CSV header must have at least 3 columns (Datum, Cas od, Cas do)");

        // Build a map of EAN columns: column index -> (EAN base identifier without IN/OUT prefix)
        var eanColumnMap = new Dictionary<int, (string Ean, bool IsInput)>();
        for (int i = 3; i < headerFields.Length; i++)
        {
            var colName = headerFields[i].Trim();
            if (colName.StartsWith("IN-"))
            {
                var ean = colName[3..];  // "IN-{EAN}-{SUFFIX}" -> "{EAN}-{SUFFIX}"
                eanColumnMap[i] = (ean, true);
            }
            else if (colName.StartsWith("OUT-"))
            {
                var ean = colName[4..];  // "OUT-{EAN}-{SUFFIX}" -> "{EAN}-{SUFFIX}"
                eanColumnMap[i] = (ean, false);
            }
        }

        // Parse data rows
        var czechCulture = System.Globalization.CultureInfo.GetCultureInfo("cs-CZ");
        
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fields = line.Split(';');
            if (fields.Length < 3)
                continue;

            try
            {
                var date = DateTime.ParseExact(fields[0].Trim(), "dd.MM.yyyy", czechCulture);
                var timeFrom = TimeSpan.ParseExact(fields[1].Trim(), "hh\\:mm", czechCulture);
                var timeTo = TimeSpan.ParseExact(fields[2].Trim(), "hh\\:mm", czechCulture);

                // Parse EAN values
                var eanValues = new Dictionary<string, (decimal In, decimal Out)>();
                foreach (var (colIndex, (ean, isInput)) in eanColumnMap)
                {
                    if (colIndex >= fields.Length)
                        continue;

                    var valueStr = fields[colIndex].Trim();
                    var value = decimal.Parse(valueStr, czechCulture);

                    if (!eanValues.ContainsKey(ean))
                        eanValues[ean] = (0, 0);

                    var (inVal, outVal) = eanValues[ean];
                    eanValues[ean] = isInput ? (value, outVal) : (inVal, value);
                }

                records.Add(new EnergyDataRecord
                {
                    Date = date,
                    TimeFrom = timeFrom,
                    TimeTo = timeTo,
                    Eans = eanValues.AsReadOnly()
                });
            }
            catch (Exception ex)
            {
                throw new EdcScraperException($"Failed to parse CSV line: {line}", ex);
            }
        }

        return records.AsReadOnly();
    }

    /// <summary>
    /// Polls until the report with the given ID has finished generating and returns the report ID.
    /// </summary>
    private async Task<int> WaitForReportAsync(
        int reportId,
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(10));

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var list = await ListReportsAsync(cancellationToken: cancellationToken);
            var report = Array.Find(list.Content, r => r.Id == reportId);

            if (report != null && IsCompleted(report.ReportState))
                return reportId;

            if (report != null && IsFailed(report.ReportState))
                throw new EdcScraperException($"Report {reportId} failed with state '{report.ReportState}'.");

            await Task.Delay(interval, cancellationToken);
        }

        throw new TimeoutException($"Report {reportId} was not ready within {timeout ?? TimeSpan.FromMinutes(10)}.");
    }

    private static bool IsCompleted(string state) =>
        state.Equals("GENERATED", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailed(string state) =>
        state.Equals("ERROR", StringComparison.OrdinalIgnoreCase);

    // ----------------------------------------------------------------
    // HTTP plumbing
    // ----------------------------------------------------------------

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var request = BuildRequest(HttpMethod.Get, url);
        var response = await _apiHttpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct)
    {
        using var request = BuildRequest(HttpMethod.Post, url);
        request.Content = content;
        var response = await _apiHttpClient.SendAsync(request, ct);
        await EnsureSuccessAsync(response);
        return response;
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string url)
    {
        var token = _authService.AccessToken
            ?? throw new InvalidOperationException("Not authenticated. Call LoginAsync first.");
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("Authorization", $"Bearer {token}");
        req.Headers.Add("edc-contract-type", ContractType);
        req.Headers.Add("x-correlation-id", Guid.NewGuid().ToString());
        return req;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new EdcScraperException(
                $"API error {(int)response.StatusCode} ({response.ReasonPhrase}): {body}");
        }
    }

    // ----------------------------------------------------------------
    // Request mapping
    // ----------------------------------------------------------------

    private ApiExportRequest MapToApiRequest(ExportRequest req)
    {
        var profileType = req.ProfileType == ProfileType.Pair ? "PAIR" : "STANDARD";

        string? calculationType = req.ViewType switch
        {
            ViewType.Daily => "DAILY",
            ViewType.Monthly => "MONTHLY",
            _ => null
        };

        string? currentEnteredDateTime = req.ViewType == ViewType.Current
            ? (req.CurrentEnteredDateTime ?? DateTime.UtcNow).ToString("O")
            : null;

        var fileName = req.FileName
            ?? $"Export-dat-{DateTime.Now:yyyy-MM-dd-hh-mm}";

        // dateFrom / dateTo: convert to UTC midnight so the API receives consistent timestamps
        var dateFrom = ToUtcIso(req.DateFrom);
        var dateTo = ToUtcIso(req.DateTo);

        // When filtering by sharing group, eans must be omitted
        var hasGroup = req.SharingGroupId.HasValue;

        return new ApiExportRequest
        {
            Eans = hasGroup ? null : req.Eans,
            SseId = req.SharingGroupId,
            ProfileType = profileType,
            CalculationType = calculationType,
            CurrentEnteredDateTime = currentEnteredDateTime,
            InputData = req.IncludeMeasuredData,
            OutputData = req.IncludeEvaluationResults,
            DateFrom = dateFrom,
            DateTo = dateTo,
            FileName = fileName,
        };
    }

    /// <summary>
    /// Converts a local or UTC DateTime to a UTC ISO-8601 string (the format expected by the API).
    /// </summary>
    private static string ToUtcIso(DateTime dt) =>
        dt.Kind == DateTimeKind.Utc
            ? dt.ToString("O")
            : dt.ToUniversalTime().ToString("O");

    // ----------------------------------------------------------------
    // JSON helpers
    // ----------------------------------------------------------------

    private T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, _jsonOptions)
            ?? throw new EdcScraperException($"Failed to deserialize {typeof(T).Name}.");

    // ----------------------------------------------------------------
    // Disposal
    // ----------------------------------------------------------------

    /// <summary>
    /// Disposes of resources synchronously. Call LogoutAsync before disposing
    /// to ensure proper server-side logout, or use 'await using' pattern.
    /// </summary>
    public void Dispose()
    {
        try
        {
            // Note: We cannot await LogoutAsync in sync Dispose.
            // Users must call LogoutAsync explicitly or use 'await using' pattern.
        }
        finally
        {
            _apiHttpClient.Dispose();
            _authService.Dispose();
        }
    }

    /// <summary>
    /// Async disposal: performs server-side logout and disposes of resources.
    /// Ensure this is called by using 'await using' pattern.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await LogoutAsync();
        }
        finally
        {
            _apiHttpClient.Dispose();
            _authService.Dispose();
        }
    }
}
