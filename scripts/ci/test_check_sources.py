#!/usr/bin/env python3
"""Tests of how check_sources.py weighs a failed address: python3 scripts/ci/test_check_sources.py"""

import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / "fixtures"))
import check_sources  # noqa: E402

WAYBACK = "https://web.archive.org/web/20010611205504id_/http://example.invalid/a.tar.gz"
ORIGINAL = "https://example.invalid/a.tar.gz"


def never(name, digest, address):
    return False


def always(name, digest, address):
    return True


class SettleTests(unittest.TestCase):
    def test_wayback_failure_is_a_warning_when_another_address_served_the_file(self):
        failures = [("a.tar.gz", "d", WAYBACK, "does not serve it: 404")]
        problems, warnings = check_sources.settle(failures, {("a.tar.gz", "d")}, never)
        self.assertEqual(problems, [])
        self.assertEqual(warnings, failures)

    def test_wayback_failure_is_a_warning_when_an_unchecked_address_serves_the_file(self):
        failures = [("a.tar.gz", "d", WAYBACK, "does not serve it: 404")]
        problems, warnings = check_sources.settle(failures, set(), always)
        self.assertEqual((problems, warnings), ([], failures))

    def test_wayback_failure_is_a_problem_when_no_address_serves_the_file(self):
        failures = [("a.tar.gz", "d", WAYBACK, "does not serve it: 404")]
        problems, warnings = check_sources.settle(failures, set(), never)
        self.assertEqual((problems, warnings), (failures, []))

    def test_other_failures_stay_problems_even_when_the_file_is_served(self):
        failures = [("a.tar.gz", "d", ORIGINAL, "does not serve it: 404")]
        problems, warnings = check_sources.settle(failures, {("a.tar.gz", "d")}, always)
        self.assertEqual((problems, warnings), (failures, []))

    def test_a_wrong_digest_at_wayback_counts_like_a_failed_download(self):
        failures = [("a.tar.gz", "d", WAYBACK, "serves SHA-256 e")]
        problems, warnings = check_sources.settle(failures, {("a.tar.gz", "d")}, never)
        self.assertEqual((problems, warnings), ([], failures))

    def test_a_warning_is_an_annotation_that_names_the_file_and_address(self):
        line = check_sources.warning_line("a.tar.gz", WAYBACK, "does not serve it: 404")
        self.assertTrue(line.startswith("::warning "))
        self.assertIn("a.tar.gz", line)
        self.assertIn(WAYBACK, line)


if __name__ == "__main__":
    unittest.main()
