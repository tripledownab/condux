# condux (Python SDK)

Report errors from a Python app to a Condux relay. Emits the Sentry "store" wire shape, so the relay
normalizes it exactly like an official Sentry SDK — point it at a project DSN and it works.

Delivery is resilient (429 / 5xx / network failures retry with backoff, honoring `Retry-After`) and
**never raises** — a failed send returns a `SendResult`, it does not crash the host app.

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
