"""Constants for the EDC energy-sharing integration."""

from __future__ import annotations

import re
from typing import Final

DOMAIN: Final = "edc_sharing"

# External statistics source prefix -> statistic_id looks like "edc:<name>_<suffix>".
STATISTIC_SOURCE: Final = "edc"

# Config / options keys
CONF_EMAIL: Final = "email"
CONF_PASSWORD: Final = "password"  # noqa: S105 - config key name, not a secret value
CONF_SHARING_GROUP_ID: Final = "sharing_group_id"
CONF_NAMES: Final = "names"  # mapping EAN -> friendly name
CONF_RUN_TIME: Final = "run_time"  # "HH:MM" local (Europe/Prague)
CONF_MAX_BACKFILL_DAYS: Final = "max_backfill_days"

# Defaults
DEFAULT_RUN_TIME: Final = "11:30"
DEFAULT_MAX_BACKFILL_DAYS: Final = 30
EDC_MAX_BACKFILL_DAYS: Final = 30  # hard limit enforced by the EDC portal

# Timezone the EDC portal reports quarter-hour intervals in.
EDC_TIMEZONE: Final = "Europe/Prague"

# Per-kind statistic suffixes (see README data-point table).
PRODUCER_SUFFIXES: Final = ("produced", "shared", "sold")
CONSUMER_SUFFIXES: Final = ("consumed", "from_shared", "from_grid")

_RUN_TIME_RE: Final = re.compile(r"^([01]?\d|2[0-3]):[0-5]\d$")


def validate_run_time(value: str) -> str:
    """Validate a ``HH:MM`` 24-hour string (raises ValueError for voluptuous)."""
    text = str(value).strip()
    if not _RUN_TIME_RE.match(text):
        raise ValueError(f"Invalid run time {value!r}; expected HH:MM (24h)")
    return text
