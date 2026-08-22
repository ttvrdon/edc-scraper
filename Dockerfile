# syntax=docker/dockerfile:1

# ---- build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy only what the worker needs to restore/build
COPY Directory.Packages.props ./
COPY src/EdcScraper/EdcScraper.csproj src/EdcScraper/
COPY src/EdcScraper.Worker/EdcScraper.Worker.csproj src/EdcScraper.Worker/
RUN dotnet restore src/EdcScraper.Worker/EdcScraper.Worker.csproj

COPY src/EdcScraper/ src/EdcScraper/
COPY src/EdcScraper.Worker/ src/EdcScraper.Worker/
RUN dotnet publish src/EdcScraper.Worker/EdcScraper.Worker.csproj \
    -c Release -o /app --no-restore

# ---- runtime stage ----
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

# Default location for the SQLite database volume.
# Mount a host directory or named volume here, e.g. -v edc-data:/data
ENV Database__Path=/data/edc.db
VOLUME /data

COPY --from=build /app ./

# The worker runs the scrape once and exits.
ENTRYPOINT ["dotnet", "EdcScraper.Worker.dll"]
