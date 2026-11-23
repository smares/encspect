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

LEVEL_LINE = re.compile(r"^\s+-[0-8], --(?:compression-level|fast|best)")


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
LD_LIBRARY_PATH="$d/src/libFLAC/.libs${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" exec "$d/src/flac/.libs/flac" "$@"
"""


def build(version):
    """Compiles release `version` in .cache/encoders and returns the path of a script that runs its flac program."""
    if version == SYSTEM_VERSION:
        return pathlib.Path(shutil.which("flac") or "flac")
    tree = CACHE / f"flac-{version}"
    program = tree / "flac-run"
    if program.exists():
        return program
    with tempfile.TemporaryDirectory() as temp:
        with tarfile.open(fetch(version)) as files:
            files.extractall(temp, filter="data")
        source = next(p for p in pathlib.Path(temp).iterdir() if p.is_dir())
        subprocess.run(["./configure", "-q", "--disable-ogg", "--disable-static", "--disable-doxygen-docs", "--disable-xmms-plugin",
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
        print(f"== flac {version}")
        print("\n".join(levels(version)))


if __name__ == "__main__":
    main()
