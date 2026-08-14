"""Ambient event enrichment: who the user is, which tags and contexts apply, and the breadcrumb trail
leading up to an error.

Set once (or as the app's state changes) and every subsequent event carries it — the first triage
questions ("which customer, which plan, what did they do last") answered without threading anything
through capture calls. The relay already scrubs all of these at ingest and derives the pseudonymous
users-affected key from the user fields.

State is module level, matching the module level ``init``/``capture_exception`` API.
"""

from __future__ import annotations

import time
from typing import Any, Dict, Optional

# Newest trail wins: a long-lived process drops the oldest crumbs rather than growing without bound.
MAX_BREADCRUMBS = 30

_user: Optional[Dict[str, str]] = None
_tags: Dict[str, str] = {}
_contexts: Dict[str, Dict[str, Any]] = {}
_breadcrumbs: list = []


def set_user(user: Optional[Dict[str, str]]) -> None:
    """Attach the signed-in user (``id``/``email``/``username``) to subsequent events; None clears."""
    global _user
    _user = dict(user) if user is not None else None


def set_tag(key: str, value: Optional[str]) -> None:
    """Attach a tag to subsequent events; None removes it."""
    if value is None:
        _tags.pop(key, None)
    else:
        _tags[key] = value


def set_context(name: str, context: Optional[Dict[str, Any]]) -> None:
    """Attach a named context object to subsequent events; None removes it."""
    if context is None:
        _contexts.pop(name, None)
    else:
        _contexts[name] = dict(context)


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

    _breadcrumbs.append(crumb)
    if len(_breadcrumbs) > MAX_BREADCRUMBS:
        del _breadcrumbs[: len(_breadcrumbs) - MAX_BREADCRUMBS]


def clear_scope() -> None:
    """Reset all ambient state (tests, or a full sign-out)."""
    global _user
    _user = None
    _tags.clear()
    _contexts.clear()
    _breadcrumbs.clear()


def scope_fields() -> Dict[str, Any]:
    """The scope's contribution to an event, holding only the keys that are actually set so an
    unenriched event keeps its exact wire shape. Breadcrumbs use the Sentry ``{"values": []}`` envelope.
    """
    fields: Dict[str, Any] = {}
    if _user is not None:
        fields["user"] = dict(_user)
    if _tags:
        fields["tags"] = dict(_tags)
    if _contexts:
        fields["contexts"] = {name: dict(values) for name, values in _contexts.items()}
    if _breadcrumbs:
        fields["breadcrumbs"] = {"values": list(_breadcrumbs)}
    return fields
