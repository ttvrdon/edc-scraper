"""Data models and CSV parsing for EDC energy exports.

Pure-Python port of the C# EdcScraper models. No Home Assistant dependencies.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import date, time
from decimal import Decimal, InvalidOperation
from enum import Enum


class EdcError(Exception):
    """Raised when EDC data cannot be retrieved or parsed."""


class EanKind(str, Enum):
    """Role of an EAN (metering point) within a sharing group."""

    UNKNOWN = "unknown"
    PRODUCTION = "production"  # CSV suffix -D ("dodavka"): feeds the grid
    CONSUMPTION = "consumption"  # CSV suffix -O ("odber"): draws from the grid

    @classmethod
    def from_suffix(cls, suffix: str | None) -> "EanKind":
        match (suffix or "").upper():
            case "D":
                return cls.PRODUCTION
            case "O":
                return cls.CONSUMPTION
            case _:
                return cls.UNKNOWN


@dataclass(frozen=True, slots=True)
class EanEnergyData:
    """IN/OUT values and derived sharing figures for one EAN in one interval.

    Depending on ``kind``:

    * Production (-D): ``in_`` = total sent to grid, ``out`` = sold to provider
      (not shared). ``shared`` = ``in_ - out`` = energy offered to the group.
    * Consumption (-O): ``in_`` = total consumed from grid, ``out`` = still taken
      from grid. ``shared`` = ``in_ - out`` = energy received from sharing.

    Consumption values arrive negative in the CSV; use the ``*_kwh`` magnitude
    helpers when feeding positive kWh into Home Assistant statistics.
    """

    ean: str
    suffix: str | None
    kind: EanKind
    in_: Decimal
    out: Decimal

    @property
    def shared(self) -> Decimal:
        """Energy shared within the group (signed): ``in_ - out``."""
        return self.in_ - self.out

    # --- positive-magnitude helpers for HA statistics ---

    @property
    def total_kwh(self) -> Decimal:
        """Total energy magnitude for the interval (|in_|)."""
        return abs(self.in_)

    @property
    def shared_kwh(self) -> Decimal:
        """Shared energy magnitude for the interval (|in_ - out|)."""
        return abs(self.shared)

    @property
    def other_kwh(self) -> Decimal:
        """Non-shared magnitude (producer: sold; consumer: bought) (|out|)."""
        return abs(self.out)

    @classmethod
    def from_raw(cls, raw_key: str, in_: Decimal, out: Decimal) -> "EanEnergyData":
        """Build from a CSV column key such as ``859182400221784180-D``."""
        ean, suffix = _split_key(raw_key)
        return cls(ean=ean, suffix=suffix, kind=EanKind.from_suffix(suffix), in_=in_, out=out)


@dataclass(frozen=True, slots=True)
class EnergyDataRecord:
    """One row (a 15-minute interval) from an EDC export."""

    day: date
    time_from: time
    time_to: time
    eans: dict[str, EanEnergyData] = field(default_factory=dict)

    @property
    def production(self) -> list[EanEnergyData]:
        return [e for e in self.eans.values() if e.kind is EanKind.PRODUCTION]

    @property
    def consumption(self) -> list[EanEnergyData]:
        return [e for e in self.eans.values() if e.kind is EanKind.CONSUMPTION]


def _split_key(raw_key: str) -> tuple[str, str | None]:
    dash = raw_key.rfind("-")
    if dash <= 0 or dash == len(raw_key) - 1:
        return raw_key, None
    return raw_key[:dash], raw_key[dash + 1 :]


def _parse_decimal(value: str) -> Decimal:
    # Czech CSV uses a comma decimal separator.
    try:
        return Decimal(value.strip().replace("\xa0", "").replace(" ", "").replace(",", "."))
    except InvalidOperation as exc:  # pragma: no cover - re-raised with context by caller
        raise ValueError(f"Invalid decimal value: {value!r}") from exc


def parse_energy_data_csv(csv_content: str) -> list[EnergyDataRecord]:
    """Parse an EDC export CSV into :class:`EnergyDataRecord` objects.

    Expected header: ``Datum;Cas od;Cas do;IN-{EAN}-{SUFFIX};OUT-{EAN}-{SUFFIX};...``
    Date ``dd.MM.yyyy``, time ``HH:mm``, decimal comma (Czech locale), ``;`` delimited.
    """
    records: list[EnergyDataRecord] = []
    if not csv_content or not csv_content.strip():
        return records

    lines = csv_content.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    if not lines:
        return records

    header = lines[0]
    if header.startswith("\ufeff"):
        header = header[1:]
    if not header.strip():
        return records

    header_fields = header.split(";")
    if len(header_fields) < 3:
        raise EdcError("CSV header must have at least 3 columns (Datum, Cas od, Cas do)")

    # Map column index -> (raw EAN key incl. suffix, is_input)
    ean_columns: dict[int, tuple[str, bool]] = {}
    for i in range(3, len(header_fields)):
        col = header_fields[i].strip()
        if col.startswith("IN-"):
            ean_columns[i] = (col[3:], True)
        elif col.startswith("OUT-"):
            ean_columns[i] = (col[4:], False)

    for line in lines[1:]:
        if not line.strip():
            continue
        fields = line.split(";")
        if len(fields) < 3:
            continue
        try:
            day = _parse_date(fields[0].strip())
            time_from = _parse_time(fields[1].strip())
            time_to = _parse_time(fields[2].strip())

            raw: dict[str, list[Decimal]] = {}
            for idx, (key, is_input) in ean_columns.items():
                if idx >= len(fields):
                    continue
                value = _parse_decimal(fields[idx])
                pair = raw.setdefault(key, [Decimal(0), Decimal(0)])
                pair[0 if is_input else 1] = value

            eans: dict[str, EanEnergyData] = {}
            for raw_key, (in_, out) in raw.items():
                data = EanEnergyData.from_raw(raw_key, in_, out)
                eans[data.ean] = data

            records.append(
                EnergyDataRecord(day=day, time_from=time_from, time_to=time_to, eans=eans)
            )
        except (ValueError, InvalidOperation) as exc:
            raise EdcError(f"Failed to parse CSV line: {line}") from exc

    return records


def _parse_date(value: str) -> date:
    day, month, year = value.split(".")
    return date(int(year), int(month), int(day))


def _parse_time(value: str) -> time:
    parts = value.split(":")
    if len(parts) != 2:
        raise ValueError(f"Invalid time: {value!r}")
    return time(int(parts[0]), int(parts[1]))
