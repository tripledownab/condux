"""The runtime dependency inventory that rides events (ADR-0041).

Which package versions are actually installed alongside the running process, as opposed to which ones
a requirements file declares. The relay indexes this so a security advisory can be answered with "and
you are running 2.19.0 in production" rather than only "your lockfile says so".

Reading the installed distributions is what answers that: a requirements file states ranges, and what
matters is what resolution actually put in the environment. ``importlib.metadata`` is the stdlib's own
view of exactly that, so there is nothing to parse and nothing to guess.
"""

import time
from typing import Dict, Mapping, Optional

# The most entries carried on one event. A large environment runs past this, and the cap applies after
# sorting, so which entries survive is stable across events rather than varying with iteration order:
# the server sees one consistent set instead of a shifting sample.
MAX_MODULES = 1000

# How long to wait before repeating the inventory on another event.
#
# This is what makes the feature affordable. The map is repeated on every event that carries it, and
# the server deduplicates a release's inventory down to one row per package per day, so attaching it
# to every event would spend bytes for nothing. Repeating on an interval rather than sending once
# keeps the robustness that every-event buys: the event carrying the inventory can be dropped by a
# rate limit or a quota rejection before anything parses it, so a single attempt per process would
# lose that day's inventory outright.
MODULES_INTERVAL_SECONDS = 15 * 60

_modules: Optional[Dict[str, str]] = None
_last_attached_at: Optional[float] = None


def collect_modules() -> Dict[str, str]:
    """The installed distributions as a name to version mapping, empty when it cannot be read.

    Never raises. This runs during ``init`` in an application that never asked for it, so a broken
    distribution or an unreadable metadata directory costs that entry, or at worst the inventory, and
    never the caller's startup.
    """
    found: Dict[str, str] = {}
    try:
        from importlib.metadata import distributions

        for dist in distributions():
            try:
                name = dist.metadata["Name"]
                version = dist.version
            except Exception:
                # One unreadable distribution is not a reason to report nothing about the rest.
                continue
            if name and version:
                # setdefault, so the first copy on the path wins, which is the one an import resolves.
                found.setdefault(str(name), str(version))
    except Exception:
        return {}
    return found


def set_modules(modules: Optional[Mapping[str, str]]) -> None:
    """Declare the installed package versions for subsequent events; ``None`` clears them."""
    global _modules, _last_attached_at

    # A fresh declaration is news, so let the next event carry it rather than waiting out an interval
    # started by the previous inventory.
    _last_attached_at = None

    if modules is None:
        _modules = None
        return

    entries = sorted(
        (str(name), str(version))
        for name, version in modules.items()
        if name and version
    )[:MAX_MODULES]
    _modules = dict(entries) if entries else None


def modules_field(now: Optional[float] = None) -> Dict[str, Dict[str, str]]:
    """The inventory's contribution to an event.

    The full map on the first event and then at most once per :data:`MODULES_INTERVAL_SECONDS`, and an
    empty dict otherwise, so an event that carries nothing keeps its exact previous wire shape.
    """
    global _last_attached_at

    if _modules is None:
        return {}

    moment = time.time() if now is None else now
    if _last_attached_at is not None and moment - _last_attached_at < MODULES_INTERVAL_SECONDS:
        return {}

    _last_attached_at = moment
    return {"modules": dict(_modules)}


def clear_modules() -> None:
    """Reset the inventory and its interval. Tests only; a process has one environment."""
    global _modules, _last_attached_at
    _modules = None
    _last_attached_at = None
