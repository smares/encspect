#!/usr/bin/env python3
"""Downloads the three Windows Media Audio version 1 files that Microsoft's encoder wrote (tests/conformance/wma1-microsoft-samples.json) into a folder.

They come from the FFmpeg sample archive (A-codecs/WMA1), are commercial content and are never committed or uploaded: each is checked against its SHA-256, from the archive's
address first and then the private repository's copy (mirrors/samples/ffmpeg-wma1). crosscheck_wma_pcm.py --samples uses them, the Windows run
(.github/workflows/windows-wma-decode.yml) writes Microsoft's samples of them, and check_sources.py checks both addresses.

    python3 scripts/fixtures/wma1_microsoft_samples.py <output folder>
"""

import json
import pathlib
import sys
import urllib.parse

import downloads

ROOT = pathlib.Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "tests" / "conformance" / "wma1-microsoft-samples.json"


def samples():
    return json.loads(MANIFEST.read_text(encoding="utf-8"))["samples"]


def addresses(sample):
    """The archive's address, then the private repository's copy."""
    return [sample["url"], downloads.private(f"mirrors/samples/ffmpeg-wma1/{urllib.parse.quote(sample['file'])}")]


def fetch(folder):
    """Downloads every file into folder; returns the paths of those that could be had."""
    folder = pathlib.Path(folder)
    folder.mkdir(parents=True, exist_ok=True)
    found = []
    for sample in samples():
        try:
            downloads.fetch(addresses(sample), sample["sha256"], folder / sample["file"])
            found.append(folder / sample["file"])
            print("downloaded", sample["file"])
        except Exception as error:  # a file that cannot be had is left out; the rest still runs
            print("not downloaded", sample["file"], str(error)[:200])
    return found


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    fetch(sys.argv[1])
    return 0


if __name__ == "__main__":
    sys.exit(main())
