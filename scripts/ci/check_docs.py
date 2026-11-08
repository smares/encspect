#!/usr/bin/env python3
"""A light check of the documentation, cheap enough for every change and the only check a docs-only pull request runs.

- every relative link in README.md, CLAUDE.md and docs/**/*.md points at a file or folder of the committed tree
  (read with git ls-tree, so the job's sparse checkout does not matter);
- no reference to a Markdown file of the tree is left as plain text or in backticks: it is a link (a file name pattern such
  as docs/formats/<name>.md, and anything in a code block, is not a reference);
- every page in docs/formats and docs/tags is linked from its folder's README.md.

Anchors (#section) are not followed.
"""

import pathlib
import posixpath
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
LINK = re.compile(r"(?<!\!)\[[^\]]*\]\(([^)\s]+)(?:\s+\"[^\"]*\")?\)")
FENCE = re.compile(r"^\s*```")
MD_LINK = re.compile(r"\[[^\]]*\]\([^)]*\)")
MD_REF = re.compile(r"(?<![\w./-])([\w./-]+\.md)\b(?![\w/-])")


def tracked():
    """The files and folders of HEAD, or None outside a git checkout."""
    result = subprocess.run(["git", "ls-tree", "-r", "--name-only", "HEAD"], cwd=ROOT, capture_output=True, text=True)
    if result.returncode != 0:
        return None
    files = set(result.stdout.splitlines())
    return files | {parent for f in files for parent in _parents(f)} | {"."}


def _parents(path):
    while "/" in path:
        path = path.rsplit("/", 1)[0]
        yield path


def links(path):
    in_fence = False
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if FENCE.match(line):
            in_fence = not in_fence
        if in_fence:
            continue
        for target in LINK.findall(re.sub(r"`[^`]*`", "", line)):
            yield number, target


def resolve_reference(token, page, known):
    """The tracked Markdown file a plain-text name refers to, or None: relative to the repository root, to the page's
    folder, or else by file name when exactly one tracked file has it."""
    if known is None or "<" in token or "*" in token:
        return None
    folder = page.parent.relative_to(ROOT).as_posix()
    for candidate in (token, posixpath.join(folder, token)):
        candidate = posixpath.normpath(candidate)
        if candidate in known:
            return candidate
    if "/" not in token:
        matches = [f for f in known if f.endswith(".md") and posixpath.basename(f) == token]
        if len(matches) == 1:
            return matches[0]
    return None


def unlinked_references(page, known):
    in_fence = False
    for number, line in enumerate(page.read_text(encoding="utf-8").splitlines(), 1):
        if FENCE.match(line):
            in_fence = not in_fence
        if in_fence:
            continue
        for token in MD_REF.findall(MD_LINK.sub("", line)):
            if resolve_reference(token, page, known):
                yield number, token


def main():
    problems = []
    known = tracked()
    pages = [ROOT / "README.md", ROOT / "CLAUDE.md", *sorted((ROOT / "docs").rglob("*.md"))]
    for page in pages:
        for number, target in links(page):
            if re.match(r"[a-z][a-z0-9+.-]*:", target) or target.startswith("#"):
                continue
            path = target.split("#", 1)[0]
            resolved = posixpath.normpath(posixpath.join(page.parent.relative_to(ROOT).as_posix(), path)) if path else ""
            exists = resolved in known if known is not None else (page.parent / path).exists()
            if path and not exists:
                problems.append(f"{page.relative_to(ROOT)}:{number}: the link {target} points at nothing")
    for page in pages:
        for number, token in unlinked_references(page, known):
            problems.append(f"{page.relative_to(ROOT)}:{number}: {token} names a Markdown file of the tree without linking it")
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
