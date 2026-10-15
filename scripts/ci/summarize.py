#!/usr/bin/env python3
"""Writes what failed in a job to the job's step summary ($GITHUB_STEP_SUMMARY, or standard output).

    summarize.py tests <directory with .trx files> [--title TEXT]
    summarize.py logs <directory or file with logs> [--title TEXT] [--files DIRECTORY]

"tests" lists the failed tests of TRX files with the first lines of their messages. "logs" picks the
lines of a cross-check's or the fuzzer's output that name a problem, and the end of each log; with
--files it also lists the files of a directory, the inputs the fuzzer saved. Nothing is written when
nothing failed, so the step can run with if: failure() and leave a clean job's summary alone.
"""

import argparse
import os
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

MAX_FAILURES = 40
MAX_MESSAGE = 700
PROBLEM = re.compile(
    r"(problems?\b|mismatch|differ|disagree|unexpected|not found|missing|error|failed|failure|traceback|exception|Iteration \d+:|Input saved|replay with|Seed \d+)",
    re.IGNORECASE)
SUMMARY_LINE = re.compile(r"(Cross-checked|checked \d+|\d+ problems?|inputs, no failures|Seed \d+)", re.IGNORECASE)


def emit(text):
    target = os.environ.get("GITHUB_STEP_SUMMARY")
    if target:
        with open(target, "a", encoding="utf-8") as out:
            out.write(text)
    else:
        sys.stdout.write(text)


def annotate(message):
    """A workflow command GitHub turns into an annotation of the job, which the public API serves without a
    token (scripts/ci_report.py reads them); at most ten errors per step are shown, so only the first are sent."""
    sys.stdout.write("::error::" + message.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A") + "\n")


def tests(directory, title):
    failed = []
    counts = {"total": 0, "passed": 0, "failed": 0}
    for trx in sorted(pathlib.Path(directory).rglob("*.trx")):
        root = ET.parse(trx).getroot()
        namespace = root.tag[: root.tag.index("}") + 1]
        counters = root.find(f".//{namespace}ResultSummary/{namespace}Counters")
        if counters is not None:
            counts["total"] += int(counters.get("total", 0))
            counts["passed"] += int(counters.get("passed", 0))
            counts["failed"] += int(counters.get("failed", 0))
        for result in root.iter(f"{namespace}UnitTestResult"):
            if result.get("outcome") != "Failed":
                continue
            message = result.find(f"{namespace}Output/{namespace}ErrorInfo/{namespace}Message")
            text = (message.text or "").strip() if message is not None else ""
            failed.append((result.get("testName"), text))
    if not failed:
        return
    lines = [f"### {title}", "", f"{len(failed)} failed of {counts['total']} tests ({counts['passed']} passed).", ""]
    for name, text in failed[:MAX_FAILURES]:
        lines.append(f"- `{name}`")
        if text:
            shown = text[:MAX_MESSAGE] + ("..." if len(text) > MAX_MESSAGE else "")
            lines.append("  ```")
            lines.extend("  " + line for line in shown.splitlines()[:12])
            lines.append("  ```")
    if len(failed) > MAX_FAILURES:
        lines.append(f"- and {len(failed) - MAX_FAILURES} more; the TRX files are in the job's test-results artifact.")
    emit("\n".join(lines) + "\n\n")
    for name, text in failed[:9]:
        annotate(f"Failed {name}" + (f": {text.splitlines()[0][:200]}" if text else ""))
    if len(failed) > 9:
        annotate(f"and {len(failed) - 9} more failed tests; see the job summary")


def logs(path, title, files):
    sources = sorted(pathlib.Path(path).glob("*.log")) if pathlib.Path(path).is_dir() else [pathlib.Path(path)]
    sections = []
    annotated = []
    for source in sources:
        if not source.exists():
            continue
        text = source.read_text(encoding="utf-8", errors="replace").splitlines()
        picked = [line for line in text if PROBLEM.search(line)]
        summary = [line for line in text if SUMMARY_LINE.search(line)]
        shown = list(dict.fromkeys(picked[:60] + summary[-3:]))
        annotated += [line.strip()[:300] for line in dict.fromkeys(picked[:5] + summary[-1:]) if not line.lstrip().startswith("::")]
        tail = text[-15:]
        body = ["#### " + source.stem, ""]
        if shown:
            body += ["```", *[line[:300] for line in shown], "```", ""]
        body += ["Last lines:", "", "```", *[line[:300] for line in tail], "```", ""]
        sections.append("\n".join(body))
    saved = []
    if files and pathlib.Path(files).is_dir():
        saved = [p.name for p in sorted(pathlib.Path(files).rglob("*")) if p.is_file()]
    if not sections and not saved:
        return
    out = [f"### {title}", ""]
    if saved:
        out += ["Saved inputs (also uploaded as an artifact): " + ", ".join(f"`{n}`" for n in saved[:20]), ""]
    emit("\n".join(out + sections) + "\n")
    for line in annotated[:6]:
        annotate(line)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["tests", "logs"])
    parser.add_argument("path")
    parser.add_argument("--title", default="Failures")
    parser.add_argument("--files")
    args = parser.parse_args()
    if args.mode == "tests":
        tests(args.path, args.title)
    else:
        logs(args.path, args.title, args.files)


if __name__ == "__main__":
    main()
