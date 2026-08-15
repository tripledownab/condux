# condux (Python SDK)

Report errors from a Python app to a Condux relay. Emits the Sentry "store" wire shape, so the relay
normalizes it exactly like an official Sentry SDK — point it at a project DSN and it works.

Delivery is resilient (429 / 5xx / network failures retry with backoff, honoring `Retry-After`) and
**never raises** — a failed send returns a `SendResult`, it does not crash the host app. That holds even
if `init` was never called: capture warns once and drops the event, because the middleware below report
from inside an `except` block and an SDK that raised there would replace your application's exception
with its own. A malformed DSN is refused by `init` instead, where it is a developer-time mistake.

## Install

```bash
pip install condux
```

## Usage

```python
import condux

condux.init(
    dsn="https://<key>@ingest.condux.ai/<projectId>",
    environment="production",
    release="1.4.2",
)

try:
    do_work()
except Exception as error:
    condux.capture_exception(error)
    raise

# or a bare message
condux.capture_message("cache miss storm", condux.Level.WARNING)
```

## Verify your setup

Silence is what a broken error monitor and a healthy app look like from the outside, so prove the
pipeline once:

```bash
CONDUX_DSN="https://<key>@ingest.condux.ai/<projectId>" python -m condux.test_event
```

Exit code 0 means delivered (the message appears as an info-level issue), 1 means delivery failed and
prints why, 2 means the DSN was missing or malformed.

## Enrichment

Attach the ambient facts triage always needs. Every subsequent event carries them, so nothing has to be
threaded through capture calls:

```python
condux.set_user({"id": "1042", "email": "dev@example.com"})   # None clears it (sign-out)
condux.set_tag("plan", "team")                                 # None removes the tag
condux.set_context("job", {"queue": "billing", "attempt": 3})  # None removes the context
condux.add_breadcrumb("charge.started", category="billing")
```

The breadcrumb trail keeps the most recent 30 entries. `condux.clear_scope()` resets everything.

### In a server, scope one request at a time

Those calls are **process wide** by default, which is right for facts about the deployment and wrong
for facts about one request: a server handles requests concurrently, so a bare `set_user` in a view can
attach that user to a different request's error. That is worse than reporting no user, because it is
confidently wrong.

`request_scope()` isolates it. Anything set inside belongs to that request alone, layered over the
process-wide values:

```python
with condux.request_scope():
    condux.set_user({"id": user.id})   # this request only
    handle(request)
```

**The framework integrations do this for you**, so a `set_user` inside a Flask, Django, FastAPI or
Starlette view is already isolated. Call it directly for background jobs and consumers, which have the
same problem: many in flight at once, each wanting its own identity on its own events. It is built on
`contextvars`, so one mechanism covers threads and `asyncio`.

Detail belonging to a single event can also skip the scope entirely:

```python
condux.capture_exception(error, request={"url": "/api/sync"}, tags={"job": "nightly"})
```

## Framework integrations

Each reports an uncaught request exception as unhandled, with the request and the matched route, and
isolates enrichment to that request.

```python
# Flask
from condux.integrations.flask import ConduxFlask
ConduxFlask(app)

# Django: add to MIDDLEWARE in settings.py
MIDDLEWARE = ["condux.integrations.django.ConduxMiddleware", ...]

# FastAPI / Starlette
from condux.integrations.asgi import ConduxAsgiMiddleware
app = ConduxAsgiMiddleware(app)
```

**Flask and Django need their own integrations, not the WSGI one.** Both catch a view exception,
convert it to a 500 and return it, so nothing propagates out of the application. A WSGI middleware wraps
the application from the outside and would therefore never see the exception at all: it reports zero
events, silently, with nothing to tell you it is not working. `ConduxFlask` and `ConduxMiddleware` hook
`got_request_exception` instead, which is the signal each framework actually fires.

`ConduxWsgiMiddleware` remains correct for a bare WSGI application, or any framework that lets
exceptions escape:

```python
from condux.integrations.wsgi import ConduxWsgiMiddleware
application = ConduxWsgiMiddleware(my_wsgi_app)
```

## Develop

```bash
pip install -e .
python -m unittest discover -s tests
```

Zero runtime dependencies (standard library only). The transport and sleep are injectable, so the tests
exercise the retry/backoff with no real network or timers.
