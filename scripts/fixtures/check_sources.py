#!/usr/bin/env python3
"""Downloads every pinned file the scripts fetch from every one of its addresses (downloads.py)
and checks its SHA-256 (and that FFmpeg's release holds every file ffmpeg_sources.py extracts), so the scheduled sources workflow notices when an original address or one
of its copies stops serving it, before the fallback is all that is left. FAAC's releases are pinned by
the commits of their tags (faac_tool.py), which it asks both repositories for.

Usage: python3 scripts/fixtures/check_sources.py
"""

import json
import pathlib
import sys
import tempfile
import time

import a52_trace_tool
import aotuv_tool
import asf_specs
import blade_tool
import check_eac3_samples
import check_mp3pro_samples
import check_sbr_samples
import check_usac_samples
import check_wmavoice_samples
import conformance_mpeg_audio
import dashif_systems
import downloads
import dts_specs
import faac_tool
import ffmpeg_sources
import fhg_tool
import gogo_tool
import gpac_tool
import helix_tool
import lame_win_tool
import libdca_source
import libtta_tool
import matroska_specs
import mctoolamed_tool
import musepack_tool
import old_lame_tool
import wavpack_tool
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
    eac3 = json.loads((ROOT / "tests" / "conformance" / "eac3-samples.json").read_text(encoding="utf-8"))
    for sample in eac3["samples"]:
        yield sample["file"], sample["sha256"], check_eac3_samples.addresses(sample)
    usac = json.loads((ROOT / "tests" / "conformance" / "usac-samples.json").read_text(encoding="utf-8"))
    for sample in usac["samples"]:
        yield sample["file"], sample["sha256"], check_usac_samples.addresses(sample)
    sbr = json.loads((ROOT / "tests" / "conformance" / "sbr-samples.json").read_text(encoding="utf-8"))
    for sample in sbr["samples"]:
        yield sample["file"], sample["sha256"], check_sbr_samples.addresses(sample)
    wmavoice = json.loads((ROOT / "tests" / "conformance" / "wmavoice-samples.json").read_text(encoding="utf-8"))
    for sample in wmavoice["samples"]:
        yield sample["file"], sample["sha256"], check_wmavoice_samples.addresses(sample)
    yield "mctoolamed-01a.tgz", mctoolamed_tool.SHA256, mctoolamed_tool.ADDRESSES
    yield a52_trace_tool.ARCHIVE, a52_trace_tool.SHA256, a52_trace_tool.ADDRESSES
    yield "mp3sconsv15.zip", fhg_tool.SHA256, fhg_tool.ADDRESSES
    yield "tompg.zip", xing_tool.SHA256, xing_tool.ADDRESSES
    for release in blade_tool.RELEASES.values():
        yield release["archive"], release["sha256"], blade_tool.addresses(release)
    yield "helix_mp3enc_5.0r11.tar.gz", helix_tool.R11_SHA256, helix_tool.R11_ADDRESSES
    yield "gogo3.12.zip", gogo_tool.SHA256, gogo_tool.ADDRESSES
    for build in lame_win_tool.BUILDS.values():
        yield build["archive"], build["sha256"], build["addresses"]
    for version, digest in old_lame_tool.RELEASES.items():
        yield f"lame-{version}.tar.gz", digest, old_lame_tool.addresses(version)
    for archive, digest in aotuv_tool.SOURCES.values():
        yield archive, digest, aotuv_tool.addresses(archive)
    for archive, digest in aotuv_tool.WINDOWS.values():
        yield archive, digest, aotuv_tool.windows_addresses(archive)
    yield from musepack_tool.sources()
    yield from wavpack_tool.sources()
    yield from libtta_tool.sources()
    yield asf_specs.NAME, asf_specs.SHA256, asf_specs.ADDRESSES
    for name, digest, addresses in matroska_specs.SPECS:
        yield name, digest, addresses
    yield dts_specs.NAME, dts_specs.SHA256, dts_specs.ADDRESSES
    yield libdca_source.ARCHIVE, libdca_source.SHA256, libdca_source.ADDRESSES
    yield from gpac_tool.sources()
    yield ffmpeg_sources.ARCHIVE, ffmpeg_sources.SHA256, ffmpeg_sources.ADDRESSES
    yield dashif_systems.NAME, dashif_systems.SHA256, dashif_systems.ADDRESSES


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
                    # The archive is the one the scripts extract FILES from: each must be in it.
                    if name == ffmpeg_sources.ARCHIVE:
                        for absent in ffmpeg_sources.missing(path):
                            problems += 1
                            print(f"{name}: {address} does not hold {absent}")
                    continue
                problems += 1
                print(f"{name}: {address} {problem}")
                path.unlink(missing_ok=True)
    note = f"; {skipped} in the private repository skipped, {downloads.TOKEN} is not set" if skipped else ""
    # LAME's betas, its alpha and 3.99.5 are trees of files in svn, checked by their digests.
    for version, (tag, expected) in old_lame_tool.SVN_TREES.items():
        with tempfile.TemporaryDirectory() as temp:
            checked += 1
            tree = pathlib.Path(temp)
            try:
                old_lame_tool.fetch_tree(old_lame_tool.svn_url(version), tree)
                digest = old_lame_tool.tree_digest(tree)
            except OSError as error:
                digest = f"unreachable ({error})"
            if digest != expected:
                problems += 1
                print(f"{tag}: {old_lame_tool.svn_url(version)} holds tree digest {digest}")
    # FAAC's releases are pinned by their commits, which git checks against the tree: each repository must
    # still serve each tag with its commit.
    checked += len(faac_tool.RELEASES) * len(faac_tool.REPOSITORIES)
    for problem in faac_tool.tag_problems():
        problems += 1
        print(problem)
    print(f"Checked {checked} addresses, {problems} problems{note}.", file=sys.stderr)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
