"""Event-shape tests for the Condux Python SDK.

The transport suite only checks retry/backoff with a `{}` body; these assert that capture_exception
and capture_message put the Sentry store shape on the wire (the field names the relay parses), which
is what a native-shape regression would break.
"""

import json
import unittest

import condux
from condux import Level


def recording_transport():
    """A transport that records the last request body and reports success."""
    sent = []

    def transport(url, headers, body):
        sent.append(body)
        return 202, {}

    return transport, (lambda: json.loads(sent[-1]))


def boom():
    amount = 42
    raise ValueError(f"cannot charge card for {amount}")


class EventShapeTests(unittest.TestCase):
    def test_capture_exception_emits_sentry_store_shape_with_stack(self):
        transport, last = recording_transport()
        condux.init("http://pub123@relay.test/7", environment="test", release="app@1.2.3", transport=transport)

        try:
            boom()
        except ValueError as error:
            condux.capture_exception(error)

        event = last()
        # Sentry field names — not the old native shape.
        self.assertNotIn("exceptions", event)
        self.assertNotIn("timestamp_unix_ms", event)
        self.assertEqual(event["level"], "error")
        self.assertEqual(event["platform"], "python")
        self.assertEqual(event["environment"], "test")
        self.assertEqual(event["release"], "app@1.2.3")
        self.assertIsInstance(event["timestamp"], float)

        ex = event["exception"]["values"][0]
        self.assertEqual(ex["type"], "ValueError")
        self.assertEqual(ex["value"], "cannot charge card for 42")
        # Handled capture carries the Sentry mechanism (drives the unhandled badge).
        self.assertEqual(ex["mechanism"]["type"], "generic")
        self.assertTrue(ex["mechanism"]["handled"])

        # Frames are oldest-first, so the raising function is last and marked in-app.
        frames = ex["stacktrace"]["frames"]
        self.assertGreater(len(frames), 0)
        raising = frames[-1]
        self.assertEqual(raising["function"], "boom")
        self.assertTrue(raising["in_app"])
        # The code-path fields: surrounding source (this very file) and the frame's locals.
        self.assertIn("raise ValueError", raising["context_line"])
        self.assertGreater(len(raising["pre_context"]), 0)
        self.assertGreater(len(raising["post_context"]), 0)
        self.assertIn("amount", raising["vars"])
        self.assertEqual(raising["vars"]["amount"], "42")

    def test_capture_exception_can_mark_unhandled(self):
        # Framework integrations report uncaught exceptions with handled=False, driving the unhandled badge.
        transport, last = recording_transport()
        condux.init("http://pub123@relay.test/7", transport=transport)

        try:
            boom()
        except ValueError as error:
            condux.capture_exception(error, handled=False)

        self.assertFalse(last()["exception"]["values"][0]["mechanism"]["handled"])

    def test_capture_message_emits_message_event_at_level(self):
        transport, last = recording_transport()
        condux.init("http://pub123@relay.test/7", transport=transport)

        condux.capture_message("nightly job slow", Level.WARNING)

        event = last()
        self.assertEqual(event["message"], "nightly job slow")
        self.assertEqual(event["level"], "warning")
        self.assertNotIn("exception", event)
        self.assertNotIn("environment", event)  # unset → omitted, not null


if __name__ == "__main__":
    unittest.main()
