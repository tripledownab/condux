"""Runtime dependency inventory tests (ADR-0041).

The wire key is asserted directly, because the relay reads a top-level ``modules`` string map and a
renamed or nested key would be dropped silently by the parser rather than rejected. And because an
event that carries no inventory must keep its exact previous shape, which a shape-agnostic test would
not catch.
"""

import json
import unittest

import condux
from condux.modules import (
    MAX_MODULES,
    MODULES_INTERVAL_SECONDS,
    collect_modules,
    modules_field,
    set_modules,
)

DSN = "http://pub123@relay.test/7"


def recording_transport():
    sent = []

    def transport(url, headers, body):
        sent.append(body)
        return 202, {}

    return transport, (lambda: [json.loads(body) for body in sent])


class ModulesTests(unittest.TestCase):
    def setUp(self):
        condux.clear_modules()

    def test_declared_modules_ride_the_wire_under_the_sentry_key(self):
        transport, events = recording_transport()
        condux.init(DSN, transport=transport, send_modules=False)
        set_modules({"requests": "2.19.0", "flask": "2.0.0"})

        condux.capture_exception(ValueError("boom"))

        self.assertEqual(
            {"flask": "2.0.0", "requests": "2.19.0"}, events()[0]["modules"]
        )

    def test_an_event_carries_no_modules_key_when_none_were_declared(self):
        transport, events = recording_transport()
        condux.init(DSN, transport=transport, send_modules=False)

        condux.capture_exception(ValueError("boom"))

        self.assertNotIn("modules", events()[0])

    def test_the_inventory_rides_the_first_event_then_waits_out_the_interval(self):
        """What makes the feature affordable.

        The server deduplicates a release's inventory to one row per package per day, so repeating the
        map on every event of a busy project spends bytes for nothing. Remove the interval and this
        still passes on the first event but fails on the second.
        """
        transport, events = recording_transport()
        condux.init(DSN, transport=transport, send_modules=False)
        set_modules({"requests": "2.19.0"})

        condux.capture_exception(ValueError("one"))
        condux.capture_exception(ValueError("two"))
        condux.capture_exception(ValueError("three"))

        with_modules = [e for e in events() if "modules" in e]
        self.assertEqual(1, len(with_modules))

    def test_the_inventory_rides_again_once_the_interval_elapses(self):
        """Repeating matters as much as skipping.

        The event carrying the inventory can be dropped by a rate limit or a quota rejection before
        anything parses it, so one attempt per process would lose that day's inventory outright.
        """
        set_modules({"requests": "2.19.0"})
        start = 1_000_000.0

        self.assertIn("modules", modules_field(start))
        self.assertNotIn("modules", modules_field(start + MODULES_INTERVAL_SECONDS - 1))
        self.assertIn("modules", modules_field(start + MODULES_INTERVAL_SECONDS))

    def test_a_fresh_declaration_does_not_wait_out_the_previous_interval(self):
        set_modules({"requests": "2.19.0"})
        start = 1_000_000.0
        self.assertIn("modules", modules_field(start))

        set_modules({"requests": "2.32.0"})

        self.assertEqual({"requests": "2.32.0"}, modules_field(start + 1)["modules"])

    def test_entries_are_sorted_and_capped_so_every_event_carries_the_same_set(self):
        # Zero padded so lexicographic order is also numeric order, making the survivors predictable.
        set_modules({f"pkg-{i:05d}": "1.0.0" for i in range(MAX_MODULES + 50)})

        names = list(modules_field(1.0)["modules"])

        self.assertEqual(MAX_MODULES, len(names))
        self.assertEqual("pkg-00000", names[0])
        self.assertEqual(sorted(names), names)

    def test_blank_entries_are_dropped_rather_than_reported_as_versions(self):
        set_modules({"good": "1.0.0", "blank": "", "": "2.0.0"})

        self.assertEqual({"good": "1.0.0"}, modules_field(1.0)["modules"])

    def test_clearing_removes_the_key_entirely(self):
        set_modules({"requests": "2.19.0"})
        set_modules(None)

        self.assertEqual({}, modules_field(1.0))

    def test_collect_reads_the_installed_distributions(self):
        """Reads the environment, not a requirements file.

        Asserted against this package's own test environment rather than a fixture, because the point
        is that importlib.metadata sees what is actually installed. Every value must be a non-empty
        string: a None version rendered as "running None" would be worse than no row.
        """
        found = collect_modules()

        self.assertGreater(len(found), 0)
        for name, version in found.items():
            self.assertIsInstance(name, str)
            self.assertIsInstance(version, str)
            self.assertTrue(name and version)

    def test_init_collects_so_the_next_event_carries_the_inventory(self):
        """The two halves joined: collection and carrying are each tested alone above."""
        transport, events = recording_transport()

        condux.init(DSN, transport=transport)
        condux.capture_exception(ValueError("boom"))

        self.assertGreater(len(events()[0]["modules"]), 0)

    def test_send_modules_false_leaves_the_inventory_off_entirely(self):
        transport, events = recording_transport()

        condux.init(DSN, transport=transport, send_modules=False)
        condux.capture_exception(ValueError("boom"))

        self.assertNotIn("modules", events()[0])

    def test_init_opting_out_clears_an_inventory_a_previous_init_collected(self):
        """A re-init must not leave the previous run's inventory attached."""
        transport, events = recording_transport()
        condux.init(DSN, transport=transport)

        condux.init(DSN, transport=transport, send_modules=False)
        condux.capture_exception(ValueError("boom"))

        self.assertNotIn("modules", events()[0])


if __name__ == "__main__":
    unittest.main()
