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
import time

import blade_tool
import check_mp3pro_samples
import conformance_mpeg_audio
import downloads
import fhg_tool
import gogo_tool
import helix_tool
import lame_win_tool
import mctoolamed_tool
import old_lame_tool
import xing_tool

ROOT = pathlib.Path(__file__).resolve().parents[2]
ARCHIVE_PAUSE = 5


def sources():
    """(name, SHA-256, addresses) of every file the scripts download."""
    iso = json.loads((ROOT / "tests" / "conformance" / "mpeg2-audio.json").read_text(encoding="utf-8"))
    for stream in iso["streams"]:
        name = f"{stream['name']}.tar.gz"
        yield name, stream["archive_sha256"], conformance_mpeg_audio.addresses(iso, name)
    mp3pro = json.loads((ROOT / "tests" / "conformance" / "mp3pro-samples.json").read_text(encoding="utf-8"))
    for sample in mp3pro["samples"]:
        yield sample["file"], sample["sha256"], check_mp3pro_samples.addresses(mp3pro, sample)
    yield "mctoolamed-01a.tgz", mctoolamed_tool.SHA256, mctoolamed_tool.ADDRESSES
    yield "mp3sconsv15.zip", fhg_tool.SHA256, fhg_tool.ADDRESSES
    yield "tompg.zip", xing_tool.SHA256, xing_tool.ADDRESSES
    yield "bladeenc-0.94.2-src-stable.tar.gz", blade_tool.SHA256, blade_tool.ADDRESSES
    yield "helix_mp3enc_5.0r11.tar.gz", helix_tool.R11_SHA256, helix_tool.R11_ADDRESSES
    yield "gogo3.12.zip", gogo_tool.SHA256, gogo_tool.ADDRESSES
    for build in lame_win_tool.BUILDS.values():
        yield build["archive"], build["sha256"], build["addresses"]
    for version, digest in old_lame_tool.RELEASES.items():
        yield f"lame-{version}.tar.gz", digest, old_lame_tool.addresses(version)


def main():
    problems = 0
    checked = 0
    skipped = 0
    with tempfile.TemporaryDirectory() as temp:
        path = pathlib.Path(temp) / "download"
        for name, digest, addresses in sources():
            for address in addresses:
                if not downloads.has_token(address):
                    skipped += 1
                    continue
                checked += 1
                # The Wayback Machine limits how fast one client may download.
                if address.startswith("https://web.archive.org/"):
                    time.sleep(ARCHIVE_PAUSE)
                error = downloads.download(address, path)
                if error is not None:
                    problem = f"does not serve it: {error}"
                elif downloads.sha256(path) != digest:
                    problem = f"serves SHA-256 {downloads.sha256(path)}"
                else:
                    continue
                problems += 1
                print(f"{name}: {address} {problem}")
                path.unlink(missing_ok=True)
    note = f"; {skipped} in the private repository skipped, {downloads.TOKEN} is not set" if skipped else ""
    # LAME 3.94 beta 1 is a tree of files in svn, checked by its digest.
    with tempfile.TemporaryDirectory() as temp:
        checked += 1
        tree = pathlib.Path(temp)
        try:
            old_lame_tool.fetch_tree(old_lame_tool.BETA_URL, tree)
            digest = old_lame_tool.tree_digest(tree)
        except OSError as error:
            digest = f"unreachable ({error})"
        if digest != old_lame_tool.BETA_TREE_SHA256:
            problems += 1
            print(f"lame3_94beta: {old_lame_tool.BETA_URL} holds tree digest {digest}")
    print(f"Checked {checked} addresses, {problems} problems{note}.", file=sys.stderr)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
