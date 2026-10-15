#!/usr/bin/env python3
"""Tests of check_results.py: python3 scripts/ci/test_check_results.py"""

import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import check_results  # noqa: E402


def needs(**results):
    flags = {job: "false" for job, r in results.items() if r == "skipped"}
    return {"changes": {"result": "success", "outputs": flags}, **{job: {"result": r} for job, r in results.items()}}


class CheckResultsTests(unittest.TestCase):
    def test_all_success(self):
        self.assertEqual(check_results.verdict(needs(a="success", b="success")), [])

    def test_a_deliberate_skip_passes(self):
        self.assertEqual(check_results.verdict(needs(a="success", b="skipped")), [])

    def test_a_skip_the_filter_did_not_want_fails(self):
        n = needs(a="success", b="skipped")
        n["changes"]["outputs"]["b"] = "true"
        self.assertEqual(len(check_results.verdict(n)), 1)

    def test_a_skip_without_a_decision_fails(self):
        n = needs(a="success", b="skipped")
        n["changes"]["outputs"] = {}
        self.assertEqual(len(check_results.verdict(n)), 1)

    def test_no_skip_is_accepted_when_the_pipeline_changed(self):
        n = needs(a="success", b="skipped")
        n["changes"]["outputs"]["pipeline"] = "true"
        self.assertEqual(len(check_results.verdict(n)), 1)
        n["changes"]["outputs"]["pipeline"] = "false"
        self.assertEqual(check_results.verdict(n), [])

    def test_failure_and_cancellation_fail(self):
        self.assertEqual(len(check_results.verdict(needs(a="failure", b="cancelled"))), 2)

    def test_a_failed_changes_job_fails(self):
        n = needs(a="success")
        n["changes"]["result"] = "failure"
        self.assertEqual(len(check_results.verdict(n)), 1)


if __name__ == "__main__":
    unittest.main()
