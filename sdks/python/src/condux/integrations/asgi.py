"""ASGI middleware for the Condux SDK — covers FastAPI, Starlette, and other ASGI frameworks.

Wrap the ASGI app; any exception it raises while handling an HTTP request is reported to Condux (as
unhandled) and re-raised, so the framework's own error handling still runs. Non-HTTP scopes (lifespan,
websocket) pass straight through.

    # FastAPI / Starlette
    from condux.integrations.asgi import ConduxAsgiMiddleware
    app = ConduxAsgiMiddleware(app)
"""

from __future__ import annotations

from typing import Callable

import condux

from . import _request


def request_fields(scope: dict) -> dict:
    """The request, read from the ASGI scope. Shaping is shared with the WSGI integration."""
    query = scope.get("query_string") or b""
    if isinstance(query, bytes):
        # ASGI gives the query as bytes; latin-1 round-trips any byte, so a malformed query cannot raise
        # from inside the reporting path.
        query = query.decode("latin-1", "replace")
    return _request.request_fields(scope.get("path") or "/", scope.get("method"), query)


class ConduxAsgiMiddleware:
    def __init__(self, app: Callable) -> None:
        self._app = app

    async def __call__(self, scope: dict, receive: Callable, send: Callable) -> None:
        if scope.get("type") != "http":
            await self._app(scope, receive, send)
            return
        # A scope per request. Concurrent requests interleave on one event loop, so enrichment set in a
        # handler has to belong to that request rather than to the process. contextvars follow the await
        # chain, so a value set here stays with this task.
        with condux.request_scope():
            try:
                await self._app(scope, receive, send)
            except Exception as error:  # noqa: BLE001 - report anything the app raises, then re-raise
                condux.capture_exception(error, handled=False, request=request_fields(scope))
                raise
