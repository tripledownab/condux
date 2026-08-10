"""WSGI middleware for the Condux SDK — covers Flask, Django, and other WSGI frameworks.

Wrap the app's WSGI callable; any exception it raises while producing the response is reported to Condux
(as unhandled) and re-raised, so the framework's own error handling still runs.

    # Flask
    from condux.integrations.wsgi import ConduxWsgiMiddleware
    app.wsgi_app = ConduxWsgiMiddleware(app.wsgi_app)

    # Django (wsgi.py)
    application = ConduxWsgiMiddleware(get_wsgi_application())
"""

from __future__ import annotations

from typing import Callable, Iterable

import condux


class ConduxWsgiMiddleware:
    def __init__(self, app: Callable) -> None:
        self._app = app

    def __call__(self, environ: dict, start_response: Callable) -> Iterable[bytes]:
        try:
            return self._app(environ, start_response)
        except Exception as error:  # noqa: BLE001 - report anything the app raises, then re-raise
            condux.capture_exception(error, handled=False)
            raise
