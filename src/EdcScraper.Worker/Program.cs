using EdcScraper.Worker.Configuration;
using EdcScraper.Worker.Data;
using EdcScraper.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<EdcOptions>(builder.Configuration.GetSection(WorkerOptions.EdcSection));
builder.Services.Configure<FetchOptions>(builder.Configuration.GetSection(WorkerOptions.FetchSection));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(WorkerOptions.DatabaseSection));

builder.Services.AddSingleton<ScraperDatabase>();
builder.Services.AddHostedService<ScraperJob>();

using var host = builder.Build();
await host.RunAsync();

return Environment.ExitCode;
