# EDC Energy Sharing — Home Assistant integration

Imports your **EDC CR** (portal.edc-cr.cz) energy-sharing data into Home Assistant's
long-term **Statistics** engine. Runs daily, backfills any missing days (up to the
EDC 30-day limit), and exposes per-metering-point energy figures you can use in the
Energy Dashboard and in cards.

This is the Python/HACS port of the C# `EdcScraper` library in the same repository.

## How it works

1. Every day at the configured local time (default **11:30 Europe/Prague**) the
   integration logs in to the EDC portal (Keycloak PKCE), schedules a **daily
   quarter-hour export** for your sharing group, waits for it, and downloads the CSV.
2. It figures out which days are missing by reading the last hour already present in
   its own statistics marker, then requests only `last_day+1 … yesterday`
   (clamped to 30 days).
3. The 96 quarter-hour intervals per day are aggregated into **hourly** buckets
   (Home Assistant long-term statistics are hourly) and written as external
   statistics with a continuous running sum.

Trigger a run manually any time with the **`edc_sharing.import_now`** service.

## Data points

Each metering point in the export is classified by its CSV suffix (`-D` = producer,
`-O` = consumer). You can give each EAN a friendly name; otherwise the raw EAN is used.
All statistics are `kWh`, `sum` type, id `edc:<name>_<suffix>`.

### Producer (`-D`)

| Statistic | Meaning | From quarter-hour data |
|---|---|---|
| `edc:<name>_produced` | total sent to grid | Σ `IN` |
| `edc:<name>_shared` | shared to the community | Σ (`IN − OUT`) |
| `edc:<name>_sold` | sold to the supplier (not shared) | Σ `OUT` |

`produced = shared + sold`.

### Consumer (`-O`)

| Statistic | Meaning | From quarter-hour data |
|---|---|---|
| `edc:<name>_consumed` | total consumed from grid | Σ \|`IN`\| |
| `edc:<name>_from_shared` | covered by sharing | Σ \|`IN − OUT`\| |
| `edc:<name>_from_grid` | still bought from the supplier | Σ \|`OUT`\| |

`consumed = from_shared + from_grid`.

### Energy Dashboard mapping

- **Solar production** → `edc:<producer>_produced`
- **Return to grid** → `edc:<producer>_sold`
- **Grid consumption** → `edc:<consumer>_from_grid`

The `_shared` / `_from_shared` streams are your community-sharing KPIs (show them in
statistics cards).

## Installation (HACS)

1. Add this repository as a custom repository (category: *Integration*).
2. Install **EDC Energy Sharing** and restart Home Assistant.
3. Add it via **Settings → Devices & Services → Add Integration → EDC Energy Sharing**,
   or configure it in YAML (below).

## Configuration

### UI

Enter your portal **email**, **password**, and **sharing group ID**. Then use the
integration's **Options** to set the daily run time, the maximum backfill window, and
the per-EAN friendly names (one `EAN: Name` per line).

### YAML

```yaml
# configuration.yaml
edc_sharing:
  email: you@example.com
  password: !secret edc_password
  sharing_group_id: 36557
  run_time: "11:30"          # local Europe/Prague time
  max_backfill_days: 30      # capped at 30 by EDC
  names:
    "859182400221784180": Roof
    "859182400204460056": House
```

YAML is imported into a config entry, so both methods manage the same integration.

## Notes

- Statistics are stored **hourly**; the underlying EDC data is quarter-hour and is
  summed into each hour (DST transitions handled via `Europe/Prague`).
- Consumption values are negative in the CSV; they are stored as positive kWh
  magnitudes so they behave correctly in the Energy Dashboard.
