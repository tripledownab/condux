"""Building the wire payload for an exception: its stack frames, their source context and
their locals.

in_app decides grouping, the culprit and what the fix engine reads, so what counts as application
code is decided here. This SDK's own frames are excluded deliberately: they are monitoring plumbing,
and their locals ARE the request, headers included.
"""

from __future__ import annotations

import linecache
import os
import traceback
from types import TracebackType
from typing import Optional, Tuple


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
        # Never this SDK's own locals: they are the request object itself, headers and all.
        if index < len(raw_frames) and not _is_sdk_frame(frame.filename):
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
    if _is_sdk_frame(filename):
        return False
    return "site-packages" not in filename and "/lib/python" not in filename


# This package's own directory, resolved once. Compared as a path prefix rather than by looking for
# "condux" in the filename, which would also match an application module of that name.
_SDK_ROOT = os.path.dirname(os.path.abspath(__file__))


def _is_sdk_frame(filename: str) -> bool:
    """True for a frame inside this SDK: the client itself, or one of the framework integrations.

    Two reasons this matters, and the second is the important one.

    It is not application code, so marking it in-app pushes monitoring plumbing into the user's stack
    trace and into grouping. That was only ever avoided by accident, because a pip install lands in
    site-packages; an editable install, a vendored copy or a custom prefix all defeat that.

    More seriously, these frames' locals ARE the request. The WSGI middleware holds `environ` and the
    ASGI one holds `scope`, both of which carry every header, including Cookie and Authorization. The
    integrations deliberately keep headers out of the reported request, so capturing them here would
    ship the credentials anyway by another route.
    """
    try:
        return os.path.abspath(filename).startswith(_SDK_ROOT)
    except (TypeError, ValueError):  # a synthetic frame with no real path ("<string>")
        return False
