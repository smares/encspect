#!/usr/bin/env python3
"""Old MKVToolNix releases for Windows, run under Wine as black boxes, for the Matroska files they wrote.

Releases 4.5.0 to 5.9.0 of mkvmerge strip the headers of some audio by default (ContentCompAlgo 3, RFC 9559 section 5.1.4.1.31.6): the sync word of MP3 (its first byte) and of
DTS (its four bytes) from 4.5.0, and the sync word of AC-3 (0x0B77) from 5.4.0; releases 3.4.0 to 4.0.0 and from 6.0.0 on write the frames whole (the project's scan of the releases it
could fetch, with default settings, over the committed AC-3, DTS, MP3 and AAC files: docs/development.md, "Header stripping in Matroska"). The source of mkvtoolnix is not read: the
binaries are run, and what they wrote is looked at with mkvinfo and the analyzer.

    python3 old_mkvmerge_tool.py --list      prints each release and its archive's SHA-256

mux(release, output, inputs) runs the release's mkvmerge.exe under Wine (WINEPREFIX in .cache/old-mkvmerge) on files of the repository.
"""

import os
import pathlib
import shutil
import subprocess
import sys

import downloads

ROOT = pathlib.Path(__file__).resolve().parents[2]
CACHE = ROOT / ".cache" / "old-mkvmerge"
SITE = "https://mkvtoolnix.download/windows/releases/{version}/{archive}"
WAYBACK_YEAR = {"4.5.0": "2011", "5.4.0": "2012", "5.9.0": "2013"}

# Release: (archive, SHA-256).
RELEASES = {
    "4.5.0": ("mkvtoolnix-unicode-4.5.0.7z", "a0f0224c0adbc0fa1968bdac4337778c7ae70fde95c97d4ddc5d97a46ee276df"),
    "5.4.0": ("mkvtoolnix-unicode-5.4.0.7z", "56d339823f09dd0b56d0aa0976fa6ae2b50afc21d7e96c4cea5893fc2ab460f8"),
    "5.9.0": ("mkvtoolnix-unicode-5.9.0.7z", "bd2c4c394d51a02a143701e731837f1df896e47466a58e797ee2a45f5cd8fbde"),
}


def addresses(version):
    archive = RELEASES[version][0]
    url = SITE.format(version=version, archive=archive)
    return [url, downloads.archived(url, WAYBACK_YEAR[version])]


def sources():
    for version, (archive, digest) in RELEASES.items():
        yield archive, digest, addresses(version)


def executable(version):
    """The path of the release's mkvmerge.exe, fetched and unpacked in the cache."""
    folder = CACHE / version
    for found in folder.rglob("mkvmerge.exe"):
        return found
    CACHE.mkdir(parents=True, exist_ok=True)
    archive, digest = RELEASES[version]
    path = CACHE / archive
    downloads.fetch(addresses(version), digest, path)
    if shutil.which("7z") is None:
        sys.exit("7z is needed to unpack the old MKVToolNix archives.")
    subprocess.run(["7z", "x", "-y", f"-o{folder}", str(path)], check=True, capture_output=True)
    return next(folder.rglob("mkvmerge.exe"))


def mux(version, output, inputs, arguments=()):
    """Runs the release's mkvmerge over the files, which are paths on this machine."""
    if shutil.which("wine") is None:
        sys.exit("Wine is needed to run the old MKVToolNix releases.")
    exe = executable(version)
    environment = dict(os.environ, WINEDEBUG="-all", WINEPREFIX=str(CACHE / "prefix"))
    windows = lambda p: "Z:" + str(pathlib.Path(p).resolve())
    result = subprocess.run(["wine", str(exe), "-o", windows(output), *arguments, *[windows(i) for i in inputs]], capture_output=True, text=True, env=environment, timeout=300)
    # Exit status 1 is a warning (mkvmerge's own convention), 2 an error.
    if result.returncode > 1 or not pathlib.Path(output).exists():
        sys.exit(f"mkvmerge {version} failed ({result.returncode}): {result.stdout[-400:]}")


def banner(version):
    exe = executable(version)
    environment = dict(os.environ, WINEDEBUG="-all", WINEPREFIX=str(CACHE / "prefix"))
    return subprocess.run(["wine", str(exe), "--version"], capture_output=True, text=True, env=environment, timeout=120).stdout.strip()


if __name__ == "__main__":
    if "--list" in sys.argv:
        for version, (archive, digest) in RELEASES.items():
            print(version, archive, digest)
    else:
        sys.exit(__doc__)
