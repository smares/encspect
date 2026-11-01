#!/usr/bin/env python3
"""Tests of the retry decision of downloads.py: python3 scripts/ci/test_downloads.py"""

import os
import pathlib
import sys
import tempfile
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

    def test_archive_retries_a_certificate_failure_but_other_hosts_do_not(self):
        self.assertEqual(downloads.retry_delay(ARCHIVE, (60, "curl: (60) SSL certificate problem"), 0), 10)
        self.assertIsNone(downloads.retry_delay(OTHER, (60, "curl: (60) SSL certificate problem"), 0))

    def test_a_mismatch_or_missing_token_is_never_retried(self):
        for url in (ARCHIVE, OTHER):
            self.assertIsNone(downloads.retry_delay(url, (0, "has SHA-256 00"), 0))

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


class Case(unittest.TestCase):
    def setUp(self):
        self.clock = 0.0
        self.sleeps = []
        for patch in (mock.patch.object(downloads, "_local_spent", 0.0), mock.patch.object(downloads, "_clock", lambda: self.clock),
                      mock.patch.dict(os.environ, {}, clear=False)):
            patch.start()
            self.addCleanup(patch.stop)
        os.environ.pop("RUNNER_TEMP", None)

    def sleep(self, seconds):
        self.sleeps.append(seconds)
        self.clock += seconds

    def curl(self, results):
        """A _curl that returns the results in turn; each failure takes 5 seconds."""
        results = list(results)
        calls = []

        def run(url, path):
            calls.append(url)
            result = results.pop(0)
            if isinstance(result, dict):
                result = result[url]
            self.clock += 5 if result else 0
            return result
        return run, calls


RESET = (35, "curl: (35) Recv failure: Connection reset by peer")


class DownloadTests(Case):
    def run_download(self, url, results):
        run, calls = self.curl(results)
        with mock.patch.object(downloads, "_curl", run):
            error = downloads.download(url, pathlib.Path("unused"), sleep=self.sleep)
        return error, self.sleeps, len(calls)

    def test_archive_succeeds_after_404s(self):
        error, sleeps, calls = self.run_download(ARCHIVE, [http(404), http(404), None])
        self.assertIsNone(error)
        self.assertEqual((sleeps, calls), ([10, 30], 3))

    def test_archive_fails_after_the_last_retry_naming_the_error(self):
        error, sleeps, calls = self.run_download(ARCHIVE, [http(404)] * 4)
        self.assertIn("404", error)
        self.assertEqual((sleeps, calls), ([10, 30, 60], 4))

    def test_other_host_succeeds_after_connection_resets(self):
        error, sleeps, calls = self.run_download(OTHER, [RESET, RESET, None])
        self.assertIsNone(error)
        self.assertEqual((sleeps, calls), ([30, 90], 3))

    def test_other_host_404_is_tried_once(self):
        error, sleeps, calls = self.run_download(OTHER, [http(404)])
        self.assertIn("404", error)
        self.assertEqual((sleeps, calls), ([], 1))

    def test_waiting_and_retry_attempts_count_toward_the_budget_but_the_first_attempt_does_not(self):
        self.run_download(OTHER, [RESET] * 4)
        self.assertEqual(downloads.spent(), 3 * 5 + 30 + 90 + 180)

    def test_a_dead_address_costs_the_budget_nothing(self):
        for _ in range(50):
            self.run_download(OTHER, [http(404)])
        self.assertEqual(downloads.spent(), 0)

    def test_a_slow_failing_retry_uses_up_the_budget(self):
        results = iter([RESET, RESET, RESET])

        def slow(url, path):
            self.clock += 1100
            return next(results)
        with mock.patch.object(downloads, "_curl", slow):
            error = downloads.download(OTHER, pathlib.Path("unused"), sleep=self.sleep)
        # first attempt free; the retry after 30 s fails in 1100 s; 1130 + 90 > 1200 ends the retries
        self.assertIn("Connection reset", error)
        self.assertEqual(self.sleeps, [30])

    def test_the_budget_is_shared_through_a_file_under_runner_temp(self):
        with tempfile.TemporaryDirectory() as directory:
            os.environ["RUNNER_TEMP"] = directory
            self.run_download(OTHER, [RESET] * 4)
            used = downloads.spent()
            self.assertGreater(used, 0)
            self.assertEqual([f.name for f in pathlib.Path(directory).iterdir()], [downloads.BUDGET_FILE])
            self.assertEqual(downloads._local_spent, 0.0)
            # any process of the job reads the same figure
            self.assertEqual(float((pathlib.Path(directory) / downloads.BUDGET_FILE).read_text()), used)
            downloads.spend(downloads.RETRY_BUDGET)
            error, sleeps, calls = self.run_download(OTHER, [RESET])
            self.assertEqual(calls, 1)


class FetchTests(Case):
    FIRST = "https://first.invalid/a.bin"
    SECOND = "https://second.invalid/a.bin"
    DATA = b"pinned"

    def setUp(self):
        super().setUp()
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.path = pathlib.Path(directory.name) / "a.bin"
        self.digest = __import__("hashlib").sha256(self.DATA).hexdigest()
        os.environ.pop(downloads.CACHE_VARIABLE, None)

    def fetch(self, results, addresses=None):
        """results: per _curl call, None (writes the pinned data), b"..." (writes other data) or an error."""
        queue = list(results)
        calls = []

        def run(url, path):
            calls.append(url)
            result = queue.pop(0)
            if result is None or isinstance(result, bytes):
                path.write_bytes(self.DATA if result is None else result)
                return None
            self.clock += 5
            return result
        with mock.patch.object(downloads, "_curl", run):
            downloads.fetch(addresses or [self.FIRST, self.SECOND], self.digest, self.path, sleep=self.sleep)
        return calls

    def test_every_address_is_tried_once_before_any_is_retried(self):
        calls = self.fetch([RESET, None])
        self.assertEqual((calls, self.sleeps), ([self.FIRST, self.SECOND], []))

    def test_a_dead_original_does_not_delay_a_working_fallback(self):
        calls = self.fetch([http(404), None])
        self.assertEqual((calls, self.sleeps), ([self.FIRST, self.SECOND], []))

    def test_retry_rounds_follow_the_first_pass(self):
        calls = self.fetch([RESET, http(503), RESET, None])
        self.assertEqual(calls, [self.FIRST, self.SECOND, self.FIRST, self.SECOND])
        self.assertEqual(self.sleeps, [30])

    def test_rounds_use_growing_delays_and_end_with_the_errors_named(self):
        with self.assertRaises(SystemExit) as raised:
            self.fetch([RESET] * 8, [self.FIRST])
        self.assertEqual(self.sleeps, [30, 90, 180])
        self.assertIn("Connection reset", str(raised.exception))
        self.assertIn(self.FIRST, str(raised.exception))

    def test_a_definite_failure_ends_the_rounds_early(self):
        with self.assertRaises(SystemExit):
            self.fetch([http(404), (60, "curl: (60) SSL certificate problem")])
        self.assertEqual(self.sleeps, [])

    def test_a_wrong_file_is_not_retried_and_is_removed(self):
        with self.assertRaises(SystemExit) as raised:
            self.fetch([b"other", b"other"])
        self.assertEqual(self.sleeps, [])
        self.assertIn("has SHA-256", str(raised.exception))
        self.assertFalse(self.path.exists())

    def test_the_budget_stops_the_rounds(self):
        downloads.spend(downloads.RETRY_BUDGET - 10)
        with self.assertRaises(SystemExit):
            self.fetch([RESET, RESET], [self.FIRST, self.SECOND])
        self.assertEqual(self.sleeps, [])


if __name__ == "__main__":
    unittest.main()
