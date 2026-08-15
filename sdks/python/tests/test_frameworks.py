"""End to end against the REAL frameworks, not a stand-in that raises on cue.

This file exists because every other adapter test in this suite drives the middleware with a fake app:

    def app(environ, start_response):
        raise ValueError("boom")

That construction assumes the exception escapes the application, which is the very thing that is false
for Flask and Django: both handle it internally and return a 500. The tests encoded the same wrong
assumption as the code, so they passed for months while the WSGI integration reported nothing at all for
the two frameworks its own docstring named.

So these run the actual framework. A test here fails if the integration stops reporting, which is the
only property that matters and the one no fake app can check.

Each framework is skipped when it is not installed, so a contributor without the extras still gets a
green suite; CI installs them, which is where the gate actually bites.
"""

from __future__ import annotations

import json
import threading
import unittest

import condux

DSN = "http://publickey@relay.test/7"


class Recorder:
    """Captures every event body. Thread-safe: the concurrency cases report from several threads."""

    def __init__(self) -> None:
        self.events: list = []
        self._lock = threading.Lock()

    def __call__(self, url, headers, body):
        with self._lock:
            self.events.append(json.loads(body.decode()))
        return 200, {}

    def only(self) -> dict:
        with self._lock:
            assert len(self.events) == 1, f"expected exactly one event, got {len(self.events)}"
            return self.events[0]


def _skip_without(module: str):
    try:
        __import__(module)
    except ImportError:  # pragma: no cover - depends on the environment
        return unittest.skip(f"{module} is not installed")
    return lambda test: test


@_skip_without("flask")
class FlaskIntegrationTest(unittest.TestCase):
    def setUp(self) -> None:
        condux.clear_scope()

    def test_reports_an_uncaught_view_exception(self):
        from flask import Flask

        from condux.integrations.flask import ConduxFlask

        recorder = Recorder()
        condux.init(DSN, transport=recorder, sleep=lambda ms: None)

        app = Flask(__name__)

        @app.route("/checkout/<item>")
        def checkout(item):
            condux.set_user({"id": item})
            raise ValueError(f"checkout failed for {item}")

        # Bare statement, exactly as documented. blinker holds handlers weakly by default, so if the
        # integration ever stops passing weak=False the object is collected and every handler silently
        # disconnects. Nothing else in the suite would notice.
        ConduxFlask(app)
        import gc

        gc.collect()

        with app.test_client() as client:
            self.assertEqual(client.get("/checkout/widget").status_code, 500)

        event = recorder.only()
        exception = event["exception"]["values"][0]
        self.assertEqual(exception["type"], "ValueError")
        self.assertIs(exception["mechanism"]["handled"], False)
        self.assertEqual(event["request"]["url"], "/checkout/widget")
        self.assertEqual(event["request"]["method"], "GET")
        # The parameterised rule, not the concrete path, so every instance of a route groups together.
        self.assertEqual(event["tags"]["route"], "/checkout/<item>")
        self.assertEqual(event["user"]["id"], "widget")

    def test_a_successful_request_reports_nothing(self):
        from flask import Flask

        from condux.integrations.flask import ConduxFlask

        recorder = Recorder()
        condux.init(DSN, transport=recorder, sleep=lambda ms: None)

        app = Flask(__name__)

        @app.route("/ok")
        def ok():
            return "fine"

        ConduxFlask(app)
        with app.test_client() as client:
            self.assertEqual(client.get("/ok").status_code, 200)

        self.assertEqual(recorder.events, [])

    def test_the_request_scope_does_not_leak_between_requests(self):
        from flask import Flask

        from condux.integrations.flask import ConduxFlask

        recorder = Recorder()
        condux.init(DSN, transport=recorder, sleep=lambda ms: None)

        app = Flask(__name__)

        @app.route("/who/<name>")
        def who(name):
            if name != "anonymous":
                condux.set_user({"id": name})
            raise ValueError(f"failed for {name}")

        ConduxFlask(app)
        with app.test_client() as client:
            client.get("/who/alice")
            client.get("/who/anonymous")

        # The second request set no user, so it must carry none. Inheriting alice would be the leak.
        self.assertEqual(recorder.events[0]["user"]["id"], "alice")
        self.assertNotIn("user", recorder.events[1])

        # Deliberately NOT asserting that the scope was closed. Flask runs each request in an isolated
        # context, so the contextvar reads None afterwards whether or not the teardown ran: measured by
        # neutering scope.__exit__, which changes nothing observable here, through test_client and
        # through a direct wsgi_app call alike. An assertion on it would be green and worthless.
        #
        # The teardown stays because that isolation is Flask's behaviour rather than a guarantee this
        # integration makes, and a scope left open on a pooled worker would be a real leak. Its effect
        # is simply not observable from this side of the WSGI call.


@_skip_without("django")
class DjangoIntegrationTest(unittest.TestCase):
    def test_reports_an_uncaught_view_exception(self):
        import io

        import django
        from django.conf import settings

        if not settings.configured:
            settings.configure(
                DEBUG=False,
                ALLOWED_HOSTS=["*"],
                ROOT_URLCONF=__name__,
                SECRET_KEY="test-only",
                LOGGING_CONFIG=None,
                MIDDLEWARE=["condux.integrations.django.ConduxMiddleware"],
            )
            django.setup()

        from django.core.handlers.wsgi import WSGIHandler

        recorder = Recorder()
        condux.init(DSN, transport=recorder, sleep=lambda ms: None)
        condux.clear_scope()

        # Django holds signal receivers weakly, so this pins that reporting survives a collection. It
        # does, because Django retains the middleware chain, but that is a property of the framework
        # rather than of our code and it should fail loudly if it ever changes.
        import gc

        handler = WSGIHandler()
        gc.collect()

        status = []
        handler(
            {
                "REQUEST_METHOD": "GET",
                "PATH_INFO": "/checkout/widget",
                "QUERY_STRING": "step=2",
                "SERVER_NAME": "test",
                "SERVER_PORT": "80",
                "SERVER_PROTOCOL": "HTTP/1.1",
                "wsgi.url_scheme": "http",
                "wsgi.input": io.BytesIO(b""),
                "wsgi.errors": io.StringIO(),
                "CONTENT_LENGTH": "0",
                # Django puts headers in the environ, so this pins that they are not reported.
                "HTTP_COOKIE": "session=must-not-be-reported",
            },
            lambda code, headers: status.append(code),
        )

        # Django converts the exception to a 500 rather than letting it escape, which is exactly why a
        # WSGI wrapper cannot see it and this integration hooks the signal instead.
        self.assertEqual(status[0], "500 Internal Server Error")

        event = recorder.only()
        exception = event["exception"]["values"][0]
        self.assertEqual(exception["type"], "ValueError")
        self.assertIs(exception["mechanism"]["handled"], False)
        self.assertEqual(event["request"]["url"], "/checkout/widget")
        self.assertEqual(event["request"]["query_string"], "step=2")
        self.assertEqual(event["tags"]["route"], "checkout/<str:item>")
        self.assertNotIn("must-not-be-reported", json.dumps(event["request"]))


@_skip_without("starlette")
class StarletteIntegrationTest(unittest.TestCase):
    def test_reports_an_uncaught_route_exception(self):
        import asyncio

        from starlette.applications import Starlette
        from starlette.routing import Route

        from condux.integrations.asgi import ConduxAsgiMiddleware

        recorder = Recorder()
        condux.init(DSN, transport=recorder, sleep=lambda ms: None)
        condux.clear_scope()

        async def checkout(request):
            raise ValueError("checkout failed")

        app = ConduxAsgiMiddleware(Starlette(routes=[Route("/checkout", checkout)]))

        async def drive():
            scope = {
                "type": "http",
                "method": "GET",
                "path": "/checkout",
                "query_string": b"step=2",
                "headers": [],
            }

            # Both must be awaitable: Starlette responds with a 500 before re-raising, so it awaits send.
            async def receive():
                return {"type": "http.request", "body": b""}

            async def send(message):
                return None

            with self.assertRaises(ValueError):
                await app(scope, receive, send)

        asyncio.run(drive())

        event = recorder.only()
        self.assertEqual(event["exception"]["values"][0]["type"], "ValueError")
        self.assertEqual(event["request"]["url"], "/checkout")
        self.assertEqual(event["request"]["query_string"], "step=2")


# Django resolves ROOT_URLCONF against this module, so the view and patterns live here.
def _django_checkout(request, item):
    condux.set_user({"id": item})
    raise ValueError(f"checkout failed for {item}")


try:  # pragma: no cover - only meaningful when Django is installed
    from django.urls import path

    urlpatterns = [path("checkout/<str:item>", _django_checkout, name="checkout")]
except ImportError:
    urlpatterns = []


if __name__ == "__main__":
    unittest.main()
