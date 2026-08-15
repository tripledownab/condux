"""Django integration for the Condux SDK.

Add it to ``MIDDLEWARE``, outermost is fine since it does not rely on catching anything:

    MIDDLEWARE = [
        "condux.integrations.django.ConduxMiddleware",
        ...
    ]

Reports every uncaught view exception as unhandled, with the request and the matched route, and isolates
enrichment to the request so a ``set_user`` in a view cannot attach to a concurrent one.

**Why this exists rather than the WSGI middleware.** Django's ``BaseHandler`` converts a view exception
into a 500 response via ``response_for_exception``; nothing propagates out of the WSGI application. A
WSGI wrapper sits outside that and sees nothing at all, reporting zero events silently. The
``got_request_exception`` signal is what Django actually fires, so this hooks that.

The middleware itself exists only to bracket the request scope. The reporting is the signal, which is
why its position in ``MIDDLEWARE`` does not matter for whether errors are captured.
"""

from __future__ import annotations

from typing import Any, Callable

import condux

from . import _request


def request_fields(request: Any) -> dict:
    """The request, read from Django's HttpRequest.

    Headers are on the request and deliberately not sent: they carry cookies and authorization, and not
    sending credentials is a stronger guarantee than scrubbing them after they arrive.
    """
    query = request.META.get("QUERY_STRING") if hasattr(request, "META") else None
    return _request.request_fields(getattr(request, "path", None), getattr(request, "method", None), query)


def route_tags(request: Any) -> dict:
    """The matched route as a tag, so the dashboard can facet by view rather than by concrete path.

    ``resolver_match`` carries the URL pattern (``checkout/<str:id>``) and the view name, both of which
    group every instance of a route under one value. It is None when the failure happened before URL
    resolution, which is why this is best effort.
    """
    match = getattr(request, "resolver_match", None)
    if match is None:
        return {}
    tags = {}
    if getattr(match, "route", None):
        tags["route"] = match.route
    if getattr(match, "view_name", None):
        tags["view"] = match.view_name
    return tags


class ConduxMiddleware:
    """Brackets each request in its own enrichment scope, and reports via Django's signal."""

    def __init__(self, get_response: Callable) -> None:
        self.get_response = get_response
        # Imported here rather than at module import time so the base package keeps no dependency on
        # Django: importing condux must never require it.
        from django.core.signals import got_request_exception

        # Django holds signal receivers weakly by default, and this is a bound method, so the connection
        # only survives while the middleware instance does. That is safe here and NOT the same situation
        # as the Flask integration, which passes weak=False: Django retains the middleware chain for the
        # life of the handler, whereas the documented Flask usage discards the object immediately. Pinned
        # by a test that forces a collection between requests, so this stops being an assumption.
        #
        # dispatch_uid keeps it idempotent: Django instantiates middleware per WSGI/ASGI handler, and
        # without it a second handler would connect a second receiver and every error would report twice.
        got_request_exception.connect(self._on_exception, dispatch_uid="condux.report")

    def __call__(self, request: Any) -> Any:
        # Django serves requests concurrently on pooled threads, so enrichment set in a view has to
        # belong to that request. The scope closes on the way out, including when the view raised.
        with condux.request_scope():
            return self.get_response(request)

    def _on_exception(self, sender: Any, request: Any = None, **extra: Any) -> None:
        import sys

        error = sys.exc_info()[1]
        if error is None:
            return
        condux.capture_exception(
            error,
            handled=False,
            request=request_fields(request) if request is not None else None,
            tags=route_tags(request) if request is not None else None,
        )
