#!/usr/bin/env python3
"""libFLAC's releases (BSD-3-Clause), built from their source tarballs, for the fixtures that show what each compression level writes and for
the table of the levels in docs/formats/flac.md.

The tarballs are pinned by SHA-256 in SOURCES and come from Xiph.Org's release server. build(version) compiles a release's flac program in
.cache/encoders (the library and the program only: no Ogg support, no documentation, no assembly) and returns its path; the program is
used as a black box, and what each level sets is read from its own `flac --help`. 1.4.3 is the flac package of Ubuntu 24.04, run as `flac`.

    python3 flac_tool.py --levels   prints each release's text for the levels 0 to 8 (needs a C toolchain)
"""

import pathlib
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile

import downloads

ROOT = pathlib.Path(__file__).resolve().parents[2]
CACHE = ROOT / ".cache" / "encoders"

SYSTEM_VERSION = "1.4.3"

RELEASES = "https://ftp.osuosl.org/pub/xiph/releases/flac/"
MIRROR = "https://downloads.xiph.org/releases/flac/"

# Release: (archive, SHA-256). The vendor string a release writes is "reference libFLAC <release> <date>".
SOURCES = {
    "1.0.3": ("flac-1.0.3.tar.gz", "82f1d367e750a566b8c51aa909ec621a742f41594ae81b14672f8320b0d11868"),
    "1.0.4": ("flac-1.0.4.tar.gz", "4846413132b2b9e04296a2aa3e8205d9cfa9c62fa72aabd5aaaef3ddf2964aa8"),
    "1.1.0": ("flac-1.1.0.tar.gz", "897b0ac415b36ea241932c56852b8d6f3e083d0db66be9a96fc83c9cc42932a1"),
    "1.1.1": ("flac-1.1.1.tar.gz", "fb8d5df895e785e8eb1687cc532faf8f5bc41c84e67b4f1c143786df698bcd03"),
    "1.1.2": ("flac-1.1.2.tar.gz", "ce4f7d11b3c04a7368c916ca4abc284dd0c0256f461dfb7f07df1ab445e7a5c0"),
    "1.1.3": ("flac-1.1.3.tar.gz", "ba0bf8b4720537b08aba9b0d2d5b3fbe796ce9957a56334354a2f95694866a7c"),
    "1.1.4": ("flac-1.1.4.tar.gz", "b774d573b971f71f84386d34b96d388dd2dae3f15735acf91dbd4adff0ce4114"),
    "1.2.0": ("flac-1.2.0.tar.gz", "e0f8525025addf40e9a4fab88bbb0d34b3d5835676600c6be83a20a8dfbb5e4e"),
    "1.2.1": ("flac-1.2.1.tar.gz", "9635a44bceb478bbf2ee8a785cf6986fba525afb5fad1fd4bba73cf71f2d3edf"),
    "1.3.0": ("flac-1.3.0.tar.xz", "fa2d64aac1f77e31dfbb270aeb08f5b32e27036a52ad15e69a77e309528010dc"),
    "1.3.1": ("flac-1.3.1.tar.xz", "4773c0099dba767d963fd92143263be338c48702172e8754b9bc5103efe1c56c"),
    "1.3.2": ("flac-1.3.2.tar.xz", "91cfc3ed61dc40f47f050a109b08610667d73477af6ef36dcad31c31a4a8d53f"),
    "1.3.3": ("flac-1.3.3.tar.xz", "213e82bd716c9de6db2f98bcadbc4c24c7e2efe8c75939a1a84e28539c4e1748"),
    "1.3.4": ("flac-1.3.4.tar.xz", "8ff0607e75a322dd7cd6ec48f4f225471404ae2730d0ea945127b1355155e737"),
    "1.4.0": ("flac-1.4.0.tar.xz", "af41c0733c93c237c3e52f64dd87e3b0d9af38259f1c7d11e8cbf583c48c2506"),
    "1.4.1": ("flac-1.4.1.tar.xz", "91303c3e5dfde52c3e94e75976c0ab3ee14ced278ab8f60033a3a12db9209ae6"),
    "1.4.2": ("flac-1.4.2.tar.xz", "e322d58a1f48d23d9dd38f432672865f6f79e73a6f9cc5a5f57fcaa83eb5a8e4"),
    "1.5.0": ("flac-1.5.0.tar.xz", "f2c1c76592a82ffff8413ba3c4a1299b6c7ab06c734dee03fd88630485c2b920"),
}

# Releases whose program does not compile with a current toolchain: their tarballs are pinned (the tables of the levels are read from the source of
# src/flac/main.c, and 1.0.4's and 1.1.1's are the ones of 1.1.0 and 1.1.2), but no program is built.
NOT_BUILT = {"1.0.4", "1.1.1"}

# 1.2.0's encode.c does not compile without Ogg support, which the other releases build without.
WITH_OGG = {"1.2.0"}

LEVEL_LINE = re.compile(r"^\s+-[0-8](?:, --(?:compression-level|fast|best)|\s+: synonymous)")


def addresses(archive):
    return [RELEASES + archive, MIRROR + archive]


def sources():
    for archive, digest in SOURCES.values():
        yield archive, digest, addresses(archive)


def fetch(version):
    archive, digest = SOURCES[version]
    CACHE.mkdir(parents=True, exist_ok=True)
    path = CACHE / archive
    downloads.fetch(addresses(archive), digest, path)
    return path


# libtool's wrapper names the directory the release was built in, so a built tree is run through this script, which finds the library next to itself.
RUNNER = """#!/bin/sh
d=$(dirname "$(readlink -f "$0")")
LD_LIBRARY_PATH="$d/src/libFLAC/.libs:$d/src/libOggFLAC/.libs${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" exec "$d/src/flac/.libs/flac" "$@"
"""


def build(version):
    """Compiles release `version` in .cache/encoders and returns the path of a script that runs its flac program."""
    if version == SYSTEM_VERSION:
        return pathlib.Path(shutil.which("flac") or "flac")
    if version in NOT_BUILT:
        raise SystemExit(f"flac {version} does not build with a current toolchain")
    tree = CACHE / f"flac-{version}"
    program = tree / "flac-run"
    if program.exists():
        return program
    with tempfile.TemporaryDirectory() as temp:
        with tarfile.open(fetch(version)) as files:
            files.extractall(temp, filter="data")
        source = next(p for p in pathlib.Path(temp).iterdir() if p.is_dir())
        subprocess.run(["./configure", "-q", *([] if version in WITH_OGG else ["--disable-ogg"]), "--disable-static", "--disable-doxygen-docs", "--disable-xmms-plugin",
                        "--disable-cpplibs", "--disable-examples", "--disable-docs", "--disable-asm-optimizations"], cwd=source, check=True, capture_output=True)
        subprocess.run(["make", "-j4", "-k"], cwd=source, check=False, capture_output=True)
        if not (source / "src" / "flac" / "flac").exists():
            raise SystemExit(f"flac {version} did not build")
        shutil.rmtree(tree, ignore_errors=True)
        shutil.move(str(source), str(tree))
    program.write_text(RUNNER, encoding="utf-8")
    program.chmod(0o755)
    return program


def levels(version):
    """The lines of `flac --help` that give levels 0 to 8, with the continuation lines that follow them."""
    # Releases before 1.4 write the help to the error stream.
    result = subprocess.run([str(build(version)), "--help"], capture_output=True, text=True)
    text = (result.stdout or result.stderr).splitlines()
    found = []
    for index, line in enumerate(text):
        if LEVEL_LINE.match(line):
            found.append(line.strip())
            while index + 1 < len(text) and text[index + 1].startswith(" " * 20):
                index += 1
                found.append("  " + text[index].strip())
    return found


def main():
    if "--levels" not in sys.argv:
        raise SystemExit(__doc__)
    for version in [*SOURCES, SYSTEM_VERSION]:
        if version in NOT_BUILT:
            continue
        print(f"== flac {version}")
        print("\n".join(levels(version)))


if __name__ == "__main__":
    main()
