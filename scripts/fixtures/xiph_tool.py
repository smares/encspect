#!/usr/bin/env python3
"""Xiph's own libvorbis releases before 1.3, built from the tags of the repository xiph/vorbis
(BSD-style license), for encoder-written Vorbis fixtures and for the vendor strings the analyzer
names them by.

The old oggenc builds for Windows are gone from Rarewares (its pages keep libvorbis 1.3.7 builds
only) and neither Xiph's download server nor the Wayback Machine could be reached when this was
written, so the releases are built from source: REPOSITORY is cloned and TAGS pins each tag by its
commit, which decides what is built, like the aoTuV rebuilds in aotuv_tool.py.

    python3 xiph_tool.py --vendors   prints each release's vendor string (lib/info.c) and checks
                                     that the analyzer's table (VorbisParser.Report.cs) lists it

build(release) compiles a release's libvorbis and libvorbisenc in .cache/encoders, whose shared
libraries Ubuntu's oggenc (vorbis-tools 1.4.2) loads in place of the system's through
LD_LIBRARY_PATH. That oggenc calls vorbis_version_string, which these releases lack, so a small
library that defines it is preloaded; the string only reaches oggenc's console output, never the
file, whose vendor string libvorbisenc writes.
"""

import os
import pathlib
import re
import subprocess
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[2]
CACHE = ROOT / ".cache" / "encoders"

REPOSITORY = "https://github.com/xiph/vorbis.git"

# Release: (tag, commit). 1.1.1 writes the same vendor string as 1.1.2 and has no better fixture.
TAGS = {
    "1.0": ("v1.0.0", "e195c84227e6c3ede1a964ae9a0a6650baec958c"),
    "1.0.1": ("v1.0.1", "b63c4d7189f861d51e4e22ff76ff8711753179b9"),
    "1.1.0": ("v1.1.0", "b11402bb7ac019a3728a02e4e8459bc6a24ea4ac"),
    "1.1.2": ("v1.1.2", "8edf4bc65bf10cb1e5e3ea8623f5f6e6b9381875"),
    "1.2.0": ("v1.2.0", "aa7deae7d778550e5e9e16d6318c72a1f5f42a99"),
    "1.2.2": ("v1.2.2", "d84f58a741071ac233e395eef7c88cca0c9ab5a7"),
    "1.2.3": ("v1.2.3", "d701313fc8b102737e5fdd3fdd4ec6b5b6410211"),
}

VENDOR = re.compile(r'"(Xiph\.Org libVorbis I [^"]*)"')
SHIM = 'const char *vorbis_version_string(void) { return "Xiph.Org libVorbis (version string shim)"; }\n'


def checkout(release):
    """The tree of a release, checked out at its pinned commit in .cache/encoders."""
    tag, commit = TAGS[release]
    repository = CACHE / "vorbis-xiph"
    if not repository.exists():
        CACHE.mkdir(parents=True, exist_ok=True)
        subprocess.run(["git", "clone", "-q", "--no-checkout", REPOSITORY, str(repository)], check=True)
    directory = CACHE / f"vorbis-xiph-{tag}"
    if not directory.exists():
        subprocess.run(["git", "-C", str(repository), "worktree", "add", "-q", "--detach", str(directory), commit], check=True)
    actual = subprocess.run(["git", "-C", str(directory), "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    if actual != commit:
        raise SystemExit(f"{tag} is commit {actual}, not the pinned {commit}")
    return directory


def vendor_strings(directory):
    return set(VENDOR.findall((directory / "lib" / "info.c").read_text(encoding="latin-1")))


def build(release):
    """Compiles libvorbis and libvorbisenc of a release into .cache/encoders and returns the
    directory of their shared libraries."""
    prefix = CACHE / f"xiph-{release}"
    library = prefix / "lib"
    if (library / "libvorbisenc.so.2").exists():
        return library
    source = checkout(release)
    subprocess.run(["sh", "autogen.sh", f"--prefix={prefix}", "--disable-static"], cwd=source, check=True, capture_output=True)
    # The examples do not link with today's toolchains; oggenc needs only the libraries.
    subprocess.run(["make", "-j8", "-C", "lib"], cwd=source, check=True, capture_output=True)
    subprocess.run(["make", "-C", "lib", "install"], cwd=source, check=True, capture_output=True)
    return library


def shim():
    path = CACHE / "version-shim.so"
    if not path.exists():
        with tempfile.TemporaryDirectory() as temp:
            source = pathlib.Path(temp) / "shim.c"
            source.write_text(SHIM)
            subprocess.run(["gcc", "-shared", "-fPIC", "-o", str(path), str(source)], check=True)
    return path


def oggenc(release, arguments, source, target):
    """Encodes with Ubuntu's oggenc and the release's libraries."""
    environment = dict(os.environ, LD_LIBRARY_PATH=str(build(release)), LD_PRELOAD=str(shim()))
    subprocess.run(["oggenc", "-Q", *arguments, "-o", str(target), str(source)], check=True, env=environment)


def analyzer_table():
    """The libvorbis vendor strings VorbisParser.Report.cs names, with their releases."""
    text = (ROOT / "src" / "Encspect.Formats.Vorbis" / "VorbisParser.Report.cs").read_text(encoding="utf-8")
    block = text[text.index("LibvorbisReleases"):]
    block = block[:block.index("};")]
    return dict(re.findall(r'\["([^"]+)"\] = "([^"]+)"', block))


def main():
    if "--vendors" not in sys.argv:
        raise SystemExit(__doc__)
    table = analyzer_table()
    problems = 0
    for release in TAGS:
        for vendor in sorted(vendor_strings(checkout(release))):
            listed = table.get(vendor)
            print(f"{release:6} {vendor!r} -> {listed}")
            problems += listed is None or release not in listed.split(" or ")
    if problems:
        raise SystemExit(f"{problems} vendor strings are missing from the analyzer's table or name another release")


if __name__ == "__main__":
    main()
