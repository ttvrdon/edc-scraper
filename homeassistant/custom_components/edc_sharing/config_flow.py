"""Config and options flow for EDC Energy Sharing."""

from __future__ import annotations

import logging
from typing import Any

import aiohttp
import voluptuous as vol

from homeassistant.config_entries import (
    ConfigEntry,
    ConfigFlow,
    ConfigFlowResult,
    OptionsFlow,
)
from homeassistant.const import CONF_EMAIL, CONF_PASSWORD
from homeassistant.core import callback

from .const import (
    CONF_MAX_BACKFILL_DAYS,
    CONF_NAMES,
    CONF_RUN_TIME,
    CONF_SHARING_GROUP_ID,
    DEFAULT_MAX_BACKFILL_DAYS,
    DEFAULT_RUN_TIME,
    DOMAIN,
    EDC_MAX_BACKFILL_DAYS,
    validate_run_time,
)
from .edc import EdcClient, EdcError

_LOGGER = logging.getLogger(__name__)


async def _validate_login(hass, email: str, password: str) -> None:
    """Attempt a login to validate credentials; raise EdcError on failure."""
    jar = aiohttp.CookieJar()
    async with aiohttp.ClientSession(cookie_jar=jar) as session:
        async with EdcClient(session) as client:
            await client.login(email, password)


def _parse_names(raw: str | dict | None) -> dict[str, str]:
    """Accept a dict or newline text of 'EAN: Name' / 'EAN=Name' into a mapping."""
    if isinstance(raw, dict):
        return {str(k): str(v) for k, v in raw.items()}
    names: dict[str, str] = {}
    for line in (raw or "").splitlines():
        line = line.strip()
        if not line:
            continue
        sep = ":" if ":" in line else ("=" if "=" in line else None)
        if sep is None:
            continue
        ean, name = line.split(sep, 1)
        ean, name = ean.strip(), name.strip()
        if ean and name:
            names[ean] = name
    return names


def _names_to_text(names: dict[str, str]) -> str:
    return "\n".join(f"{ean}: {name}" for ean, name in names.items())


class EdcConfigFlow(ConfigFlow, domain=DOMAIN):
    """Handle the UI and YAML-import config flow."""

    VERSION = 1

    async def async_step_user(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        errors: dict[str, str] = {}
        if user_input is not None:
            await self.async_set_unique_id(str(user_input[CONF_SHARING_GROUP_ID]))
            self._abort_if_unique_id_configured()
            try:
                await _validate_login(
                    self.hass, user_input[CONF_EMAIL], user_input[CONF_PASSWORD]
                )
            except EdcError:
                errors["base"] = "invalid_auth"
            except aiohttp.ClientError:
                errors["base"] = "cannot_connect"
            else:
                return self.async_create_entry(
                    title=f"EDC sharing group {user_input[CONF_SHARING_GROUP_ID]}",
                    data={
                        CONF_EMAIL: user_input[CONF_EMAIL],
                        CONF_PASSWORD: user_input[CONF_PASSWORD],
                        CONF_SHARING_GROUP_ID: user_input[CONF_SHARING_GROUP_ID],
                    },
                )

        schema = vol.Schema(
            {
                vol.Required(CONF_EMAIL): str,
                vol.Required(CONF_PASSWORD): str,
                vol.Required(CONF_SHARING_GROUP_ID): int,
            }
        )
        return self.async_show_form(step_id="user", data_schema=schema, errors=errors)

    async def async_step_import(self, user_input: dict[str, Any]) -> ConfigFlowResult:
        """Import from configuration.yaml."""
        group_id = str(user_input[CONF_SHARING_GROUP_ID])
        await self.async_set_unique_id(group_id)

        data = {
            CONF_EMAIL: user_input[CONF_EMAIL],
            CONF_PASSWORD: user_input[CONF_PASSWORD],
            CONF_SHARING_GROUP_ID: user_input[CONF_SHARING_GROUP_ID],
        }
        options = {
            CONF_RUN_TIME: validate_run_time(
                user_input.get(CONF_RUN_TIME, DEFAULT_RUN_TIME)
            ),
            CONF_MAX_BACKFILL_DAYS: min(
                int(user_input.get(CONF_MAX_BACKFILL_DAYS, DEFAULT_MAX_BACKFILL_DAYS)),
                EDC_MAX_BACKFILL_DAYS,
            ),
            CONF_NAMES: _parse_names(user_input.get(CONF_NAMES)),
        }

        # Update an existing entry in a single call (its update listener reloads once).
        for entry in self._async_current_entries():
            if entry.unique_id == group_id:
                self.hass.config_entries.async_update_entry(
                    entry, data=data, options=options
                )
                return self.async_abort(reason="already_configured")

        return self.async_create_entry(
            title=f"EDC sharing group {group_id}", data=data, options=options
        )

    @staticmethod
    @callback
    def async_get_options_flow(config_entry: ConfigEntry) -> OptionsFlow:
        return EdcOptionsFlow(config_entry)


class EdcOptionsFlow(OptionsFlow):
    """Options: run time, backfill window, and per-EAN friendly names."""

    def __init__(self, config_entry: ConfigEntry) -> None:
        self._entry = config_entry

    async def async_step_init(
        self, user_input: dict[str, Any] | None = None
    ) -> ConfigFlowResult:
        if user_input is not None:
            return self.async_create_entry(
                data={
                    CONF_RUN_TIME: user_input[CONF_RUN_TIME],
                    CONF_MAX_BACKFILL_DAYS: min(
                        int(user_input[CONF_MAX_BACKFILL_DAYS]), EDC_MAX_BACKFILL_DAYS
                    ),
                    CONF_NAMES: _parse_names(user_input.get(CONF_NAMES)),
                }
            )

        opts = self._entry.options
        schema = vol.Schema(
            {
                vol.Required(
                    CONF_RUN_TIME, default=opts.get(CONF_RUN_TIME, DEFAULT_RUN_TIME)
                ): vol.All(str, validate_run_time),
                vol.Required(
                    CONF_MAX_BACKFILL_DAYS,
                    default=opts.get(CONF_MAX_BACKFILL_DAYS, DEFAULT_MAX_BACKFILL_DAYS),
                ): vol.All(int, vol.Range(min=1, max=EDC_MAX_BACKFILL_DAYS)),
                vol.Optional(
                    CONF_NAMES,
                    default=_names_to_text(opts.get(CONF_NAMES, {})),
                ): str,
            }
        )
        return self.async_show_form(step_id="init", data_schema=schema)
