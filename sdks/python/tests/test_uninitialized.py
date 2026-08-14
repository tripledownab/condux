"""Capture before init must never raise.

This is the sharpest edge in the whole SDK: the WSGI/ASGI middleware call capture_exception *inside* an
`except` block before re-raising, so an SDK that raised when uninitialized would replace the
application's own exception with the SDK's. An install that forgot init would then corrupt every error
it was meant to observe. Capture warns once and drops the event instead.
"""

import asyncio
import unittest
import warnings

import condux
from condux.integrations.asgi import ConduxAsgiMiddleware
from condux.integrations.wsgi import ConduxWsgiMiddleware


class UninitializedCaptureTests(unittest.TestCase):
    def setUp(self):
        # Module-level state, set explicitly so this holds whatever order the suite runs in.
        condux._options = None
        condux._warned_uninitialized = False

    def tearDown(self):
        condux._options = None
        condux._warned_uninitialized = False

    def test_capture_message_returns_a_failure_instead_of_raising(self):
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            result = condux.capture_message("dropped")

        self.assertFalse(result.ok)
        self.assertEqual(result.attempts, 0)
        self.assertEqual(result.error, "not_initialized")

    def test_capture_exception_returns_a_failure_instead_of_raising(self):
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            result = condux.capture_exception(ValueError("boom"))

        self.assertFalse(result.ok)
        self.assertEqual(result.error, "not_initialized")

    def test_warns_once_however_many_events_are_dropped(self):
        with warnings.catch_warnings(record=True) as recorded:
            warnings.simplefilter("always")
            condux.capture_message("first")
            condux.capture_message("second")
            condux.capture_exception(ValueError("third"))

        self.assertEqual(len(recorded), 1)
        self.assertIn("capture called before init", str(recorded[0].message))

    def test_wsgi_middleware_propagates_the_apps_own_exception(self):
        def app(environ, start_response):
            raise KeyError("the app's own failure")

        wrapped = ConduxWsgiMiddleware(app)
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            with self.assertRaises(KeyError) as caught:
                wrapped({}, lambda *a: None)

        self.assertIn("the app's own failure", str(caught.exception))

    def test_asgi_middleware_propagates_the_apps_own_exception(self):
        async def app(scope, receive, send):
            raise KeyError("the app's own failure")

        wrapped = ConduxAsgiMiddleware(app)
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            with self.assertRaises(KeyError) as caught:
                asyncio.run(wrapped({"type": "http"}, None, None))

        self.assertIn("the app's own failure", str(caught.exception))


if __name__ == "__main__":
    unittest.main()
