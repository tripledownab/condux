"""Framework integrations for the Condux SDK.

Each reports an app's uncaught request exceptions to Condux (as unhandled) and lets the framework's own
error handling carry on, and isolates enrichment to the request it happened in.

Two shapes, and which one a framework needs is not a style choice:

* ``flask`` and ``django`` hook the signal each framework fires. **They are required for those two**,
  because both catch a view exception, convert it to a 500 and return it, so nothing propagates out of
  the application and a middleware wrapping it from the outside sees nothing at all.
* ``wsgi`` and ``asgi`` wrap the application, which is correct for a bare WSGI or ASGI app and for a
  framework that lets exceptions escape (Starlette re-raises after responding, so FastAPI works).

Importing this package pulls in no framework: each integration imports its own lazily, so ``import
condux`` never requires Flask or Django to be installed.
"""

from condux.integrations.asgi import ConduxAsgiMiddleware
from condux.integrations.django import ConduxMiddleware
from condux.integrations.flask import ConduxFlask
from condux.integrations.wsgi import ConduxWsgiMiddleware

__all__ = ["ConduxAsgiMiddleware", "ConduxFlask", "ConduxMiddleware", "ConduxWsgiMiddleware"]
