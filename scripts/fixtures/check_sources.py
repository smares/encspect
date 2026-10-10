#!/usr/bin/env python3
"""Downloads every pinned file the scripts fetch from every one of its addresses (downloads.py)
and checks its SHA-256, so the scheduled sources workflow notices when an original address or one
of its copies stops serving it, before the fallback is all that is left.

Usage: python3 scripts/fixtures/check_sources.py
"""

import json
import pathlib
import sys
import tempfile
import urllib.parse

import conformance_mpeg_audio
import downloads
import fhg_tool
import mctoolamed_tool

ROOT = pathlib.Path(__file__).resolve().parents[2]


def sources():
    """(name, SHA-256, addresses) of every file the scripts download."""
    iso = json.loads((ROOT / "tests" / "conformance" / "mpeg2-audio.json").read_text(encoding="utf-8"))
    for stream in iso["streams"]:
        name = f"{stream['name']}.tar.gz"
        yield name, stream["archive_sha256"], conformance_mpeg_audio.addresses(iso, name)
    mp3pro = json.loads((ROOT / "tests" / "conformance" / "mp3pro-samples.json").read_text(encoding="utf-8"))
    for sample in mp3pro["samples"]:
        url = mp3pro["source"] + urllib.parse.quote(sample["file"])
        yield sample["file"], sample["sha256"], [url, downloads.archived(url, mp3pro["archive_year"])]
    yield "mctoolamed-01a.tgz", mctoolamed_tool.SHA256, mctoolamed_tool.ADDRESSES
    yield "mp3sconsv15.zip", fhg_tool.SHA256, fhg_tool.ADDRESSES


def main():
    problems = 0
    checked = 0
    with tempfile.TemporaryDirectory() as temp:
        path = pathlib.Path(temp) / "download"
        for name, digest, addresses in sources():
            for address in addresses:
                checked += 1
                if not downloads.download(address, path):
                    problem = "does not serve it"
                elif downloads.sha256(path) != digest:
                    problem = f"serves SHA-256 {downloads.sha256(path)}"
                else:
                    continue
                problems += 1
                print(f"{name}: {address} {problem}")
                path.unlink(missing_ok=True)
    print(f"Checked {checked} addresses, {problems} problems.", file=sys.stderr)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
