"""Parse a Condux/Sentry DSN into the relay endpoint, project id, and public key.

Validated rather than best-effort: a DSN with a typo parses "successfully" under a bare urlparse and
yields an install that reports nowhere, which looks exactly like an application with no errors. So the
malformed case raises at ``init`` (developer time), never at capture (production request time).
"""

from __future__ import annotations

from typing import Tuple
from urllib.parse import urlparse

_ALLOWED_SCHEMES = ("http", "https")
_PREFIX = "Invalid Condux DSN:"


def parse_dsn(dsn: str) -> Tuple[str, str, str]:
    """Split a DSN into ``(endpoint, project_id, public_key)``.

    Raises :class:`ValueError` naming the missing part if the DSN is not
    ``<scheme>://<publicKey>@<host>[:port]/<projectId>``.
    """
    try:
        parsed = urlparse(dsn)
        port = f":{parsed.port}" if parsed.port else ""
    except ValueError as cause:  # a non-numeric port makes .port itself raise
        raise ValueError(f"{_PREFIX} {dsn!r} has an invalid port ({cause}).") from cause

    if parsed.scheme not in _ALLOWED_SCHEMES:
        raise ValueError(f"{_PREFIX} {dsn!r} must start with http:// or https://.")
    if not parsed.username:
        raise ValueError(f"{_PREFIX} {dsn!r} is missing the public key (expected <scheme>://<key>@<host>/<projectId>).")
    if not parsed.hostname:
        raise ValueError(f"{_PREFIX} {dsn!r} is missing the relay host.")

    project_id = parsed.path.lstrip("/")
    if not project_id:
        raise ValueError(f"{_PREFIX} {dsn!r} is missing the project id (the last path segment).")

    return f"{parsed.scheme}://{parsed.hostname}{port}", project_id, parsed.username
