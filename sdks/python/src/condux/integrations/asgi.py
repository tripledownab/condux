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


class ConduxAsgiMiddleware:
    def __init__(self, app: Callable) -> None:
        self._app = app

    async def __call__(self, scope: dict, receive: Callable, send: Callable) -> None:
        if scope.get("type") != "http":
            await self._app(scope, receive, send)
            return
        try:
            await self._app(scope, receive, send)
        except Exception as error:  # noqa: BLE001 - report anything the app raises, then re-raise
            condux.capture_exception(error, handled=False)
            raise
