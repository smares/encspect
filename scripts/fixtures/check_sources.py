#!/usr/bin/env python3
"""Downloads every pinned file the scripts fetch from every one of its addresses (downloads.py)
and checks its SHA-256 (and that FFmpeg's release holds every file ffmpeg_sources.py extracts), so the scheduled sources workflow notices when an original address or one
of its copies stops serving it, before the fallback is all that is left. FAAC's releases are pinned by
the commits of their tags (faac_tool.py), which it asks both repositories for, and Apple's ALAC source by its commit (alac_tool.py).

Usage: python3 scripts/fixtures/check_sources.py [--list | --only FILE]
  --list       prints every (name, SHA-256, address, kind) as sorted JSON, without downloading; the LAME svn trees
               (tree digest), FAAC's tags (commit, one entry per repository) and each file of FFmpeg's release that
               ffmpeg_sources.py extracts (name "<archive>/<member>", the archive's digest and address) are entries too
  --only FILE  checks only the entries in FILE, in that form (the pull request check passes the ones it adds or changes)
"""

import json
import pathlib
import sys
import tempfile
import time

import a52_trace_tool
import alac_tool
import aotuv_tool
import asf_specs
import blade_tool
import check_eac3_samples
import check_monkeys_audio_samples
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
import monkeys_audio_tool
import musepack_tool
import old_lame_tool
import speex_tool
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
    monkeys = json.loads((ROOT / "tests" / "conformance" / "monkeys-audio-samples.json").read_text(encoding="utf-8"))
    for sample in monkeys["samples"]:
        yield sample["file"], sample["sha256"], check_monkeys_audio_samples.addresses(sample)
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
    yield from speex_tool.sources()
    yield from monkeys_audio_tool.sources()
    yield from libtta_tool.sources()
    yield asf_specs.NAME, asf_specs.SHA256, asf_specs.ADDRESSES
    for name, digest, addresses in matroska_specs.SPECS:
        yield name, digest, addresses
    yield dts_specs.NAME, dts_specs.SHA256, dts_specs.ADDRESSES
    yield libdca_source.ARCHIVE, libdca_source.SHA256, libdca_source.ADDRESSES
    yield from gpac_tool.sources()
    yield ffmpeg_sources.ARCHIVE, ffmpeg_sources.SHA256, ffmpeg_sources.ADDRESSES
    yield dashif_systems.NAME, dashif_systems.SHA256, dashif_systems.ADDRESSES


def member_name(member):
    return f"{ffmpeg_sources.ARCHIVE}/{member}"


def entries():
    """(kind, name, SHA-256 or commit, address) of everything the full run checks: "download" for a file,
    "member" for a file of FFmpeg's release that must be in the archive (the archive's digest and address),
    "svn" for a LAME tree (its digest is the tree digest), "tag" for a FAAC tag in one repository, "commit" for the commit of Apple's ALAC source."""
    for name, digest, addresses in sources():
        for address in addresses:
            yield "download", name, digest, address
    for member in ffmpeg_sources.FILES:
        for address in ffmpeg_sources.ADDRESSES:
            yield "member", member_name(member), ffmpeg_sources.SHA256, address
    for version, (tag, digest) in old_lame_tool.SVN_TREES.items():
        yield "svn", tag, digest, old_lame_tool.svn_url(version)
    for faac_tag, commit, _, _ in faac_tool.RELEASES.values():
        for repository in faac_tool.REPOSITORIES:
            yield "tag", faac_tag, f"commit:{commit}", f"{repository} refs/tags/{faac_tag}"
    yield "commit", alac_tool.NAME, f"commit:{alac_tool.COMMIT}", f"{alac_tool.REPOSITORY} {alac_tool.COMMIT}"


def listing(selected=None):
    """The entries as sorted, unique dicts, the form --list prints and --only reads."""
    found = {}
    for kind, name, digest, address in entries():
        found.setdefault((name, digest, address), kind)
    return [{"name": name, "sha256": digest, "address": address, "kind": found[name, digest, address]}
            for name, digest, address in sorted(found) if selected is None or (name, digest, address) in selected]


def main():
    args = sys.argv[1:]
    if args == ["--list"]:
        print(json.dumps(listing(), indent=1))
        return
    selected = None
    if len(args) == 2 and args[0] == "--only":
        wanted = json.loads(pathlib.Path(args[1]).read_text(encoding="utf-8"))
        selected = {(item["name"], item["sha256"], item["address"]) for item in wanted}
        unknown = selected - {(name, digest, address) for _, name, digest, address in entries()}
        if unknown:
            sys.exit("--only lists entries that --list does not hold: " + ", ".join(sorted(f"{n} {a}" for n, _, a in unknown)))
    elif args:
        sys.exit("usage: check_sources.py [--list | --only FILE]")

    def wanted_entry(name, digest, address):
        return selected is None or (name, digest, address) in selected

    problems = 0
    checked = 0
    skipped = 0
    with tempfile.TemporaryDirectory() as temp:
        path = pathlib.Path(temp) / "download"
        for name, digest, addresses in sources():
            for address in addresses:
                wanted_file = wanted_entry(name, digest, address)
                # The archive is the one the scripts extract FILES from: each must be in it.
                members = [m for m in ffmpeg_sources.FILES if name == ffmpeg_sources.ARCHIVE and wanted_entry(member_name(m), digest, address)]
                if not wanted_file and not members:
                    continue
                if not downloads.has_token(address):
                    skipped += 1
                    continue
                checked += int(wanted_file) + len(members)
                # The Wayback Machine limits how fast one client may download.
                if address.startswith("https://web.archive.org/"):
                    time.sleep(ARCHIVE_PAUSE)
                error = downloads.download(address, path)
                if error is not None:
                    problem = f"does not serve it: {error}"
                elif downloads.sha256(path) != digest:
                    problem = f"serves SHA-256 {downloads.sha256(path)}"
                else:
                    if members:
                        for absent in ffmpeg_sources.missing(path, members):
                            problems += 1
                            print(f"{name}: {address} does not hold {absent}")
                    continue
                problems += 1
                print(f"{name}: {address} {problem}")
                path.unlink(missing_ok=True)
    note = f"; {skipped} in the private repository skipped, {downloads.TOKEN} is not set" if skipped else ""
    # LAME's betas, its alpha and 3.99.5 are trees of files in svn, checked by their digests.
    for version, (tag, expected) in old_lame_tool.SVN_TREES.items():
        if not wanted_entry(tag, expected, old_lame_tool.svn_url(version)):
            continue
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
    faac_wanted = {(tag, repository) for tag, commit, _, _ in faac_tool.RELEASES.values() for repository in faac_tool.REPOSITORIES
                   if wanted_entry(tag, f"commit:{commit}", f"{repository} refs/tags/{tag}")}
    checked += len(faac_wanted)
    for problem in faac_tool.tag_problems(faac_wanted):
        problems += 1
        print(problem)
    # Apple's ALAC source is pinned by one commit, which the repository must still serve.
    alac_address = f"{alac_tool.REPOSITORY} {alac_tool.COMMIT}"
    if wanted_entry(alac_tool.NAME, f"commit:{alac_tool.COMMIT}", alac_address):
        checked += 1
        for problem in alac_tool.commit_problems({alac_address}):
            problems += 1
            print(problem)
    print(f"Checked {checked} entries, {problems} problems{note}.", file=sys.stderr)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
