"""The EDC Energy Sharing integration."""

from __future__ import annotations

import logging

import voluptuous as vol

from homeassistant.config_entries import SOURCE_IMPORT, ConfigEntry
from homeassistant.const import CONF_EMAIL, CONF_PASSWORD
from homeassistant.core import HomeAssistant, ServiceCall
from homeassistant.helpers import config_validation as cv
from homeassistant.helpers.event import async_call_later, async_track_point_in_time
from homeassistant.helpers.typing import ConfigType

from .const import (
    CONF_MAX_BACKFILL_DAYS,
    CONF_NAMES,
    CONF_RUN_TIME,
    CONF_SHARING_GROUP_ID,
    DEFAULT_MAX_BACKFILL_DAYS,
    DEFAULT_RUN_TIME,
    DOMAIN,
    validate_run_time,
)
from .coordinator import EdcRunner

_LOGGER = logging.getLogger(__name__)

_SERVICE_IMPORT_NOW = "import_now"

CONFIG_SCHEMA = vol.Schema(
    {
        DOMAIN: vol.Schema(
            {
                vol.Required(CONF_EMAIL): cv.string,
                vol.Required(CONF_PASSWORD): cv.string,
                vol.Required(CONF_SHARING_GROUP_ID): cv.positive_int,
                vol.Optional(CONF_RUN_TIME, default=DEFAULT_RUN_TIME): vol.All(
                    cv.string, validate_run_time
                ),
                vol.Optional(
                    CONF_MAX_BACKFILL_DAYS, default=DEFAULT_MAX_BACKFILL_DAYS
                ): cv.positive_int,
                vol.Optional(CONF_NAMES, default={}): {cv.string: cv.string},
            }
        )
    },
    extra=vol.ALLOW_EXTRA,
)


async def async_setup(hass: HomeAssistant, config: ConfigType) -> bool:
    """Import YAML configuration into a config entry (YAML + UI both supported)."""
    if DOMAIN not in config:
        return True

    hass.async_create_task(
        hass.config_entries.flow.async_init(
            DOMAIN, context={"source": SOURCE_IMPORT}, data=dict(config[DOMAIN])
        )
    )
    return True


async def async_setup_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    """Set up a config entry: build the runner, schedule the daily run, add service."""
    merged = {**entry.data, **entry.options}
    runner = EdcRunner(hass, merged)

    hass.data.setdefault(DOMAIN, {})
    hass.data[DOMAIN][entry.entry_id] = {"runner": runner}

    # Register all cancellations with the entry so unload/reload is clean.
    entry.async_on_unload(_schedule(hass, runner))
    entry.async_on_unload(entry.add_update_listener(_async_options_updated))
    _register_service(hass)

    # Kick off a catch-up run shortly after startup so restarts don't miss days.
    async def _startup(_now) -> None:
        try:
            await runner.async_run()
        except Exception:  # noqa: BLE001 - logged inside; never break setup
            _LOGGER.exception("Initial EDC catch-up run failed")

    entry.async_on_unload(async_call_later(hass, 30, _startup))
    return True


async def async_unload_entry(hass: HomeAssistant, entry: ConfigEntry) -> bool:
    """Tear down a config entry."""
    hass.data.get(DOMAIN, {}).pop(entry.entry_id, None)
    if not hass.data.get(DOMAIN):
        hass.services.async_remove(DOMAIN, _SERVICE_IMPORT_NOW)
    return True


async def _async_options_updated(hass: HomeAssistant, entry: ConfigEntry) -> None:
    """Reload the entry when options change."""
    await hass.config_entries.async_reload(entry.entry_id)


def _schedule(hass: HomeAssistant, runner: EdcRunner):
    """Schedule the next run at the configured local time; reschedule after each run.

    Returns a cancel callback. The ``active`` flag guarantees that a run finishing
    after unload does not resurrect the schedule.
    """
    state: dict[str, object] = {"active": True, "unsub": None}

    def _plan(_now=None):
        next_utc = runner.next_run_utc()
        _LOGGER.debug("Next EDC import scheduled for %s (UTC)", next_utc.isoformat())

        async def _fire(now) -> None:
            try:
                await runner.async_run(now)
            except Exception:  # noqa: BLE001 - logged inside; keep the schedule alive
                _LOGGER.exception("Scheduled EDC import failed")
            finally:
                if state["active"]:
                    state["unsub"] = _plan()

        return async_track_point_in_time(hass, _fire, next_utc)

    state["unsub"] = _plan()

    def _cancel() -> None:
        state["active"] = False
        unsub = state.get("unsub")
        if callable(unsub):
            unsub()

    return _cancel


def _register_service(hass: HomeAssistant) -> None:
    if hass.services.has_service(DOMAIN, _SERVICE_IMPORT_NOW):
        return

    async def _handle_import_now(_call: ServiceCall) -> None:
        for data in hass.data.get(DOMAIN, {}).values():
            runner: EdcRunner = data["runner"]
            await runner.async_run()

    hass.services.async_register(DOMAIN, _SERVICE_IMPORT_NOW, _handle_import_now)
