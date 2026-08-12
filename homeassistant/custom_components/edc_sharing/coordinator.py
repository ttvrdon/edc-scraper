"""Runtime coordinator: schedule and execute the daily EDC import."""

from __future__ import annotations

import asyncio
import logging
from datetime import date, datetime, time, timedelta
from zoneinfo import ZoneInfo

import aiohttp

from homeassistant.core import HomeAssistant
from homeassistant.helpers.storage import Store

from .const import (
    CONF_EMAIL,
    CONF_MAX_BACKFILL_DAYS,
    CONF_NAMES,
    CONF_PASSWORD,
    CONF_RUN_TIME,
    CONF_SHARING_GROUP_ID,
    DEFAULT_MAX_BACKFILL_DAYS,
    DEFAULT_RUN_TIME,
    DOMAIN,
    EDC_MAX_BACKFILL_DAYS,
    EDC_TIMEZONE,
)
from .edc import EdcClient, EdcError
from .statistics import build_statistic_streams, import_streams

_LOGGER = logging.getLogger(__name__)
_STORE_VERSION = 1


class EdcRunner:
    """Owns configuration, the last-run marker, and the import routine."""

    def __init__(self, hass: HomeAssistant, config: dict) -> None:
        self.hass = hass
        self._config = config
        self._store: Store = Store(hass, _STORE_VERSION, f"{DOMAIN}_{self._entry_key()}")
        self._lock = asyncio.Lock()

    # --- configuration accessors ----------------------------------------

    def _entry_key(self) -> str:
        return str(self._config.get(CONF_SHARING_GROUP_ID, "default"))

    @property
    def email(self) -> str:
        return self._config[CONF_EMAIL]

    @property
    def password(self) -> str:
        return self._config[CONF_PASSWORD]

    @property
    def sharing_group_id(self) -> int:
        return int(self._config[CONF_SHARING_GROUP_ID])

    @property
    def names(self) -> dict[str, str]:
        raw = self._config.get(CONF_NAMES) or {}
        return {str(k): str(v) for k, v in raw.items()}

    @property
    def max_backfill_days(self) -> int:
        value = int(self._config.get(CONF_MAX_BACKFILL_DAYS, DEFAULT_MAX_BACKFILL_DAYS))
        return max(1, min(value, EDC_MAX_BACKFILL_DAYS))

    @property
    def run_time(self) -> time:
        raw = str(self._config.get(CONF_RUN_TIME, DEFAULT_RUN_TIME))
        hour, minute = (int(p) for p in raw.split(":", 1))
        return time(hour, minute)

    @property
    def timezone(self) -> ZoneInfo:
        return ZoneInfo(EDC_TIMEZONE)

    def update_config(self, config: dict) -> None:
        self._config = config

    # --- scheduling ------------------------------------------------------

    def next_run_utc(self, after: datetime | None = None) -> datetime:
        """Next occurrence of the configured local run time, as a UTC datetime."""
        tz = self.timezone
        now_local = (after or datetime.now(tz)).astimezone(tz)
        candidate = datetime.combine(now_local.date(), self.run_time, tzinfo=tz)
        if candidate <= now_local:
            candidate += timedelta(days=1)
        return candidate.astimezone(ZoneInfo("UTC"))

    # --- import routine --------------------------------------------------

    def _window(self, today_local: date) -> tuple[date, date] | None:
        """Compute the [date_from, date_to] range to fetch, or None if up to date."""
        date_to = today_local - timedelta(days=1)  # yesterday: last fully settled day
        earliest_allowed = today_local - timedelta(days=EDC_MAX_BACKFILL_DAYS)

        last = self._last_imported_date
        if last is None:
            date_from = date_to - timedelta(days=self.max_backfill_days - 1)
        else:
            date_from = last + timedelta(days=1)

        if date_from < earliest_allowed:
            date_from = earliest_allowed
        if date_from > date_to:
            return None
        return date_from, date_to

    async def async_run(self, _now: datetime | None = None) -> None:
        """Fetch missing days and import them into HA statistics."""
        if self._lock.locked():
            _LOGGER.debug("EDC import already running; skipping overlapping trigger")
            return
        async with self._lock:
            await self._load_marker()
            today_local = datetime.now(self.timezone).date()
            window = self._window(today_local)
            if window is None:
                _LOGGER.debug("EDC statistics already up to date")
                return
            date_from, date_to = window
            _LOGGER.info(
                "Fetching EDC data for sharing group %s from %s to %s",
                self.sharing_group_id,
                date_from,
                date_to,
            )
            try:
                records = await self._fetch(date_from, date_to)
            except EdcError as err:
                _LOGGER.error("EDC fetch failed: %s", err)
                raise

            streams = build_statistic_streams(records, self.names)
            written = await import_streams(self.hass, streams)
            _LOGGER.info(
                "Imported %d hourly points across %d statistics", written, len(streams)
            )
            await self._save_marker(date_to)

    async def _fetch(self, date_from: date, date_to: date):
        jar = aiohttp.CookieJar()
        async with aiohttp.ClientSession(cookie_jar=jar) as session:
            async with EdcClient(session) as client:
                await client.login(self.email, self.password)
                return await client.export_and_parse(
                    self.sharing_group_id, date_from, date_to
                )

    # --- last-run marker -------------------------------------------------

    _last_imported_date: date | None = None

    async def _load_marker(self) -> None:
        data = await self._store.async_load()
        value = (data or {}).get("last_imported_date")
        self._last_imported_date = date.fromisoformat(value) if value else None

    async def _save_marker(self, day: date) -> None:
        self._last_imported_date = day
        await self._store.async_save({"last_imported_date": day.isoformat()})
