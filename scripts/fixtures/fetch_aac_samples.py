#!/usr/bin/env python3
"""Downloads the AAC samples that the repository pins by SHA-256 and never commits (tests/conformance/sbr-samples.json and usac-samples.json) into a folder, for the
official-decoder cross-checks (docs/design/official-decoder-crosschecks.md, "Fixtures").

  fetch_aac_samples.py DIR [SECONDS]

With SECONDS, no further sample is started after that time since the start: the rest is listed as not fetched, so that a host that is down for hours costs the job SECONDS and no more.

A sample that no address serves (the xHE-AAC files of Fraunhofer need the private repository's token when their own address is gone) is listed on the standard
output and skipped: the cross-check runs on what was fetched. The folder holds media and is never uploaded: artifacts and caches hold records only.
"""

import json
import pathlib
import sys
import time
import urllib.parse

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import downloads  # noqa: E402

ROOT = HERE.parents[1]


def addresses(sample, kind):
    found = [sample["url"]]
    if kind == "usac":
        found.append(downloads.private(f"usac/{urllib.parse.quote(sample['file'])}"))
    return found


def main(argv):
    target = pathlib.Path(argv[0])
    target.mkdir(parents=True, exist_ok=True)
    missing = []
    started = time.monotonic()
    deadline = float(argv[1]) if len(argv) > 1 else None
    for kind in ("sbr", "usac"):
        manifest = json.loads((ROOT / "tests" / "conformance" / f"{kind}-samples.json").read_text(encoding="utf-8"))
        for sample in manifest["samples"]:
            if deadline is not None and time.monotonic() - started > deadline:
                missing.append("%s (not started: the time budget of %d s is spent)" % (sample["file"], deadline))
                continue
            try:
                downloads.fetch([a for a in addresses(sample, kind) if downloads.has_token(a)], sample["sha256"], target / sample["file"])
            except (SystemExit, OSError, RuntimeError) as error:
                missing.append("%s (%s)" % (sample["file"], str(error)[:80]))
    print("fetched %d files into %s" % (sum(1 for p in target.iterdir() if p.is_file()), target))
    for line in missing:
        print("::warning::not fetched: " + line)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
