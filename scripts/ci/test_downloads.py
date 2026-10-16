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

    def test_other_hosts_are_not_retried(self):
        self.assertIsNone(downloads.retry_delay(OTHER, http(404), 0))
        self.assertIsNone(downloads.retry_delay(OTHER, (7, "curl: (7) Failed to connect"), 0))

    def test_waiting_is_bounded(self):
        self.assertIsNone(downloads.retry_delay(ARCHIVE, http(404), 0, downloads.ARCHIVE_BUDGET - 9))
        self.assertEqual(downloads.retry_delay(ARCHIVE, http(404), 0, downloads.ARCHIVE_BUDGET - 10), 10)


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

    def test_other_host_is_tried_once(self):
        error, sleeps, calls = self.run_download(OTHER, [http(404)])
        self.assertIn("404", error)
        self.assertEqual((sleeps, calls), ([], 1))


if __name__ == "__main__":
    unittest.main()
