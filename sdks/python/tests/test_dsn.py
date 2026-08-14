"""DSN validation tests.

A DSN typo used to parse fine and produce an install that reported nowhere, which is indistinguishable
from an app with no errors. These pin that the malformed cases are refused at init instead.
"""

import unittest

import condux
from condux.dsn import parse_dsn


class ParseDsnTests(unittest.TestCase):
    def test_splits_a_valid_dsn(self):
        self.assertEqual(
            parse_dsn("https://pub123@ingest.condux.ai/0198f-uuid"),
            ("https://ingest.condux.ai", "0198f-uuid", "pub123"),
        )

    def test_keeps_an_explicit_port(self):
        endpoint, project_id, key = parse_dsn("http://pub123@localhost:9010/7")
        self.assertEqual(endpoint, "http://localhost:9010")
        self.assertEqual(project_id, "7")
        self.assertEqual(key, "pub123")

    def test_rejects_a_dsn_without_a_key(self):
        with self.assertRaises(ValueError) as caught:
            parse_dsn("https://ingest.condux.ai/7")
        self.assertIn("public key", str(caught.exception))

    def test_rejects_a_dsn_without_a_project_id(self):
        with self.assertRaises(ValueError) as caught:
            parse_dsn("https://pub123@ingest.condux.ai")
        self.assertIn("project id", str(caught.exception))

    def test_rejects_a_dsn_without_a_scheme(self):
        with self.assertRaises(ValueError) as caught:
            parse_dsn("pub123@ingest.condux.ai/7")
        self.assertIn("http", str(caught.exception))

    def test_rejects_a_non_http_scheme(self):
        with self.assertRaises(ValueError):
            parse_dsn("ftp://pub123@ingest.condux.ai/7")

    def test_rejects_an_invalid_port(self):
        with self.assertRaises(ValueError):
            parse_dsn("http://pub123@localhost:not-a-port/7")

    def test_init_refuses_a_malformed_dsn(self):
        # Loud at setup (developer time), never at capture (production request time).
        with self.assertRaises(ValueError):
            condux.init("https://ingest.condux.ai/7")


if __name__ == "__main__":
    unittest.main()
