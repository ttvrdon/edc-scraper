"""Map parsed EDC records into Home Assistant external statistics and import them."""

from __future__ import annotations

import logging
from collections import defaultdict
from dataclasses import dataclass, field
from datetime import datetime, timezone
from decimal import Decimal
from zoneinfo import ZoneInfo

from homeassistant.components.recorder import get_instance
from homeassistant.components.recorder.models import StatisticData, StatisticMetaData
from homeassistant.components.recorder.statistics import (
    async_add_external_statistics,
    get_last_statistics,
)
from homeassistant.const import UnitOfEnergy
from homeassistant.core import HomeAssistant
from homeassistant.util import slugify

from .const import EDC_TIMEZONE, STATISTIC_SOURCE
from .edc import EanKind, EnergyDataRecord

_LOGGER = logging.getLogger(__name__)

# Cache the timezone at import time (ZoneInfo() does first-use file I/O).
_EDC_TZ = ZoneInfo(EDC_TIMEZONE)

# Optional metadata fields required by newer HA recorder versions; imported defensively
# so the integration keeps working on 2024.4+ where they don't exist yet.
_EXTRA_METADATA: dict[str, object] = {}
try:  # HA >= 2025.11
    from homeassistant.components.recorder.models import StatisticMeanType

    _EXTRA_METADATA["mean_type"] = StatisticMeanType.NONE
except ImportError:  # pragma: no cover - depends on HA version
    pass
try:  # HA >= 2025.11
    from homeassistant.util.unit_conversion import EnergyConverter

    _EXTRA_METADATA["unit_class"] = EnergyConverter.UNIT_CLASS
except (ImportError, AttributeError):  # pragma: no cover - depends on HA version
    pass

# suffix -> (human label, extractor returning a positive-kWh Decimal for the interval)
_PRODUCER_METRICS = {
    "produced": ("sent to grid", lambda e: e.in_),
    "shared": ("shared to community", lambda e: e.shared),
    "sold": ("sold to supplier", lambda e: e.out),
}
_CONSUMER_METRICS = {
    "consumed": ("consumed from grid", lambda e: e.total_kwh),
    "from_shared": ("received from sharing", lambda e: e.shared_kwh),
    "from_grid": ("bought from supplier", lambda e: e.other_kwh),
}


@dataclass(slots=True)
class StatStream:
    """One statistic stream with hourly kWh deltas (UTC, hour-aligned)."""

    statistic_id: str
    display_name: str
    buckets: dict[datetime, float] = field(default_factory=lambda: defaultdict(float))


def build_statistic_streams(
    records: list[EnergyDataRecord],
    names: dict[str, str],
) -> list[StatStream]:
    """Aggregate quarter-hour records into hourly statistic streams.

    ``names`` maps a bare EAN to a friendly name; unknown EANs fall back to the EAN.
    The statistic id is derived from the (slugified) friendly name so it stays valid
    for non-ASCII names; renaming an EAN therefore starts a new statistic.
    """
    tz = _EDC_TZ
    streams: dict[str, StatStream] = {}

    for record in records:
        hour_utc = _hour_bucket_utc(record, tz)
        for ean, data in record.eans.items():
            if data.kind is EanKind.PRODUCTION:
                metrics = _PRODUCER_METRICS
            elif data.kind is EanKind.CONSUMPTION:
                metrics = _CONSUMER_METRICS
            else:
                continue

            friendly = names.get(ean, ean)
            slug = slugify(friendly) or slugify(ean) or "unknown"
            for suffix, (label, extract) in metrics.items():
                statistic_id = f"{STATISTIC_SOURCE}:{slug}_{suffix}"
                stream = streams.get(statistic_id)
                if stream is None:
                    stream = StatStream(
                        statistic_id=statistic_id,
                        display_name=f"{friendly} {label}",
                    )
                    streams[statistic_id] = stream
                value = _to_positive_float(extract(data))
                stream.buckets[hour_utc] += value

    return list(streams.values())


async def import_streams(hass: HomeAssistant, streams: list[StatStream]) -> int:
    """Import all streams, resuming each running sum from the last stored value.

    Returns the number of hourly points written.
    """
    written = 0
    for stream in streams:
        written += await _import_stream(hass, stream)
    return written


async def _import_stream(hass: HomeAssistant, stream: StatStream) -> int:
    last_sum, last_start = await _get_last(hass, stream.statistic_id)

    # Only import buckets strictly after the last stored hour.
    new_hours = sorted(h for h in stream.buckets if last_start is None or h > last_start)
    if not new_hours:
        return 0

    running = last_sum
    points: list[StatisticData] = []
    for hour in new_hours:
        running += stream.buckets[hour]
        points.append(StatisticData(start=hour, sum=running))

    metadata = StatisticMetaData(
        has_mean=False,
        has_sum=True,
        name=stream.display_name,
        source=STATISTIC_SOURCE,
        statistic_id=stream.statistic_id,
        unit_of_measurement=UnitOfEnergy.KILO_WATT_HOUR,
        **_EXTRA_METADATA,
    )
    async_add_external_statistics(hass, metadata, points)
    _LOGGER.debug("Imported %d points for %s", len(points), stream.statistic_id)
    return len(points)


async def _get_last(
    hass: HomeAssistant, statistic_id: str
) -> tuple[float, datetime | None]:
    """Return (last cumulative sum, last hour start UTC) or (0.0, None)."""
    last = await get_instance(hass).async_add_executor_job(
        get_last_statistics, hass, 1, statistic_id, True, {"sum"}
    )
    rows = last.get(statistic_id)
    if not rows:
        return 0.0, None
    row = rows[0]
    last_sum = float(row.get("sum") or 0.0)
    last_start = _as_utc_datetime(row.get("start"))
    return last_sum, last_start


def _hour_bucket_utc(record: EnergyDataRecord, tz: ZoneInfo) -> datetime:
    local = datetime.combine(record.day, record.time_from).replace(tzinfo=tz)
    utc = local.astimezone(timezone.utc)
    return utc.replace(minute=0, second=0, microsecond=0)


def _as_utc_datetime(value: object) -> datetime | None:
    if value is None:
        return None
    if isinstance(value, datetime):
        return value.astimezone(timezone.utc)
    # Recent HA returns unix timestamps (float seconds).
    return datetime.fromtimestamp(float(value), tz=timezone.utc)


def _to_positive_float(value: Decimal) -> float:
    return float(abs(value))
