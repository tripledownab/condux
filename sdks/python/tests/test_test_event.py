"""Tests for `python -m condux.test_event`.

The exit code is the contract a CI script branches on, so assert the code, not just the output.
Delivery runs against a real local HTTP server: the command's whole purpose is proving the actual
transport works, and a fake transport here would test nothing the rest of the suite does not.
"""

import http.server
import threading
import unittest
from unittest import mock

from condux.test_event import run


class RelayStub(http.server.BaseHTTPRequestHandler):
    def do_POST(self):  # noqa: N802 - BaseHTTPRequestHandler's naming
        self.rfile.read(int(self.headers.get("content-length", 0)))
        self.send_response(200)
        self.end_headers()

    def log_message(self, *args):
        pass  # keep the test output clean


class TestEventCommandTests(unittest.TestCase):
    def test_no_dsn_is_a_usage_error(self):
        # An explicit empty env value, so a developer's real CONDUX_DSN cannot make this pass.
        with mock.patch.dict("os.environ", {"CONDUX_DSN": ""}):
            self.assertEqual(run([]), 2)

    def test_malformed_dsn_is_a_usage_error(self):
        self.assertEqual(run(["--dsn", "not-a-dsn"]), 2)

    def test_unknown_option_is_a_usage_error(self):
        self.assertEqual(run(["--frobnicate"]), 2)

    def test_delivery_to_a_live_relay_exits_zero(self):
        server = http.server.HTTPServer(("127.0.0.1", 0), RelayStub)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            port = server.server_address[1]
            self.assertEqual(run(["--dsn", f"http://key@127.0.0.1:{port}/1"]), 0)
        finally:
            server.shutdown()
            server.server_close()

    def test_a_refused_relay_exits_one(self):
        # Port 9 (discard) refuses immediately, so the retry loop gives up without waiting on a timeout.
        self.assertEqual(run(["--dsn", "http://key@127.0.0.1:9/1", "--message", "nope"]), 1)


if __name__ == "__main__":
    unittest.main()
