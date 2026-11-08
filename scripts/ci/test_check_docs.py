#!/usr/bin/env python3
"""Tests of check_docs.py's unlinked-reference check: python3 scripts/ci/test_check_docs.py"""

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


if __name__ == "__main__":
    unittest.main()
