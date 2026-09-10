"""Delivering an event to the relay: the transport seam, the retry policy and the
default urllib sender.

Never raises. A transport turns an HTTP error *response* into a returned status, so only a genuine
network failure raises, and the retry loop treats a raise as transient. Both the transport and the
sleep are injectable, which is how backoff is tested with no real waiting.
"""

from __future__ import annotations

import math
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from typing import Callable, Mapping, Optional, Tuple


# A transport POSTs (url, headers, body) and returns (status, response_headers). It must turn an
# HTTP error *response* (e.g. 429/5xx) into a returned status. Only a genuine network failure
# should raise (the retry loop treats a raise as a transient error and retries).
Transport = Callable[[str, Mapping[str, str], bytes], Tuple[int, Mapping[str, str]]]
# Sleeps for a number of milliseconds. Injected in tests to record delays without waiting.
SleepFn = Callable[[float], None]

DEFAULT_MAX_RETRIES = 3
BASE_BACKOFF_MS = 200.0
MAX_BACKOFF_MS = 30_000.0


@dataclass(frozen=True)
class SendResult:
    """Outcome of a delivery attempt sequence. Never raised: inspect ``ok``."""

    ok: bool
    attempts: int
    status: Optional[int] = None
    error: Optional[str] = None


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

    Never raises. It returns a :class:`SendResult` describing the outcome.
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
        except Exception as cause:  # noqa: BLE001 - a failed send must never crash the caller
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


# Honor Retry-After (seconds) on a 429, else exponential backoff. Both paths are capped at
# MAX_BACKOFF_MS, at ONE return so a later branch cannot route past it: the SDK holds the caller's
# thread while it waits, so bounding that wait is its own obligation and not the relay's to set.
def _backoff_ms(attempt: int, status: Optional[int], headers: Mapping[str, str]) -> float:
    requested = None
    if status == 429:
        requested = _parse_retry_after_ms(_get_header(headers, "retry-after"))
    if requested is None:
        requested = BASE_BACKOFF_MS * (2**attempt)
    return min(requested, MAX_BACKOFF_MS)


def _parse_retry_after_ms(value: Optional[str]) -> Optional[float]:
    if value is None or value.strip() == "":
        return None
    try:
        seconds = float(value)
    except ValueError:
        return None
    # float() accepts "Infinity" and "nan", and neither has a sensible answer downstream. Not finite
    # is not an instruction, so it falls back to the schedule.
    if not math.isfinite(seconds):
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




def _urllib_transport(
    url: str, headers: Mapping[str, str], body: bytes
) -> Tuple[int, Mapping[str, str]]:
    request = urllib.request.Request(url, data=body, headers=dict(headers), method="POST")
    try:
        with urllib.request.urlopen(request, timeout=5) as response:  # noqa: S310 - user DSN
            return response.status, dict(response.headers)
    except urllib.error.HTTPError as error:  # 4xx/5xx are responses, not network failures
        return error.code, dict(error.headers)
