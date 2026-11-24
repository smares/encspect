#!/usr/bin/env python3
"""Tests of check_docs.py's unlinked-reference and plan status checks: python3 scripts/ci/test_check_docs.py"""

import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import check_docs  # noqa: E402

KNOWN = {"docs", "docs/development.md", "docs/design", "docs/design/decoders.md", "README.md"}


def found(text, name="docs/cli.md"):
    root = pathlib.Path(tempfile.mkdtemp())
    page = root / name
    page.parent.mkdir(parents=True, exist_ok=True)
    page.write_text(text, encoding="utf-8")
    original = check_docs.ROOT
    check_docs.ROOT = root
    try:
        return [token for _, token in check_docs.unlinked_references(page, KNOWN)]
    finally:
        check_docs.ROOT = original


class UnlinkedReferenceTests(unittest.TestCase):
    def test_plain_and_backticked_names_are_found(self):
        self.assertEqual(found("See decoders.md and `docs/development.md`."), ["decoders.md", "docs/development.md"])

    def test_links_are_not_found(self):
        self.assertEqual(found("See [decoders.md](design/decoders.md) and [`development.md`](development.md)."), [])

    def test_code_blocks_patterns_and_unknown_files_are_left_alone(self):
        text = "```\ndocs/development.md\n```\n`docs/formats/<name>.md` and notes.md\n"
        self.assertEqual(found(text), [])


PLAN = """### 0.9: Example

**Status:** in progress: items 1 to 2 and 4 are done; 3 is in review.

1. **First** (the lead, 2026-01-01; done, 2026-01-02, #1): text.
2. **Second** (done, 2026-01-03; from the [survey](#survey)): text.
   - **A sub-item** (done): not counted.
3. **Third** (the lead, 2026-01-04): text.
4. **Fourth** (the lead, 2026-01-05; done, 2026-01-06): text.

### 1.0: Later

**Status:** planned.

1. **Planned** (the lead): text.
"""


class PlanStatusTests(unittest.TestCase):
    def test_a_status_line_that_names_the_done_items_passes(self):
        self.assertEqual(check_docs.stale_statuses(PLAN), [])

    def test_an_item_marked_done_but_not_named_is_found(self):
        stale = PLAN.replace("items 1 to 2 and 4 are done", "items 1 to 2 are done")
        self.assertEqual([line for line, _ in check_docs.stale_statuses(stale)], [3])
        self.assertIn("done but not named: [4]", check_docs.stale_statuses(stale)[0][1])

    def test_an_item_named_but_not_marked_done_is_found(self):
        stale = PLAN.replace("items 1 to 2 and 4 are done", "items 1 to 4 are done")
        self.assertIn("named but not done: [3]", check_docs.stale_statuses(stale)[0][1])

    def test_lists_with_commas_ranges_and_and_are_read(self):
        self.assertEqual(check_docs.numbers("1 to 5, 7 and 16"), {1, 2, 3, 4, 5, 7, 16})
        self.assertEqual(check_docs.numbers("1 to 12 and 16 to 18"), set(range(1, 13)) | {16, 17, 18})


if __name__ == "__main__":
    unittest.main()
