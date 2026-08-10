"""Tests for the WSGI + ASGI framework integrations.

Protocol-level, so a fake WSGI/ASGI app that raises exercises the middleware without Flask/FastAPI. Assert
the middleware reports the exception as unhandled (via a recording transport) and re-raises it, and that a
successful request or non-HTTP scope passes straight through.
"""

import asyncio
import json
import unittest

import condux
from condux.integrations.asgi import ConduxAsgiMiddleware
from condux.integrations.wsgi import ConduxWsgiMiddleware

DSN = "http://pub123@relay.test/7"


def recording_transport():
    sent = []

    def transport(url, headers, body):
        sent.append(body)
        return 202, {}

    return transport, sent, (lambda: json.loads(sent[-1]))


class WsgiTests(unittest.TestCase):
    def test_reports_uncaught_exception_as_unhandled_and_reraises(self):
        transport, _sent, last = recording_transport()
        condux.init(DSN, transport=transport)

        def app(environ, start_response):
            raise RuntimeError("wsgi boom")

        wrapped = ConduxWsgiMiddleware(app)
        with self.assertRaises(RuntimeError):
            wrapped({}, lambda *a: None)

        exception = last()["exception"]["values"][0]
        self.assertEqual(exception["value"], "wsgi boom")
        self.assertFalse(exception["mechanism"]["handled"])

    def test_passes_a_successful_response_through_and_reports_nothing(self):
        transport, sent, _last = recording_transport()
        condux.init(DSN, transport=transport)

        def app(environ, start_response):
            return [b"ok"]

        self.assertEqual(ConduxWsgiMiddleware(app)({}, lambda *a: None), [b"ok"])
        self.assertEqual(sent, [])


class AsgiTests(unittest.TestCase):
    def test_reports_uncaught_exception_as_unhandled_and_reraises(self):
        transport, _sent, last = recording_transport()
        condux.init(DSN, transport=transport)

        async def app(scope, receive, send):
            raise RuntimeError("asgi boom")

        wrapped = ConduxAsgiMiddleware(app)
        with self.assertRaises(RuntimeError):
            asyncio.run(wrapped({"type": "http"}, None, None))

        exception = last()["exception"]["values"][0]
        self.assertEqual(exception["value"], "asgi boom")
        self.assertFalse(exception["mechanism"]["handled"])

    def test_non_http_scope_passes_through_untouched(self):
        transport, sent, _last = recording_transport()
        condux.init(DSN, transport=transport)
        seen = []

        async def app(scope, receive, send):
            seen.append(scope["type"])

        asyncio.run(ConduxAsgiMiddleware(app)({"type": "lifespan"}, None, None))
        self.assertEqual(seen, ["lifespan"])
        self.assertEqual(sent, [])
