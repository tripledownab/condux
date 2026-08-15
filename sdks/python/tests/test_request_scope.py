"""Request isolation and per-event enrichment.

The point of both is that a server handles requests concurrently. These tests run real concurrency
(threads for WSGI, an event loop for ASGI) rather than asserting the mechanism in the abstract, because
the bug they exist to prevent only appears when two requests overlap.
"""

from __future__ import annotations

import asyncio
import json
import threading
import unittest

import condux
from condux.integrations.asgi import ConduxAsgiMiddleware
from condux.integrations.wsgi import ConduxWsgiMiddleware

DSN = "http://pub123@relay.test/7"


class RecordingTransport:
    """Captures every event body, so a test can inspect exactly what reached the relay."""

    def __init__(self) -> None:
        self.bodies: list = []
        self._lock = threading.Lock()

    def __call__(self, url, headers, body):
        with self._lock:
            self.bodies.append(json.loads(body.decode()))
        return 200, {}

    def events(self):
        with self._lock:
            return list(self.bodies)


class RequestScopeTest(unittest.TestCase):
    def setUp(self) -> None:
        self.transport = RecordingTransport()
        condux.init(DSN, transport=self.transport, sleep=lambda ms: None)
        condux.clear_scope()

    def tearDown(self) -> None:
        condux.clear_scope()

    def test_process_scope_still_applies_when_no_request_is_active(self):
        # Backward compatibility: setting the scope at startup must behave exactly as it did before
        # request scopes existed, or every existing install changes meaning on upgrade.
        condux.set_tag("service", "billing")
        condux.capture_message("hello")
        self.assertEqual(self.transport.events()[-1]["tags"], {"service": "billing"})

    def test_request_scope_layers_over_process_scope(self):
        condux.set_tag("service", "billing")
        with condux.request_scope():
            condux.set_tag("tenant", "acme")
            condux.capture_message("inside")
        self.assertEqual(
            self.transport.events()[-1]["tags"], {"service": "billing", "tenant": "acme"}
        )

    def test_request_scope_does_not_outlive_the_request(self):
        with condux.request_scope():
            condux.set_user({"id": "u-1"})
        condux.capture_message("after")
        # The leak in miniature: without the reset, the next event carries the previous request's user.
        self.assertNotIn("user", self.transport.events()[-1])

    def test_concurrent_threads_do_not_see_each_others_user(self):
        """The real failure: two requests in flight on pooled threads."""
        started = threading.Barrier(2)

        def handle(user_id: str) -> None:
            with condux.request_scope():
                condux.set_user({"id": user_id})
                # Both threads set their user before either captures, so a shared scope means whichever
                # wrote last wins for BOTH events. Sequential execution would hide this entirely.
                started.wait()
                condux.capture_message(user_id)

        threads = [threading.Thread(target=handle, args=(f"u-{i}",)) for i in (1, 2)]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()

        by_message = {event["message"]: event["user"]["id"] for event in self.transport.events()}
        self.assertEqual(by_message, {"u-1": "u-1", "u-2": "u-2"})

    def test_concurrent_asyncio_tasks_do_not_see_each_others_user(self):
        async def handle(user_id: str, gate: asyncio.Event) -> None:
            with condux.request_scope():
                condux.set_user({"id": user_id})
                # Yield after setting and before capturing, so the two tasks genuinely interleave.
                gate.set()
                await asyncio.sleep(0)
                condux.capture_message(user_id)

        async def main() -> None:
            gate = asyncio.Event()
            await asyncio.gather(handle("a-1", gate), handle("a-2", gate))

        asyncio.run(main())

        by_message = {event["message"]: event["user"]["id"] for event in self.transport.events()}
        self.assertEqual(by_message, {"a-1": "a-1", "a-2": "a-2"})


class AdapterRequestTest(unittest.TestCase):
    def setUp(self) -> None:
        self.transport = RecordingTransport()
        condux.init(DSN, transport=self.transport, sleep=lambda ms: None)
        condux.clear_scope()

    def test_wsgi_reports_the_request_and_never_the_headers(self):
        def app(environ, start_response):
            raise ValueError("boom")

        environ = {
            "PATH_INFO": "/checkout",
            "REQUEST_METHOD": "POST",
            "QUERY_STRING": "step=2",
            "HTTP_COOKIE": "session=supersecret",
            "HTTP_AUTHORIZATION": "Bearer tok",
        }
        with self.assertRaises(ValueError):
            ConduxWsgiMiddleware(app)(environ, lambda *a: None)

        event = self.transport.events()[-1]
        self.assertEqual(
            event["request"], {"url": "/checkout", "method": "POST", "query_string": "step=2"}
        )

        # The SDK's own middleware frame holds `environ`, which carries every header. It must contribute
        # no locals and must not be in-app: it is monitoring plumbing, not the user's code. Without this
        # the credentials ship inside the stack trace even though the request field excludes them.
        sdk_frames = [
            frame
            for frame in event["exception"]["values"][0]["stacktrace"]["frames"]
            if "condux/integrations" in frame["filename"]
        ]
        self.assertTrue(sdk_frames, "expected the middleware frame in the trace")
        for frame in sdk_frames:
            self.assertNotIn("vars", frame)
            self.assertFalse(frame["in_app"])
        self.assertNotIn("supersecret", json.dumps(sdk_frames))

    def test_asgi_reports_the_request_decoding_the_query_bytes(self):
        async def app(scope, receive, send):
            raise ValueError("boom")

        scope = {
            "type": "http",
            "path": "/api/sync",
            "method": "GET",
            "query_string": b"since=2026",
            "headers": [(b"cookie", b"session=supersecret")],
        }

        async def run():
            await ConduxAsgiMiddleware(app)(scope, None, None)

        with self.assertRaises(ValueError):
            asyncio.run(run())

        event = self.transport.events()[-1]
        self.assertEqual(
            event["request"], {"url": "/api/sync", "method": "GET", "query_string": "since=2026"}
        )
        sdk_frames = [
            frame
            for frame in event["exception"]["values"][0]["stacktrace"]["frames"]
            if "condux/integrations" in frame["filename"]
        ]
        self.assertTrue(sdk_frames, "expected the middleware frame in the trace")
        # `scope` is this frame's local and carries the headers, so its absence is the whole point.
        self.assertNotIn("supersecret", json.dumps(sdk_frames))

    def test_the_application_exception_still_propagates(self):
        """The adapters capture inside an except before re-raising, so reporting must never replace it."""

        def app(environ, start_response):
            raise KeyError("original")

        with self.assertRaises(KeyError):
            ConduxWsgiMiddleware(app)({"PATH_INFO": "/x"}, lambda *a: None)


if __name__ == "__main__":
    unittest.main()
