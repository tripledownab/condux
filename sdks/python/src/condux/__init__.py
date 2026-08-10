"""Condux SDK for Python — report errors to a Condux relay.

Emits the Sentry "store" wire shape (event_id, timestamp, level, exception.values[]) so the relay's
parser normalizes it exactly like an official Sentry SDK — swap the DSN and it works. Delivery is
resilient: transient failures (429 rate limits, 5xx, network errors) are retried with capped
exponential backoff, honoring the relay's Retry-After on a 429. Reporting never raises — a failed
send returns a :class:`SendResult` you can inspect. The transport and sleep are injectable so
backoff is exercised deterministically with no real waiting. Inspired by common SDK transports,
implemented fresh.
"""

from __future__ import annotations

import enum
import json
import linecache
import time
import traceback
import urllib.error
import urllib.request
import uuid
from dataclasses import dataclass
from types import TracebackType
from typing import Callable, Mapping, Optional, Tuple
from urllib.parse import urlparse

__all__ = ["init", "capture_exception", "capture_message", "send_event", "Level", "SendResult"]


class Level(str, enum.Enum):
    """Event severity, matching the levels the relay understands."""

    DEBUG = "debug"
    INFO = "info"
    WARNING = "warning"
    ERROR = "error"
    FATAL = "fatal"

# A transport POSTs (url, headers, body) and returns (status, response_headers). It must turn an
# HTTP error *response* (e.g. 429/5xx) into a returned status — only a genuine network failure
# should raise (the retry loop treats a raise as a transient error and retries).
Transport = Callable[[str, Mapping[str, str], bytes], Tuple[int, Mapping[str, str]]]
# Sleeps for a number of milliseconds. Injected in tests to record delays without waiting.
SleepFn = Callable[[float], None]

DEFAULT_MAX_RETRIES = 3
BASE_BACKOFF_MS = 200.0
MAX_BACKOFF_MS = 30_000.0


@dataclass(frozen=True)
class SendResult:
    """Outcome of a delivery attempt sequence. Never raised — inspect ``ok``."""

    ok: bool
    attempts: int
    status: Optional[int] = None
    error: Optional[str] = None


@dataclass
class _Options:
    dsn: str
    environment: Optional[str] = None
    release: Optional[str] = None
    max_retries: Optional[int] = None
    transport: Optional[Transport] = None
    sleep: Optional[SleepFn] = None


_options: Optional[_Options] = None


def init(
    dsn: str,
    *,
    environment: Optional[str] = None,
    release: Optional[str] = None,
    max_retries: Optional[int] = None,
    transport: Optional[Transport] = None,
    sleep: Optional[SleepFn] = None,
) -> None:
    """Configure the SDK with a project DSN (and optional testing hooks)."""
    global _options
    _options = _Options(dsn, environment, release, max_retries, transport, sleep)


def capture_exception(error: BaseException, handled: bool = True) -> SendResult:
    """Report an exception as an error-level event, with its stack trace.

    Pass ``handled=False`` when reporting an uncaught exception (a framework integration does this) so the
    relay marks it unhandled. Never raises on a delivery failure — returns a :class:`SendResult`.
    """
    return _dispatch({"level": Level.ERROR.value, "exception": {"values": [_to_exception(error, handled)]}})


def capture_message(message: str, level: Level = Level.INFO) -> SendResult:
    """Report a bare message event at the given level (default info)."""
    return _dispatch({"level": level.value, "message": message})


def _dispatch(fields: Mapping[str, object]) -> SendResult:
    if _options is None:
        raise RuntimeError("Condux SDK not initialized — call init(dsn) first")

    endpoint, project_id, public_key = _parse_dsn(_options.dsn)
    event: dict = {
        "event_id": uuid.uuid4().hex,
        "timestamp": time.time(),  # epoch seconds, the Sentry store convention
        "platform": "python",
        **fields,
    }
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


def send_event(
    url: str,
    headers: Mapping[str, str],
    body: bytes,
    *,
    max_retries: Optional[int] = None,
    transport: Optional[Transport] = None,
    sleep: Optional[SleepFn] = None,
) -> SendResult:
    """POST a serialized event to the relay, retrying transient failures with backoff.

    Never raises — returns a :class:`SendResult` describing the outcome.
    """
    retries = DEFAULT_MAX_RETRIES if max_retries is None else max_retries
    send = transport or _urllib_transport
    wait = sleep or _default_sleep

    last_status: Optional[int] = None
    last_error: Optional[str] = None

    for attempt in range(retries + 1):
        status: Optional[int] = None
        response_headers: Mapping[str, str] = {}
        try:
            status, response_headers = send(url, headers, body)
        except Exception as cause:  # noqa: BLE001 — a failed send must never crash the caller
            last_error = str(cause) or type(cause).__name__

        if status is not None:
            last_status = status
            last_error = None
            if 200 <= status < 300:
                return SendResult(ok=True, attempts=attempt + 1, status=status)
            if not _is_retriable(status):
                return SendResult(ok=False, attempts=attempt + 1, status=status)

        # Transient failure (429 / 5xx / network). Stop if that was the final attempt.
        if attempt == retries:
            break
        wait(_backoff_ms(attempt, status, response_headers))

    return SendResult(ok=False, attempts=retries + 1, status=last_status, error=last_error)


# 429 (rate limited) and 5xx are worth retrying; other 4xx (bad DSN, bad payload) are not.
def _is_retriable(status: int) -> bool:
    return status == 429 or status >= 500


# Honor Retry-After (seconds) on a 429; otherwise capped exponential backoff.
def _backoff_ms(attempt: int, status: Optional[int], headers: Mapping[str, str]) -> float:
    if status == 429:
        retry_after = _parse_retry_after_ms(_get_header(headers, "retry-after"))
        if retry_after is not None:
            return retry_after
    return min(BASE_BACKOFF_MS * (2**attempt), MAX_BACKOFF_MS)


def _parse_retry_after_ms(value: Optional[str]) -> Optional[float]:
    if value is None or value.strip() == "":
        return None
    try:
        seconds = float(value)
    except ValueError:
        return None
    return max(0.0, seconds) * 1000.0


def _get_header(headers: Mapping[str, str], name: str) -> Optional[str]:
    target = name.lower()
    for key, value in headers.items():
        if key.lower() == target:
            return value
    return None


def _default_sleep(ms: float) -> None:
    time.sleep(ms / 1000.0)


def _to_exception(error: BaseException, handled: bool = True) -> dict:
    exception: dict = {
        "type": type(error).__name__,
        "value": str(error),
        "module": type(error).__module__,
        # The relay reads mechanism.handled for the unhandled badge. A direct capture_exception is a handled
        # capture; a framework integration reporting an uncaught exception passes handled=False.
        "mechanism": {"type": "generic", "handled": handled},
    }
    frames = _stack_frames(error.__traceback__)
    if frames:
        exception["stacktrace"] = {"frames": frames}
    return exception


# extract_tb yields frames oldest-first (the raising frame last), which is the order the relay's
# fingerprinter and issue detail expect. The raw traceback is walked in parallel so each wire frame
# can carry its locals (Sentry vars); extract_tb alone drops them.
def _stack_frames(tb: Optional[TracebackType]) -> list:
    raw_frames = []
    cursor = tb
    while cursor is not None:
        raw_frames.append(cursor.tb_frame)
        cursor = cursor.tb_next

    frames = []
    for index, frame in enumerate(traceback.extract_tb(tb)):
        wire = {
            "filename": frame.filename,
            "function": frame.name,
            "lineno": frame.lineno or 0,
            "context_line": frame.line or "",
            "in_app": _is_in_app(frame.filename),
        }
        if frame.lineno:
            before, after = _source_context(frame.filename, frame.lineno)
            if before:
                wire["pre_context"] = before
            if after:
                wire["post_context"] = after
        if index < len(raw_frames):
            local_vars = _frame_vars(raw_frames[index])
            if local_vars:
                wire["vars"] = local_vars
        frames.append(wire)
    return frames


_CONTEXT_LINES = 5
_VAR_VALUE_LIMIT = 200


# Surrounding source lines via linecache (stdlib, no file-size surprises); missing sources (REPL,
# zipped apps) simply yield nothing.
def _source_context(filename: str, lineno: int) -> Tuple[list, list]:
    before = []
    for n in range(max(1, lineno - _CONTEXT_LINES), lineno):
        line = linecache.getline(filename, n)
        if line:
            before.append(line.rstrip("\n"))
    after = []
    for n in range(lineno + 1, lineno + 1 + _CONTEXT_LINES):
        line = linecache.getline(filename, n)
        if line:
            after.append(line.rstrip("\n"))
    return before, after


# Locals as short reprs; a value whose repr fails or runs long is truncated, never raised.
def _frame_vars(frame) -> dict:
    local_vars = {}
    for name, value in frame.f_locals.items():
        try:
            text = repr(value)
        except Exception:  # noqa: BLE001 - a broken __repr__ must not break error reporting
            text = "<unrepresentable>"
        if len(text) > _VAR_VALUE_LIMIT:
            text = text[:_VAR_VALUE_LIMIT] + "..."
        local_vars[name] = text
    return local_vars


# Application frames drive grouping and the culprit; standard-library and dependency frames are noise.
def _is_in_app(filename: str) -> bool:
    return "site-packages" not in filename and "/lib/python" not in filename


def _parse_dsn(dsn: str) -> Tuple[str, str, str]:
    parsed = urlparse(dsn)  # <scheme>://<publicKey>@<host>[:port]/<projectId>
    port = f":{parsed.port}" if parsed.port else ""
    endpoint = f"{parsed.scheme}://{parsed.hostname}{port}"
    return endpoint, parsed.path.lstrip("/"), parsed.username or ""


def _urllib_transport(
    url: str, headers: Mapping[str, str], body: bytes
) -> Tuple[int, Mapping[str, str]]:
    request = urllib.request.Request(url, data=body, headers=dict(headers), method="POST")
    try:
        with urllib.request.urlopen(request, timeout=5) as response:  # noqa: S310 — user DSN
            return response.status, dict(response.headers)
    except urllib.error.HTTPError as error:  # 4xx/5xx are responses, not network failures
        return error.code, dict(error.headers)
