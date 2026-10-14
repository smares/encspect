#!/usr/bin/env python3
"""A light check of the documentation, cheap enough for every change and the only check a docs-only pull request runs.

- every relative link in README.md, CLAUDE.md and docs/**/*.md points at a file or folder that exists;
- every page in docs/formats and docs/tags is linked from its folder's README.md.

Anchors (#section) are not followed.
"""

import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
LINK = re.compile(r"(?<!\!)\[[^\]]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)")
FENCE = re.compile(r"^\s*```")


def links(path):
    in_fence = False
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if FENCE.match(line):
            in_fence = not in_fence
        if in_fence:
            continue
        for target in LINK.findall(re.sub(r"`[^`]*`", "", line)):
            yield number, target


def main():
    problems = []
    pages = [ROOT / "README.md", ROOT / "CLAUDE.md", *sorted((ROOT / "docs").rglob("*.md"))]
    for page in pages:
        for number, target in links(page):
            if re.match(r"[a-z][a-z0-9+.-]*:", target) or target.startswith("#"):
                continue
            path = target.split("#", 1)[0]
            if path and not (page.parent / path).exists():
                problems.append(f"{page.relative_to(ROOT)}:{number}: the link {target} points at nothing")
    for folder in ("formats", "tags"):
        index = ROOT / "docs" / folder / "README.md"
        text = index.read_text(encoding="utf-8")
        for page in sorted((ROOT / "docs" / folder).glob("*.md")):
            if page.name != "README.md" and f"({page.name})" not in text:
                problems.append(f"docs/{folder}/README.md does not list {page.name}")
    for problem in problems:
        print(f"::error::{problem}")
    print(f"Checked {len(pages)} pages, {len(problems)} problems.")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
