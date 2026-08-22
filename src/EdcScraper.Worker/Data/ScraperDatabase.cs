using Dapper;
using EdcScraper.Worker.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace EdcScraper.Worker.Data;

/// <summary>
/// SQLite persistence layer (Dapper + Microsoft.Data.Sqlite).
/// Ensures the schema exists and upserts interval rows and daily summaries.
/// </summary>
public sealed class ScraperDatabase
{
    private readonly string _connectionString;

    public ScraperDatabase(IOptions<DatabaseOptions> options)
    {
        var path = options.Value.Path;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Database:Path is not configured.");

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>Creates the tables and indexes if they do not already exist.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();

        const string schema = """
            CREATE TABLE IF NOT EXISTS EnergyIntervals (
                SharingGroupId  INTEGER NOT NULL,
                Date            TEXT    NOT NULL,
                TimeFrom        TEXT    NOT NULL,
                TimeTo          TEXT    NOT NULL,
                Ean             TEXT    NOT NULL,
                Suffix          TEXT    NULL,
                Kind            TEXT    NOT NULL,
                "In"            REAL    NOT NULL,
                "Out"           REAL    NOT NULL,
                Shared          REAL    NOT NULL,
                FetchedAt       TEXT    NOT NULL,
                PRIMARY KEY (Date, TimeFrom, Ean)
            );

            CREATE INDEX IF NOT EXISTS IX_EnergyIntervals_Group_Date
                ON EnergyIntervals (SharingGroupId, Date);

            CREATE TABLE IF NOT EXISTS DailySummaries (
                SharingGroupId          INTEGER NOT NULL,
                Date                    TEXT    NOT NULL,
                TotalProducedToGrid     REAL    NOT NULL,
                TotalSoldToProvider     REAL    NOT NULL,
                TotalSharedProduction   REAL    NOT NULL,
                TotalConsumedFromGrid   REAL    NOT NULL,
                TotalTakenFromGrid      REAL    NOT NULL,
                TotalSharedConsumption  REAL    NOT NULL,
                IntervalCount           INTEGER NOT NULL,
                FetchedAt               TEXT    NOT NULL,
                PRIMARY KEY (Date, SharingGroupId)
            );

            CREATE TABLE IF NOT EXISTS FetchState (
                SharingGroupId  INTEGER NOT NULL,
                LastFetchedDate TEXT    NOT NULL,
                LastFetchedAt   TEXT    NOT NULL,
                PRIMARY KEY (SharingGroupId)
            );
            """;

        await connection.ExecuteAsync(new CommandDefinition(schema, cancellationToken: cancellationToken));
    }

    /// <summary>Upserts all interval rows for a day inside a single transaction.</summary>
    public async Task UpsertIntervalsAsync(
        IReadOnlyCollection<EnergyIntervalRow> rows,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
            return;

        await using var connection = OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        const string sql = """
            INSERT INTO EnergyIntervals
                (SharingGroupId, Date, TimeFrom, TimeTo, Ean, Suffix, Kind, "In", "Out", Shared, FetchedAt)
            VALUES
                (@SharingGroupId, @Date, @TimeFrom, @TimeTo, @Ean, @Suffix, @Kind, @In, @Out, @Shared, @FetchedAt)
            ON CONFLICT (Date, TimeFrom, Ean) DO UPDATE SET
                SharingGroupId = excluded.SharingGroupId,
                TimeTo         = excluded.TimeTo,
                Suffix         = excluded.Suffix,
                Kind           = excluded.Kind,
                "In"           = excluded."In",
                "Out"          = excluded."Out",
                Shared         = excluded.Shared,
                FetchedAt      = excluded.FetchedAt;
            """;

        var parameters = rows.Select(r => new
        {
            r.SharingGroupId,
            Date = r.Date.ToString("yyyy-MM-dd"),
            TimeFrom = r.TimeFrom.ToString(@"hh\:mm"),
            TimeTo = r.TimeTo.ToString(@"hh\:mm"),
            r.Ean,
            r.Suffix,
            r.Kind,
            In = (double)r.In,
            Out = (double)r.Out,
            Shared = (double)r.Shared,
            FetchedAt = r.FetchedAt.ToString("O"),
        });

        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Upserts a single daily summary row.</summary>
    public async Task UpsertDailySummaryAsync(
        DailySummaryRow row,
        CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();

        const string sql = """
            INSERT INTO DailySummaries
                (SharingGroupId, Date, TotalProducedToGrid, TotalSoldToProvider, TotalSharedProduction,
                 TotalConsumedFromGrid, TotalTakenFromGrid, TotalSharedConsumption, IntervalCount, FetchedAt)
            VALUES
                (@SharingGroupId, @Date, @TotalProducedToGrid, @TotalSoldToProvider, @TotalSharedProduction,
                 @TotalConsumedFromGrid, @TotalTakenFromGrid, @TotalSharedConsumption, @IntervalCount, @FetchedAt)
            ON CONFLICT (Date, SharingGroupId) DO UPDATE SET
                TotalProducedToGrid    = excluded.TotalProducedToGrid,
                TotalSoldToProvider    = excluded.TotalSoldToProvider,
                TotalSharedProduction  = excluded.TotalSharedProduction,
                TotalConsumedFromGrid  = excluded.TotalConsumedFromGrid,
                TotalTakenFromGrid     = excluded.TotalTakenFromGrid,
                TotalSharedConsumption = excluded.TotalSharedConsumption,
                IntervalCount          = excluded.IntervalCount,
                FetchedAt              = excluded.FetchedAt;
            """;

        var parameters = new
        {
            row.SharingGroupId,
            Date = row.Date.ToString("yyyy-MM-dd"),
            TotalProducedToGrid = (double)row.TotalProducedToGrid,
            TotalSoldToProvider = (double)row.TotalSoldToProvider,
            TotalSharedProduction = (double)row.TotalSharedProduction,
            TotalConsumedFromGrid = (double)row.TotalConsumedFromGrid,
            TotalTakenFromGrid = (double)row.TotalTakenFromGrid,
            TotalSharedConsumption = (double)row.TotalSharedConsumption,
            row.IntervalCount,
            FetchedAt = row.FetchedAt.ToString("O"),
        };

        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    /// <summary>Returns the last successfully fetched date for a sharing group, or null if none.</summary>
    public async Task<DateTime?> GetLastFetchedDateAsync(
        int sharingGroupId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();

        const string sql = "SELECT LastFetchedDate FROM FetchState WHERE SharingGroupId = @SharingGroupId;";
        var value = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(sql, new { SharingGroupId = sharingGroupId }, cancellationToken: cancellationToken));

        return string.IsNullOrEmpty(value)
            ? null
            : DateTime.ParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Records the last successfully fetched date and timestamp for a sharing group.</summary>
    public async Task UpsertFetchStateAsync(
        FetchStateRow row,
        CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();

        const string sql = """
            INSERT INTO FetchState (SharingGroupId, LastFetchedDate, LastFetchedAt)
            VALUES (@SharingGroupId, @LastFetchedDate, @LastFetchedAt)
            ON CONFLICT (SharingGroupId) DO UPDATE SET
                LastFetchedDate = excluded.LastFetchedDate,
                LastFetchedAt   = excluded.LastFetchedAt;
            """;

        var parameters = new
        {
            row.SharingGroupId,
            LastFetchedDate = row.LastFetchedDate.ToString("yyyy-MM-dd"),
            LastFetchedAt = row.LastFetchedAt.ToString("O"),
        };

        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }
}