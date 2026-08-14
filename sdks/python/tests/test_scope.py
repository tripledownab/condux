"""Enrichment-scope tests: the ambient user/tags/contexts/breadcrumbs the relay parses.

The wire keys are asserted field by field, because the scope is only useful if it lands in the exact
shape the relay's parser reads — and because an unenriched event must keep its current shape exactly
(no new keys), which a shape-agnostic test would not catch.
"""

import json
import unittest

import condux


def recording_transport():
    sent = []

    def transport(url, headers, body):
        sent.append(body)
        return 202, {}

    return transport, (lambda: json.loads(sent[-1]))


class ScopeTests(unittest.TestCase):
    def setUp(self):
        condux.clear_scope()
        self.transport, self.last = recording_transport()
        condux.init("http://pub123@relay.test/7", transport=self.transport)

    def tearDown(self):
        # Module-level state: leaking it would enrich (and so break) every later test's wire assertions.
        condux.clear_scope()

    def test_unenriched_event_carries_none_of_the_scope_keys(self):
        condux.capture_message("plain")

        event = self.last()
        for key in ("user", "tags", "contexts", "breadcrumbs"):
            self.assertNotIn(key, event)

    def test_enriched_event_carries_the_sentry_wire_shape(self):
        condux.set_user({"id": "42", "email": "dev@example.test"})
        condux.set_tag("plan", "team")
        condux.set_context("device", {"model": "laptop", "cores": 8})
        condux.add_breadcrumb("/checkout", category="navigation", level="info")

        condux.capture_message("after enrichment")

        event = self.last()
        self.assertEqual(event["user"], {"id": "42", "email": "dev@example.test"})
        self.assertEqual(event["tags"], {"plan": "team"})
        self.assertEqual(event["contexts"], {"device": {"model": "laptop", "cores": 8}})
        # Breadcrumbs ride the Sentry {"values": [...]} envelope, not a bare array.
        crumbs = event["breadcrumbs"]["values"]
        self.assertEqual(len(crumbs), 1)
        self.assertEqual(crumbs[0]["message"], "/checkout")
        self.assertEqual(crumbs[0]["category"], "navigation")
        self.assertEqual(crumbs[0]["level"], "info")
        self.assertIsInstance(crumbs[0]["timestamp"], float)  # epoch seconds, stamped for you

    def test_scope_rides_an_exception_capture_too(self):
        condux.set_tag("plan", "business")

        try:
            raise ValueError("boom")
        except ValueError as error:
            condux.capture_exception(error)

        event = self.last()
        self.assertEqual(event["tags"], {"plan": "business"})
        self.assertEqual(event["exception"]["values"][0]["type"], "ValueError")

    def test_none_clears_user_tag_and_context(self):
        condux.set_user({"id": "42"})
        condux.set_tag("plan", "team")
        condux.set_context("device", {"model": "laptop"})

        condux.set_user(None)
        condux.set_tag("plan", None)
        condux.set_context("device", None)
        condux.capture_message("after clearing")

        event = self.last()
        for key in ("user", "tags", "contexts"):
            self.assertNotIn(key, event)

    def test_breadcrumb_trail_is_capped_dropping_the_oldest(self):
        for index in range(35):
            condux.add_breadcrumb(f"step-{index}")

        condux.capture_message("after many steps")

        crumbs = self.last()["breadcrumbs"]["values"]
        self.assertEqual(len(crumbs), 30)
        # Newest last, oldest dropped: steps 0-4 are gone.
        self.assertEqual(crumbs[0]["message"], "step-5")
        self.assertEqual(crumbs[-1]["message"], "step-34")

    def test_clear_scope_removes_everything(self):
        condux.set_user({"id": "42"})
        condux.set_tag("plan", "team")
        condux.add_breadcrumb("/checkout")

        condux.clear_scope()
        condux.capture_message("after sign out")

        event = self.last()
        for key in ("user", "tags", "contexts", "breadcrumbs"):
            self.assertNotIn(key, event)


if __name__ == "__main__":
    unittest.main()
