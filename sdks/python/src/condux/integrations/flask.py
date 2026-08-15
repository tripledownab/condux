"""Flask integration for the Condux SDK.

    from condux.integrations.flask import ConduxFlask

    app = Flask(__name__)
    ConduxFlask(app)

Reports every uncaught view exception as unhandled, with the request and the matched route, and isolates
enrichment to the request so a ``set_user`` in a view cannot attach to a concurrent one.

**Why this exists rather than the WSGI middleware.** Flask handles exceptions inside ``wsgi_app`` and
returns a 500; nothing propagates out of the application. A WSGI wrapper sits outside that and therefore
sees nothing at all, which is not a subtle degradation: it reports zero events, silently, forever. The
``got_request_exception`` signal is what Flask actually fires, so this hooks that instead.

Verified against a real Flask app rather than a fake one that raises on cue, because a fake app assumes
the exception escapes, which is the very thing that is false here.
"""

from __future__ import annotations

from typing import Any, Optional

import condux

from . import _request


def request_fields(request: Any) -> dict:
    """The request, read from Flask's request object.

    Headers are available and deliberately not sent: they carry cookies and authorization, and while the
    relay scrubs sensitive keys at ingest, not sending credentials at all is the stronger guarantee.
    """
    return _request.request_fields(
        getattr(request, "path", None),
        getattr(request, "method", None),
        getattr(request, "query_string", b"").decode("latin-1", "replace")
        if isinstance(getattr(request, "query_string", None), bytes)
        else getattr(request, "query_string", None),
    )


def route_tags(request: Any) -> dict:
    """The matched route as a tag, so the dashboard can facet by endpoint.

    ``url_rule`` is the parameterised form (``/checkout/<id>``), which groups every instance of a route
    under one value, unlike the concrete path.
    """
    rule = getattr(request, "url_rule", None)
    tags = {}
    if rule is not None and getattr(rule, "rule", None):
        tags["route"] = rule.rule
    if getattr(request, "endpoint", None):
        tags["endpoint"] = request.endpoint
    return tags


class ConduxFlask:
    """Wires Condux into a Flask app. Constructing it is the whole setup."""

    def __init__(self, app: Any) -> None:
        # Imported here rather than at module import time so the base package keeps no dependency on
        # Flask: importing condux must never require it.
        from flask import got_request_exception, request_started, request_tearing_down

        self._app = app

        # weak=False is load-bearing, not a style choice. blinker holds handlers weakly by default, and
        # these are bound methods of this object. The documented usage is a bare `ConduxFlask(app)`, whose
        # return value nobody keeps, so with weak references the instance is collected and every handler
        # silently disconnects: reporting works until the first garbage collection and then stops with no
        # error. That is the exact silent-failure shape this integration exists to replace.
        request_started.connect(self._on_started, app, weak=False)
        got_request_exception.connect(self._on_exception, app, weak=False)
        request_tearing_down.connect(self._on_teardown, app, weak=False)

    # The open scope is stored on Flask's own per-request `g` rather than in a dict on this object.
    # `g` is already scoped to exactly one request and cleaned up by Flask, so there is no side table to
    # key correctly, nothing to leak if a teardown is ever missed, and no shared mutable state between
    # concurrent requests. The attribute name is prefixed because `g` belongs to the application.
    _SCOPE_ATTRIBUTE = "_condux_request_scope"

    def _on_started(self, sender: Any, **extra: Any) -> None:
        from flask import g

        scope = condux.request_scope()
        scope.__enter__()
        setattr(g, self._SCOPE_ATTRIBUTE, scope)

    def _on_exception(self, sender: Any, exception: BaseException, **extra: Any) -> None:
        from flask import request

        condux.capture_exception(
            exception, handled=False, request=request_fields(request), tags=route_tags(request)
        )

    def _on_teardown(self, sender: Any, exc: Optional[BaseException] = None, **extra: Any) -> None:
        from flask import g

        scope = getattr(g, self._SCOPE_ATTRIBUTE, None)
        if scope is not None:
            # Always exit, or a worker thread carries this request's user into the next request it serves.
            scope.__exit__(None, None, None)
