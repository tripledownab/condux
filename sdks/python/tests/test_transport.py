"""Transport/backoff tests for the Condux Python SDK — deterministic, no real waiting.

Mirrors the JS SDK's transport suite: a scripted transport replays a sequence of
(status, headers) tuples (or raises for a network error), and a recording sleep captures the
requested backoff delays instead of sleeping.
"""

import unittest

from condux import SendResult, send_event

ENDPOINT = "http://relay.test/api/1/store/"
HEADERS = {"x-condux-auth": "devkey"}


def scripted(steps):
    """A transport replaying `steps` — each a (status, headers) tuple or an Exception to raise.
    Past the end it repeats the last step. Records every call as (url, body)."""
    calls = []

    def transport(url, headers, body):
        calls.append((url, body))
        step = steps[min(len(calls) - 1, len(steps) - 1)]
        if isinstance(step, Exception):
            raise step
        return step

    return transport, calls


def recording_sleep():
    """A sleep that records requested millisecond delays instead of waiting."""
    delays = []
    return (lambda ms: delays.append(ms)), delays


class TransportTests(unittest.TestCase):
    def test_delivers_on_first_2xx_without_sleeping(self):
        transport, calls = scripted([(202, {})])
        sleep, delays = recording_sleep()

        result = send_event(ENDPOINT, HEADERS, b"{}", transport=transport, sleep=sleep)

        self.assertEqual(result, SendResult(ok=True, attempts=1, status=202))
        self.assertEqual(len(calls), 1)
        self.assertEqual(delays, [])

    def test_retries_429_honoring_retry_after_then_succeeds(self):
        transport, calls = scripted([(429, {"Retry-After": "2"}), (202, {})])
        sleep, delays = recording_sleep()

        result = send_event(ENDPOINT, HEADERS, b"{}", transport=transport, sleep=sleep)

        self.assertTrue(result.ok)
        self.assertEqual(result.attempts, 2)
        self.assertEqual(len(calls), 2)
        self.assertEqual(delays, [2000.0])  # waited exactly Retry-After seconds

    def test_429_without_retry_after_uses_exponential_backoff(self):
        transport, _ = scripted([(429, {}), (202, {})])
        sleep, delays = recording_sleep()

        result = send_event(ENDPOINT, HEADERS, b"{}", transport=transport, sleep=sleep)

        self.assertTrue(result.ok)
        self.assertEqual(delays, [200.0])

    def test_stops_immediately_on_non_retriable_4xx(self):
        transport, calls = scripted([(401, {})])
        sleep, delays = recording_sleep()

        result = send_event(ENDPOINT, HEADERS, b"{}", max_retries=3, transport=transport, sleep=sleep)

        self.assertEqual(result, SendResult(ok=False, attempts=1, status=401))
        self.assertEqual(len(calls), 1)  # did not retry
        self.assertEqual(delays, [])

    def test_gives_up_after_retries_on_persistent_5xx(self):
        transport, calls = scripted([(503, {})])
        sleep, delays = recording_sleep()

        result = send_event(ENDPOINT, HEADERS, b"{}", max_retries=2, transport=transport, sleep=sleep)

        self.assertFalse(result.ok)
        self.assertEqual(result.status, 503)
        self.assertEqual(result.attempts, 3)  # 1 initial + 2 retries
        self.assertEqual(len(calls), 3)
        self.assertEqual(delays, [200.0, 400.0])  # slept between the 3 attempts

    def test_retries_network_errors_and_never_raises(self):
        transport, calls = scripted([ConnectionError("ECONNREFUSED")])
        sleep, _ = recording_sleep()

        result = send_event(ENDPOINT, HEADERS, b"{}", max_retries=1, transport=transport, sleep=sleep)

        self.assertFalse(result.ok)
        self.assertIsNone(result.status)
        self.assertEqual(result.error, "ECONNREFUSED")
        self.assertEqual(len(calls), 2)  # initial + 1 retry


if __name__ == "__main__":
    unittest.main()
