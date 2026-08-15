"""Ambient event enrichment: who the user is, which tags and contexts apply, and the breadcrumb trail
leading up to an error.

Set once (or as the app's state changes) and every subsequent event carries it — the first triage
questions ("which customer, which plan, what did they do last") answered without threading anything
through capture calls. The relay already scrubs all of these at ingest and derives the pseudonymous
users-affected key from the user fields.

There are two layers, and the distinction is the whole point:

* **Process state**, set at startup and shared by everything. Right for facts about the deployment.
* **A request scope**, active only inside ``with request_scope():``. Right for facts about one request.

Without the second layer, ``set_user`` in a web app is a cross-request leak: a server handles requests
concurrently, so one request's user would attach to another request's error. That is worse than having
no user at all, because it is confidently wrong and points an investigation at the wrong customer. The
integrations open a request scope for you, so a ``set_user`` inside a view stays inside that request.

A :class:`contextvars.ContextVar` carries it, which covers threads and asyncio with one mechanism: a
thread pool worker and an ``await`` chain each see their own value. The token is always reset on the way
out, because WSGI servers reuse threads and a value left behind would surface in the next request served
by that worker.
"""

from __future__ import annotations

import time
from contextlib import contextmanager
from contextvars import ContextVar
from typing import Any, Dict, Iterator, Optional

# Newest trail wins: a long-lived process drops the oldest crumbs rather than growing without bound.
MAX_BREADCRUMBS = 30

_user: Optional[Dict[str, str]] = None
_tags: Dict[str, str] = {}
_contexts: Dict[str, Dict[str, Any]] = {}
_breadcrumbs: list = []

# None means no request is in flight, so writes fall through to the process state above. That fallback is
# what keeps a startup-time set_tag working exactly as it did before request scopes existed.
_request_state: ContextVar[Optional[Dict[str, Any]]] = ContextVar("condux_request_scope", default=None)


def _new_state() -> Dict[str, Any]:
    return {"user": None, "tags": {}, "contexts": {}, "breadcrumbs": []}


@contextmanager
def request_scope() -> Iterator[None]:
    """Isolate enrichment to one request. Anything set inside is visible only to events captured inside.

    The integrations wrap each request in this. Call it directly for a background job or consumer, which
    has the same problem: many in flight at once, each wanting its own identity on its own events.
    """
    token = _request_state.set(_new_state())
    try:
        yield
    finally:
        # Always reset. A thread returned to a WSGI pool with state still attached would hand it to the
        # next request that worker picks up, which is the exact leak this exists to prevent.
        _request_state.reset(token)


def set_user(user: Optional[Dict[str, str]]) -> None:
    """Attach the signed-in user (``id``/``email``/``username``) to subsequent events; None clears.

    Inside a request scope this applies to that request alone; outside one it is process wide.
    """
    global _user
    state = _request_state.get()
    if state is not None:
        state["user"] = dict(user) if user is not None else None
        return
    _user = dict(user) if user is not None else None


def set_tag(key: str, value: Optional[str]) -> None:
    """Attach a tag to subsequent events; None removes it."""
    state = _request_state.get()
    tags = _tags if state is None else state["tags"]
    if value is None:
        tags.pop(key, None)
    else:
        tags[key] = value


def set_context(name: str, context: Optional[Dict[str, Any]]) -> None:
    """Attach a named context object to subsequent events; None removes it."""
    state = _request_state.get()
    contexts = _contexts if state is None else state["contexts"]
    if context is None:
        contexts.pop(name, None)
    else:
        contexts[name] = dict(context)


def add_breadcrumb(
    message: str,
    *,
    category: Optional[str] = None,
    level: Optional[str] = None,
    type: Optional[str] = None,  # noqa: A002 - the Sentry wire field is named "type"
    data: Optional[Dict[str, Any]] = None,
    timestamp: Optional[float] = None,
) -> None:
    """Record a breadcrumb; the trail (newest last, capped) rides every subsequent event."""
    crumb: Dict[str, Any] = {"message": message, "timestamp": time.time() if timestamp is None else timestamp}
    if category is not None:
        crumb["category"] = category
    if level is not None:
        crumb["level"] = level
    if type is not None:
        crumb["type"] = type
    if data is not None:
        crumb["data"] = data

    state = _request_state.get()
    trail = _breadcrumbs if state is None else state["breadcrumbs"]
    trail.append(crumb)
    if len(trail) > MAX_BREADCRUMBS:
        del trail[: len(trail) - MAX_BREADCRUMBS]


def clear_scope() -> None:
    """Reset all ambient state (tests, or a full sign-out). Clears the request scope when one is active."""
    global _user
    state = _request_state.get()
    if state is not None:
        state.update(_new_state())
        return
    _user = None
    _tags.clear()
    _contexts.clear()
    _breadcrumbs.clear()


def scope_fields() -> Dict[str, Any]:
    """The scope's contribution to an event, holding only the keys that are actually set so an
    unenriched event keeps its exact wire shape. Breadcrumbs use the Sentry ``{"values": []}`` envelope.

    The request scope layers over the process state rather than replacing it, so a request keeps the
    deployment-wide tags while overriding the ones it sets itself.
    """
    state = _request_state.get() or {}

    user = state.get("user") or _user
    tags = {**_tags, **state.get("tags", {})}
    contexts = {**_contexts, **state.get("contexts", {})}
    # Concatenated, not merged: the trail is a sequence, and the process-level crumbs genuinely happened
    # before the ones recorded during the request.
    breadcrumbs = [*_breadcrumbs, *state.get("breadcrumbs", [])][-MAX_BREADCRUMBS:]

    fields: Dict[str, Any] = {}
    if user is not None:
        fields["user"] = dict(user)
    if tags:
        fields["tags"] = dict(tags)
    if contexts:
        fields["contexts"] = {name: dict(values) for name, values in contexts.items()}
    if breadcrumbs:
        fields["breadcrumbs"] = {"values": list(breadcrumbs)}
    return fields
