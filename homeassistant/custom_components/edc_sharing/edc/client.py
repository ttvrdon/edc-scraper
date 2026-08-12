"""Async EDC portal client: schedule exports, poll, download, and parse CSV.

Pure-Python port of the C# EdcScraperClient. No Home Assistant dependencies.
"""

from __future__ import annotations

import asyncio
import contextlib
from datetime import date, datetime, timezone

import aiohttp

from .auth import BROWSER_HEADERS, AuthService
from .models import EdcError, EnergyDataRecord, parse_energy_data_csv

_API_BASE = "https://api.portal.edc-cr.cz/api/v0"
_CONTRACT_TYPE = "STANDARD"


class EdcClient:
    """High-level client for one login session against the EDC portal."""

    def __init__(self, session: aiohttp.ClientSession) -> None:
        self._session = session
        self._auth = AuthService(session)

    async def login(self, email: str, password: str) -> None:
        await self._auth.login(email, password)

    async def logout(self) -> None:
        await self._auth.logout()

    async def __aenter__(self) -> "EdcClient":
        return self

    async def __aexit__(self, *exc: object) -> None:
        with contextlib.suppress(Exception):
            await self.logout()

    # --- reports ---------------------------------------------------------

    async def list_reports(self, page: int = 0, per_page: int = 25) -> list[dict]:
        url = (
            f"{_API_BASE}/report?page={page}&perPage={per_page}"
            "&sortBy=requested&sortOrder=desc"
        )
        data = await self._get_json(url)
        return data.get("content", [])

    async def create_export_for_group(
        self,
        sharing_group_id: int,
        date_from: date,
        date_to: date,
        file_name: str | None = None,
    ) -> int:
        """Schedule a daily (quarter-hour) CSV export for a sharing group.

        Returns the report id.
        """
        payload = {
            "eans": None,
            "sseId": sharing_group_id,
            "profileType": "STANDARD",
            "calculationType": "DAILY",
            "currentEnteredDateTime": None,
            "inputData": True,
            "outputData": True,
            "dateFrom": _to_utc_iso(date_from),
            "dateTo": _to_utc_iso(date_to),
            "fileName": file_name or f"HA-Export-{datetime.now():%Y-%m-%d-%H-%M}",
        }
        resp = await self._post_json(f"{_API_BASE}/profiles-data/standard/export", payload)
        return int(resp["id"])

    async def download_report_text(self, report_id: int) -> str:
        url = f"{_API_BASE}/report/{report_id}/download"
        headers = await self._auth_headers()
        async with self._session.get(url, headers=headers) as resp:
            await self._ensure_ok(resp)
            return await resp.text(encoding="utf-8")

    async def wait_for_report(
        self,
        report_id: int,
        poll_interval: float = 5.0,
        timeout: float = 600.0,
    ) -> None:
        """Poll until the report is GENERATED (or raise on ERROR/timeout)."""
        deadline = asyncio.get_running_loop().time() + timeout
        while asyncio.get_running_loop().time() < deadline:
            reports = await self.list_reports()
            report = next((r for r in reports if r.get("id") == report_id), None)
            if report is not None:
                state = str(report.get("reportState", "")).upper()
                if state == "GENERATED":
                    return
                if state == "ERROR":
                    raise EdcError(f"Report {report_id} failed with state 'ERROR'.")
            await asyncio.sleep(poll_interval)
        raise EdcError(f"Report {report_id} was not ready within {timeout:.0f}s.")

    async def export_and_parse(
        self,
        sharing_group_id: int,
        date_from: date,
        date_to: date,
        poll_interval: float = 5.0,
        timeout: float = 600.0,
    ) -> list[EnergyDataRecord]:
        """Schedule, wait, download, and parse in one call."""
        report_id = await self.create_export_for_group(sharing_group_id, date_from, date_to)
        await self.wait_for_report(report_id, poll_interval, timeout)
        csv = await self.download_report_text(report_id)
        return parse_energy_data_csv(csv)

    # --- HTTP plumbing ---------------------------------------------------

    async def _auth_headers(self) -> dict[str, str]:
        token = await self._auth.valid_access_token()
        return {
            **BROWSER_HEADERS,
            "Authorization": f"Bearer {token}",
            "edc-contract-type": _CONTRACT_TYPE,
            "x-correlation-id": _new_correlation_id(),
        }

    async def _get_json(self, url: str) -> dict:
        headers = await self._auth_headers()
        async with self._session.get(url, headers=headers) as resp:
            await self._ensure_ok(resp)
            return await resp.json()

    async def _post_json(self, url: str, payload: dict) -> dict:
        headers = {**await self._auth_headers(), "Content-Type": "application/json"}
        async with self._session.post(url, json=payload, headers=headers) as resp:
            await self._ensure_ok(resp)
            return await resp.json()

    @staticmethod
    async def _ensure_ok(resp: aiohttp.ClientResponse) -> None:
        if resp.status >= 400:
            body = await resp.text()
            raise EdcError(f"API error {resp.status} ({resp.reason}): {body}")


def _to_utc_iso(day: date) -> str:
    dt = datetime(day.year, day.month, day.day, tzinfo=timezone.utc)
    return dt.isoformat()


def _new_correlation_id() -> str:
    import uuid

    return str(uuid.uuid4())
