"""Keycloak PKCE authentication for the EDC portal (aiohttp port of AuthService)."""

from __future__ import annotations

import base64
import hashlib
import re
import secrets
from datetime import datetime, timedelta, timezone
from urllib.parse import parse_qs, urljoin, urlparse

import aiohttp

from .models import EdcError

_SSO_BASE = "https://sso.portal.edc-cr.cz/auth/realms/edc/protocol/openid-connect"
_CLIENT_ID = "a63c22a3-6e1d-4eac-b383-d06373da046a"
_REDIRECT_URI = "https://portal.edc-cr.cz/"

_FORM_ACTION_RE = re.compile(r'(https?://[^\s"]+authenticate\?session_code=[^\s"]+)')
_FORM_ACTION_REL_RE = re.compile(
    r'"(/auth/realms/[^\s"]+authenticate\?session_code=[^\s"]+)"', re.IGNORECASE
)

BROWSER_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"
    ),
    "Accept": "application/json, text/plain, */*",
    "Accept-Language": "en-US,en;q=0.9,cs;q=0.8",
    # Intentionally omit 'br' to avoid a hard brotli dependency; aiohttp handles gzip/deflate.
    "Accept-Encoding": "gzip, deflate",
    "DNT": "1",
    "Sec-Fetch-Dest": "empty",
    "Sec-Fetch-Mode": "cors",
    "Sec-Fetch-Site": "same-site",
    "Sec-CH-UA": '"Not A(Brand";v="99", "Google Chrome";v="126", "Chromium";v="126"',
    "Sec-CH-UA-Mobile": "?0",
    "Sec-CH-UA-Platform": '"Windows"',
}


def _b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


class AuthService:
    """Owns the token lifecycle and PKCE login flow over a shared aiohttp session."""

    def __init__(self, session: aiohttp.ClientSession) -> None:
        self._session = session
        self._access_token: str | None = None
        self._refresh_token: str | None = None
        self._id_token: str | None = None
        self._access_expiry = datetime.min.replace(tzinfo=timezone.utc)
        self._refresh_expiry = datetime.min.replace(tzinfo=timezone.utc)

    @property
    def is_logged_in(self) -> bool:
        return self._access_token is not None

    async def login(self, email: str, password: str) -> None:
        """Full PKCE login: load login page, submit credentials, exchange code."""
        verifier = _b64url(secrets.token_bytes(32))
        challenge = _b64url(hashlib.sha256(verifier.encode("ascii")).digest())
        state = secrets.token_hex(16)

        auth_url = (
            f"{_SSO_BASE}/auth"
            f"?client_id={_CLIENT_ID}"
            f"&redirect_uri={_REDIRECT_URI}"
            f"&response_type=code&scope=openid&state={state}"
            f"&code_challenge={challenge}&code_challenge_method=S256"
        )

        async with self._session.get(auth_url, headers=BROWSER_HEADERS) as resp:
            login_html = await resp.text()

        form_action = self._parse_form_action(login_html)
        if not form_action:
            raise EdcError("Could not locate login form action URL in the SSO page.")

        form = {"username": email, "password": password, "credentialId": ""}
        async with self._session.post(
            form_action, data=form, headers=BROWSER_HEADERS, allow_redirects=False
        ) as resp:
            code = await self._extract_code(resp)

        if not code:
            raise EdcError("Authorization code not found after login. Verify credentials.")

        await self._exchange_code(code, verifier)

    async def valid_access_token(self) -> str:
        if self._access_token is None:
            raise EdcError("Not authenticated. Call login() first.")
        now = datetime.now(timezone.utc)
        if now >= self._access_expiry:
            if now >= self._refresh_expiry:
                raise EdcError("Session has expired. Please log in again.")
            await self._refresh()
        assert self._access_token is not None
        return self._access_token

    async def logout(self) -> None:
        if self._access_token is None:
            return
        try:
            body = {"client_id": _CLIENT_ID, "post_logout_redirect_uri": _REDIRECT_URI}
            if self._id_token:
                body["id_token_hint"] = self._id_token
            async with self._session.post(
                f"{_SSO_BASE}/logout", data=body, headers=BROWSER_HEADERS, allow_redirects=False
            ):
                pass
        finally:
            self._access_token = None
            self._refresh_token = None
            self._id_token = None

    # --- internals -------------------------------------------------------

    async def _extract_code(self, resp: aiohttp.ClientResponse) -> str | None:
        """Follow the post-login redirect chain until the portal ?code= URL."""
        for _ in range(10):
            location = resp.headers.get("Location")
            if not location:
                return None
            location = urljoin(str(resp.url), location)
            parsed = urlparse(location)
            if "portal.edc-cr.cz" in location or location.startswith(_REDIRECT_URI):
                code = parse_qs(parsed.query).get("code", [None])[0]
                if code:
                    return code
            resp = await self._session.get(
                location, headers=BROWSER_HEADERS, allow_redirects=False
            )
        return None

    async def _exchange_code(self, code: str, verifier: str) -> None:
        body = {
            "grant_type": "authorization_code",
            "client_id": _CLIENT_ID,
            "redirect_uri": _REDIRECT_URI,
            "code": code,
            "code_verifier": verifier,
        }
        await self._token_request(body, "Token exchange failed")

    async def _refresh(self) -> None:
        body = {
            "grant_type": "refresh_token",
            "client_id": _CLIENT_ID,
            "refresh_token": self._refresh_token,
        }
        await self._token_request(body, "Token refresh failed")

    async def _token_request(self, body: dict[str, str], context: str) -> None:
        async with self._session.post(
            f"{_SSO_BASE}/token", data=body, headers=BROWSER_HEADERS
        ) as resp:
            text = await resp.text()
            if resp.status != 200:
                raise EdcError(f"{context}: HTTP {resp.status} — {text}")
            import json

            token = json.loads(text)

        self._access_token = token["access_token"]
        self._refresh_token = token.get("refresh_token")
        self._id_token = token.get("id_token")
        now = datetime.now(timezone.utc)
        self._access_expiry = now + timedelta(seconds=int(token.get("expires_in", 60)) - 30)
        self._refresh_expiry = now + timedelta(
            seconds=int(token.get("refresh_expires_in", 60)) - 30
        )

    @staticmethod
    def _parse_form_action(html: str) -> str:
        match = _FORM_ACTION_RE.search(html)
        if match:
            return _html_unescape(match.group(1))
        rel = _FORM_ACTION_REL_RE.search(html)
        if rel:
            return "https://sso.portal.edc-cr.cz" + _html_unescape(rel.group(1))
        return ""


def _html_unescape(value: str) -> str:
    import html

    return html.unescape(value)
