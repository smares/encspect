#!/usr/bin/env python3
"""Tests of the retry decision of downloads.py: python3 scripts/ci/test_downloads.py"""

import pathlib
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "fixtures"))
import downloads  # noqa: E402

ARCHIVE = "https://web.archive.org/web/20010611205504id_/http://example.invalid/a.tar.gz"
OTHER = "https://example.invalid/a.tar.gz"


def http(status):
    return 22, f"curl: (22) The requested URL returned error: {status}"


class RetryDelayTests(unittest.TestCase):
    def test_archive_retries_404_429_and_5xx_with_growing_delays(self):
        for status in (404, 429, 500, 502, 503, 504):
            self.assertEqual([downloads.retry_delay(ARCHIVE, http(status), n) for n in range(4)], [10, 30, 60, None], status)

    def test_archive_retries_connection_errors(self):
        self.assertEqual(downloads.retry_delay(ARCHIVE, (7, "curl: (7) Failed to connect"), 0), 10)
        self.assertEqual(downloads.retry_delay(ARCHIVE, (28, "curl: (28) Operation timed out"), 1), 30)

    def test_archive_does_not_retry_other_statuses(self):
        for status in (301, 400, 401, 403, 410):
            self.assertIsNone(downloads.retry_delay(ARCHIVE, http(status), 0), status)

    def test_other_hosts_retry_transient_failures_with_longer_delays(self):
        for code, message in ((35, "curl: (35) Recv failure: Connection reset by peer"), (28, "curl: (28) Connection timed out after 30001 milliseconds"),
                              (7, "curl: (7) Failed to connect"), (6, "curl: (6) Could not resolve host"), (18, "curl: (18) transfer closed with outstanding read data remaining"),
                              (52, "curl: (52) Empty reply from server"), (56, "curl: (56) Recv failure: Connection reset by peer")):
            self.assertEqual([downloads.retry_delay(OTHER, (code, message), n) for n in range(4)], [30, 90, 180, None], code)

    def test_other_hosts_retry_429_and_5xx_only(self):
        for status in (429, 500, 502, 503, 504):
            self.assertEqual(downloads.retry_delay(OTHER, http(status), 0), 30, status)
        for status in (301, 400, 401, 403, 404, 410):
            self.assertIsNone(downloads.retry_delay(OTHER, http(status), 0), status)

    def test_other_hosts_do_not_retry_definite_failures(self):
        self.assertIsNone(downloads.retry_delay(OTHER, (60, "curl: (60) SSL certificate problem"), 0))
        self.assertIsNone(downloads.retry_delay(OTHER, (3, "curl: (3) URL rejected: Malformed input to a URL function"), 0))
        self.assertIsNone(downloads.retry_delay(OTHER, (23, "curl: (23) Failure writing output to destination"), 0))

    def test_waiting_is_bounded(self):
        self.assertIsNone(downloads.retry_delay(ARCHIVE, http(404), 0, downloads.RETRY_BUDGET - 9))
        self.assertEqual(downloads.retry_delay(ARCHIVE, http(404), 0, downloads.RETRY_BUDGET - 10), 10)
        self.assertIsNone(downloads.retry_delay(OTHER, http(503), 0, downloads.RETRY_BUDGET - 29))
        self.assertEqual(downloads.retry_delay(OTHER, http(503), 0, downloads.RETRY_BUDGET - 30), 30)


class DownloadTests(unittest.TestCase):
    def setUp(self):
        patch = mock.patch.object(downloads, "_waited", 0)
        patch.start()
        self.addCleanup(patch.stop)

    def run_download(self, url, results):
        sleeps = []
        with mock.patch.object(downloads, "_curl", side_effect=results) as curl:
            error = downloads.download(url, pathlib.Path("unused"), sleep=sleeps.append)
        return error, sleeps, curl.call_count

    def test_archive_succeeds_after_404s(self):
        error, sleeps, calls = self.run_download(ARCHIVE, [http(404), http(404), None])
        self.assertIsNone(error)
        self.assertEqual((sleeps, calls), ([10, 30], 3))

    def test_archive_fails_after_the_last_retry_naming_the_error(self):
        error, sleeps, calls = self.run_download(ARCHIVE, [http(404)] * 4)
        self.assertIn("404", error)
        self.assertEqual((sleeps, calls), ([10, 30, 60], 4))

    RESET = (35, "curl: (35) Recv failure: Connection reset by peer")

    def test_other_host_succeeds_after_connection_resets(self):
        error, sleeps, calls = self.run_download(OTHER, [self.RESET, self.RESET, None])
        self.assertIsNone(error)
        self.assertEqual((sleeps, calls), ([30, 90], 3))

    def test_other_host_fails_after_the_last_retry_naming_the_error(self):
        error, sleeps, calls = self.run_download(OTHER, [self.RESET] * 4)
        self.assertIn("Connection reset", error)
        self.assertEqual((sleeps, calls), ([30, 90, 180], 4))

    def test_other_host_404_is_tried_once(self):
        error, sleeps, calls = self.run_download(OTHER, [http(404)])
        self.assertIn("404", error)
        self.assertEqual((sleeps, calls), ([], 1))

    def test_the_budget_is_shared_by_all_downloads_of_a_run(self):
        for _ in range(3):
            self.run_download(OTHER, [self.RESET] * 4)
        self.assertEqual(downloads._waited, downloads.RETRY_BUDGET)
        error, sleeps, calls = self.run_download(OTHER, [self.RESET])
        self.assertIn("Connection reset", error)
        self.assertEqual((sleeps, calls), ([], 1))


if __name__ == "__main__":
    unittest.main()
