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

## Framework integrations

WSGI (Flask, Django) and ASGI (FastAPI, Starlette) middleware report uncaught exceptions
(`handled=False`) and re-raise:

```python
# WSGI
from condux.integrations.wsgi import ConduxWsgiMiddleware
app.wsgi_app = ConduxWsgiMiddleware(app.wsgi_app)

# ASGI
from condux.integrations.asgi import ConduxAsgiMiddleware
app = ConduxAsgiMiddleware(app)
```

## Develop

```bash
pip install -e .
python -m unittest discover -s tests
```

Zero runtime dependencies (standard library only). The transport and sleep are injectable, so the tests
exercise the retry/backoff with no real network or timers.
