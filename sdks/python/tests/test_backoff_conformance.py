"""Drives sdks/conformance/backoff.tsv, the retry schedule every Condux SDK owes.

The cases live in that file rather than here so the seven transports assert against one artifact
instead of seven readings of one sentence in a comment. See the file for why it is data and not prose.
"""

import unittest
from pathlib import Path

from condux import send_event

ENDPOINT = "http://relay.test/api/1/store/"
HEADERS = {"x-condux-auth": "devkey"}

FIXTURE = Path(__file__).resolve().parents[2] / "conformance" / "backoff.tsv"


def read_cases():
    """(attempt, status, retry_after or None, expected_ms) for every row in the fixture."""
    cases = []
    for line in FIXTURE.read_text(encoding="utf-8").splitlines():
        text = line.strip()
        if not text or text.startswith("#"):
            continue
        attempt, status, retry_after, expected_ms = text.split("\t")
        if retry_after == "<none>":
            retry_after = None
        elif retry_after == "<empty>":
            retry_after = ""
        cases.append((int(attempt), int(status), retry_after, int(expected_ms)))
    return cases


class BackoffConformanceTests(unittest.TestCase):
    def test_the_fixture_loaded(self):
        """A fixture that failed to load reads exactly like one where every case passed.

        A floor rather than an exact count, so adding a case does not mean editing seven SDKs.
        """
        self.assertGreaterEqual(len(read_cases()), 15, f"{FIXTURE} looks truncated")

    def test_waits_exactly_as_long_as_the_fleet_contract_says(self):
        for attempt, status, retry_after, expected_ms in read_cases():
            with self.subTest(attempt=attempt, status=status, retry_after=retry_after):
                headers = {} if retry_after is None else {"Retry-After": retry_after}
                delays = []

                # One more retry than the attempt under test, so the sleep that follows it is
                # recorded. The scripted response repeats, so every attempt fails.
                send_event(
                    ENDPOINT,
                    HEADERS,
                    b"{}",
                    max_retries=attempt + 1,
                    transport=lambda url, hdrs, body: (status, headers),
                    sleep=delays.append,
                )

                self.assertEqual(len(delays), attempt + 1)
                self.assertEqual(delays[attempt], expected_ms)


if __name__ == "__main__":
    unittest.main()
