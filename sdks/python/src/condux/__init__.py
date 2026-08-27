"""Condux SDK for Python — report errors to a Condux relay.

Emits the Sentry "store" wire shape (event_id, timestamp, level, exception.values[]) so the relay's
parser normalizes it exactly like an official Sentry SDK — swap the DSN and it works. Delivery is
resilient: transient failures (429 rate limits, 5xx, network errors) are retried with capped
exponential backoff, honoring the relay's Retry-After on a 429. Reporting never raises — a failed
send returns a :class:`SendResult` you can inspect. The transport and sleep are injectable so
backoff is exercised deterministically with no real waiting. Inspired by common SDK transports,
implemented fresh.

This module is the public surface and the client: the options, ``init`` and the capture calls. The
process-wide state stays here deliberately, because that is the name callers and tests address it by.
Building an exception payload lives in ``payload``, and delivering it in ``transport``.
"""

from __future__ import annotations

import enum
import json
import time
import uuid
import warnings
from dataclasses import dataclass
from typing import Mapping, Optional

from .dsn import parse_dsn
from .payload import _to_exception
from .scope import (
    add_breadcrumb,
    clear_scope,
    request_scope,
    scope_fields,
    set_context,
    set_tag,
    set_user,
)
from .transport import SendResult, SleepFn, Transport, send_event

__all__ = [
    "init",
    "capture_exception",
    "capture_message",
    "send_event",
    "Level",
    "SendResult",
    "set_user",
    "request_scope",
    "set_tag",
    "set_context",
    "add_breadcrumb",
    "clear_scope",
]


class Level(str, enum.Enum):
    """Event severity, matching the levels the relay understands."""

    DEBUG = "debug"
    INFO = "info"
    WARNING = "warning"
    ERROR = "error"
    FATAL = "fatal"


@dataclass
class _Options:
    dsn: str
    environment: Optional[str] = None
    release: Optional[str] = None
    max_retries: Optional[int] = None
    transport: Optional[Transport] = None
    sleep: Optional[SleepFn] = None


_options: Optional[_Options] = None
_warned_uninitialized = False


def init(
    dsn: str,
    *,
    environment: Optional[str] = None,
    release: Optional[str] = None,
    max_retries: Optional[int] = None,
    transport: Optional[Transport] = None,
    sleep: Optional[SleepFn] = None,
) -> None:
    """Configure the SDK with a project DSN (and optional testing hooks).

    Raises :class:`ValueError` on a malformed DSN. Setup runs once at developer time, so a typo is worth
    failing loudly for — the alternative is an install that silently reports nowhere.
    """
    global _options
    parse_dsn(dsn)
    _options = _Options(dsn, environment, release, max_retries, transport, sleep)


def capture_exception(
    error: BaseException,
    handled: bool = True,
    *,
    request: Optional[Mapping[str, str]] = None,
    tags: Optional[Mapping[str, str]] = None,
) -> SendResult:
    """Report an exception as an error-level event, with its stack trace.

    Pass ``handled=False`` when reporting an uncaught exception (a framework integration does this) so the
    relay marks it unhandled. Never raises on a delivery failure — returns a :class:`SendResult`.

    ``request`` (``url``/``method``/``query_string``) and ``tags`` describe this one event. They are passed
    here rather than set on the scope because scope state outlives the call: on a server handling requests
    concurrently, request detail set ambiently attaches to whichever event is captured next, which may
    belong to a different request.
    """
    return _dispatch(
        {"level": Level.ERROR.value, "exception": {"values": [_to_exception(error, handled)]}},
        request=request,
        tags=tags,
    )


def capture_message(
    message: str,
    level: Level = Level.INFO,
    *,
    request: Optional[Mapping[str, str]] = None,
    tags: Optional[Mapping[str, str]] = None,
) -> SendResult:
    """Report a bare message event at the given level (default info)."""
    return _dispatch({"level": level.value, "message": message}, request=request, tags=tags)


def _dispatch(
    fields: Mapping[str, object],
    *,
    request: Optional[Mapping[str, str]] = None,
    tags: Optional[Mapping[str, str]] = None,
) -> SendResult:
    # Reporting never raises — an error monitor that raises turns a handled error into an unhandled one in
    # exactly the code path where someone is already dealing with a failure, and the framework middleware
    # capture inside an `except` block, so raising here would replace the application's own exception.
    global _warned_uninitialized
    if _options is None:
        if not _warned_uninitialized:
            _warned_uninitialized = True
            warnings.warn(
                "Condux: capture called before init(dsn); events are being dropped.",
                RuntimeWarning,
                stacklevel=3,
            )
        return SendResult(ok=False, attempts=0, error="not_initialized")

    endpoint, project_id, public_key = parse_dsn(_options.dsn)
    ambient = scope_fields()
    event: dict = {
        "event_id": uuid.uuid4().hex,
        "timestamp": time.time(),  # epoch seconds, the Sentry store convention
        "platform": "python",
        **ambient,
        **fields,
    }
    if request:
        event["request"] = dict(request)
    if tags:
        # Merged over the ambient tags rather than replacing them, so a per-event tag cannot silently
        # drop the deployment-wide ones.
        event["tags"] = {**ambient.get("tags", {}), **tags}
    if _options.environment is not None:
        event["environment"] = _options.environment
    if _options.release is not None:
        event["release"] = _options.release

    return send_event(
        f"{endpoint}/api/{project_id}/store/",
        {"content-type": "application/json", "x-condux-auth": public_key},
        json.dumps(event).encode(),
        max_retries=_options.max_retries,
        transport=_options.transport,
        sleep=_options.sleep,
    )
