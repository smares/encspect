#!/usr/bin/env python3
"""Downloads the public Windows Media Audio Professional files of tests/conformance/wma-samples.json (from the FFmpeg
sample archive and the FATE suite, checked against their SHA-256, never committed) into a folder.

    python3 fetch_wma_pro_samples.py <output folder>
"""

import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "scripts" / "fixtures"))
import downloads  # noqa: E402


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    out = pathlib.Path(sys.argv[1])
    out.mkdir(parents=True, exist_ok=True)
    samples = json.loads((ROOT / "tests" / "conformance" / "wma-samples.json").read_text(encoding="utf-8"))["samples"]
    for sample in samples:
        if sample["codec"] != "wma-pro":
            continue
        try:
            downloads.fetch([sample["url"]], sample["sha256"], out / sample["file"])
            print("downloaded", sample["file"])
        except Exception as error:  # a file that cannot be had is left out; the rest still runs
            print("not downloaded", sample["file"], str(error)[:200])
    return 0


if __name__ == "__main__":
    sys.exit(main())
