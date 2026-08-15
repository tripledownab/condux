"""WSGI middleware for the Condux SDK, for a WSGI application that has no error handling of its own.

**Do not use this with Flask or Django.** Use ``condux.integrations.flask`` or
``condux.integrations.django`` instead. Both frameworks catch a view exception, convert it to a 500 and
return a response, so nothing propagates out of the application: this middleware wraps the application
from the outside and would therefore see nothing at all, reporting zero events with no indication that
anything is wrong.

What it does cover is a bare WSGI application, or a framework that lets exceptions escape, where the
exception genuinely does reach the wrapper:

    from condux.integrations.wsgi import ConduxWsgiMiddleware
    application = ConduxWsgiMiddleware(my_wsgi_app)

Any exception raised while producing the response is reported as unhandled and re-raised, so the
application's own handling still runs.
"""

from __future__ import annotations

from typing import Callable, Iterable

import condux

from . import _request


def request_fields(environ: dict) -> dict:
    """The request, read from the WSGI environ. Shaping is shared with the ASGI integration."""
    return _request.request_fields(
        environ.get("PATH_INFO") or "/", environ.get("REQUEST_METHOD"), environ.get("QUERY_STRING")
    )


class ConduxWsgiMiddleware:
    def __init__(self, app: Callable) -> None:
        self._app = app

    def __call__(self, environ: dict, start_response: Callable) -> Iterable[bytes]:
        # A scope per request, so a set_user inside a view belongs to that request and cannot attach to a
        # concurrent one. Threads are pooled and reused, which is exactly why this has to be scoped rather
        # than left to process state.
        #
        # It covers producing the response, which is where WSGI surfaces an application exception and
        # where a view sets the user. A streaming body is iterated by the server after this returns, so
        # enrichment set during iteration falls back to process state, as it did before request scopes.
        with condux.request_scope():
            try:
                return self._app(environ, start_response)
            except Exception as error:  # noqa: BLE001 - report anything the app raises, then re-raise
                condux.capture_exception(error, handled=False, request=request_fields(environ))
                raise
