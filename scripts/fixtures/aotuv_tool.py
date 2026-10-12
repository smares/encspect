#!/usr/bin/env python3
"""aoTuV, Aoyumi's tuned libvorbis (BSD-style license), for encoder-written Vorbis fixtures and for
the vendor strings the analyzer names it by.

aoTuV's site (formerly aotuv.org) now lives in the repository AO-Yumi/aotuv_web on GitHub, served
as ao-yumi.github.io/aotuv_web; it keeps every source release from beta 1a to beta 6.03 (2015)
under source_code/, which SOURCES pins by SHA-256 and fetches from the repository at a fixed
commit or from the site. The 2018 and 2020 rebuilds of beta 6.03 are tags of AO-Yumi/vorbis_aotuv,
pinned by their commits. Rarewares builds oggenc2 against aoTuV, and against Lancer, an optimized
aoTuV; WINDOWS pins those builds, which write vendor strings of their own and run under Wine.

    python3 aotuv_tool.py --vendors   prints each release's vendor string (lib/info.c) and checks
                                      that the analyzer's table (VorbisParser.Report.cs) lists it

build(release) compiles a release's libvorbis and libvorbisenc in .cache/encoders, whose shared
libraries Ubuntu's oggenc (vorbis-tools 1.4.2) loads in place of the system's through
LD_LIBRARY_PATH; releases before beta 5.5 lack vorbis_version_string, which that oggenc needs.
"""

import io
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import zipfile

import downloads
import xing_tool

ROOT = pathlib.Path(__file__).resolve().parents[2]
CACHE = ROOT / ".cache" / "encoders"

SITE_COMMIT = "5559778ccc28ba714d73ee29fb7be92105059cdf"
RAW = f"https://raw.githubusercontent.com/AO-Yumi/aotuv_web/{SITE_COMMIT}/source_code/"
PAGES = "https://ao-yumi.github.io/aotuv_web/source_code/"

# Release name: (archive, SHA-256). Patches against libvorbis carry the vendor string too.
SOURCES = {
    "b1a": ("libvorbis-aotuv_b1a.tar.gz.tgz", "345fc55e3ef90fd706632411dc31987ed4468949701cdc75705e016c556ce568"),
    "b2": ("libvorbis-aotuv_b2.tar.gz.tgz", "d6f5b0c903060e29676beef642105e77ada83f5d2645f09363ff519507e1ccfc"),
    "b3": ("libvorbis-aotuv_b3.tar.gz.tgz", "be19622a994dfd9d0eea1902a386bfb328300ea4e3ca66dbedcd9bd4d36efd92"),
    "b4": ("libvorbis-aotuv_b4.tar.gz.tgz", "8ad2599c57dcda03155f70d41b285795872481f20573e21c85cf2765dc26ea00"),
    "b4.5": ("libvorbis-aotuv_b4.5.tar.gz.tgz", "505a7e6d5d5c0b8dc0c06349d0b97e58cb0a2dbeeaa9831ebfc56c95e1b59bf4"),
    "b4.51": ("libvorbis-aotuv_b4.51.tar.gz.tgz", "bc75677a5934ebe48e4c56a79e777355d3d1c5c318aa3d67cbb122d8b888fca5"),
    "r1": ("libvorbis-aotuv_r1.tar.bz2", "f9dd79c56da02183b40d5babd02cb50daf4dcde77c3778acb2d39b09088bc4f3"),
    "r1 patch": ("aotuvr1_patch-libvorbis1.1.2.tar.bz2", "070d9f134d24af4380cba89e3f909b64f8776bf4c57f23b07695a3dc7f5e2865"),
    "b5": ("libvorbis-aotuv_b5.tar.bz2", "b209147c05d8d798292a1c4319421a16cde36adb7f0b81350931f96360e9920b"),
    "b5 patch": ("aotuvb5_patch-libvorbis1.1.2.tar.bz2", "1024548624758ecda23323b464945b0b2742bbc6f848a79c47ad186791df7964"),
    "b5.5": ("libvorbis-aotuv_b5.5.tar.bz2", "5d2f1a4627d61b9fc611dd7ca1c20d348aba27fc14abbd0b72636a51ab86faf0"),
    "b5.5 patch": ("aotuvb5.5_patch-libvorbis1.2.0.tar.bz2", "9637aa0b10189fcdfc58e8d22fbcb06a51c5d3a05016dfa52eb1cde5a924e26c"),
    "b5.6": ("libvorbis-aotuv_b5.6.tar.bz2", "f7f158b9d75c69ead8761d60e5107e4d6830311517df88d42d305fee610020dd"),
    "b5.61": ("libvorbis-aotuv_b5.61.tar.bz2", "244c9a0b9ffdbe322197b8e1fffddf93a1becfec420c37296c193caaf2aada03"),
    "b5.61 zip": ("aotuv-b5.61_20081215.zip", "feb047c01398e94ca6fa0f5036734f7a49966920368496f808bcd6dd06302420"),
    "b5.7": ("libvorbis-aotuv_b5.7.tar.bz2", "8c6e3440a956123d508f693851ef8d016e2b83f66daf54666041ff6d5284ffe4"),
    "b6": ("libvorbis-aotuv_b6.tar.bz2", "fb19e1d46a54c39f61f0d91a00683bfd085e0cf035c4288f91df9ace733e4339"),
    "b6.01": ("libvorbis-aotuv_b6.01.tar.bz2", "1be92261e916c59c26a4436485bebbdeb1a6861fb8a2fa499807da7845cf4952"),
    "b6.02": ("libvorbis-aotuv_b6.02.tar.bz2", "5ecb92701b7f6a8de0404ee4d98f8b825524a71d1cae55269b8cbefe8d810d2c"),
    "b6.03": ("libvorbis-aotuv_b6.03.tar.bz2", "95455420f07e4b3abdf32bda9f5921e9ed3f1afdc3739098dc090150a42fd7fd"),
    "b6.03 zip": ("aotuv-b6.03_20110424.zip", "0d3755a4b4ac3a55f41241205038cb0ab64e282ff0353681e935d5218ce02d6d"),
    "b6.03 (2014)": ("libvorbis-aotuv_b6.03_2014.tar.bz2", "5c11bd73e947ab862818fe80db0ab5eaefdd4ac670bf185dd8e7a7c6ad76f2ad"),
    "b6.03 (2015)": ("libvorbis-aotuv_b6.03_2015.tar.bz2", "fba6724d2bc2b6a911a25e60f21a45749d507f181a9e150415ce41e4d03bc08f"),
}

# The 2018 and 2020 rebuilds: tags of the source repository, pinned by their commits.
REPOSITORY = "https://github.com/AO-Yumi/vorbis_aotuv.git"
TAGS = {
    "b6.03 (2018)": ("beta6.03-2018", "01157fefa37709bb65bf06f7c7ee4b91c97ac8a4"),
    "b6.03 (2020)": ("beta6.03-2020", "1eda0f0055d307cb2a8d1a878d20e3c499e76e1e"),
}

# Rarewares' Windows builds of oggenc 2.88: aoTuV b6.03 (2020) and Lancer's SSE2 build of it.
RAREWARES = "https://www.rarewares.org/files/ogg/"
WINDOWS = {
    "aotuv-rarewares": ("oggenc2.88-1.3.7-aoTuVb6.03-generic.zip", "9913c7a09717d4b2781be4d59cb4f0cc45ca8bb57c01bfbb0e2f56c860b0539a"),
    "lancer-sse2": ("oggenc2.88-1.3.7-aoTuVb6.03-LancerSSE2-20240514.zip", "0276f3f1ad064573a0986f037789f9a9943f559ae5f9ed866948b5e32e80f430"),
}

VENDOR = re.compile(r'(?:ENCODE_VENDOR_STRING\s+|char temp\[\]\s*=\s*)"(AO; aoTuV[^"]*)"')


def addresses(archive):
    return [RAW + archive, PAGES + archive]


def windows_addresses(archive):
    return [RAREWARES + archive]


def fetch(release):
    archive, digest = SOURCES[release]
    CACHE.mkdir(parents=True, exist_ok=True)
    path = CACHE / archive
    downloads.fetch(addresses(archive), digest, path)
    return path


def checkout(release):
    """The tree of a tagged rebuild, checked out at its pinned commit."""
    tag, commit = TAGS[release]
    directory = CACHE / f"vorbis_aotuv-{tag}"
    if not directory.exists():
        subprocess.run(["git", "clone", "-q", REPOSITORY, str(directory)], check=True)
    subprocess.run(["git", "-C", str(directory), "checkout", "-q", commit], check=True)
    actual = subprocess.run(["git", "-C", str(directory), "rev-parse", f"{tag}^{{commit}}"], capture_output=True, text=True).stdout.strip()
    if actual != commit:
        raise SystemExit(f"{tag} is commit {actual}, not the pinned {commit}")
    return directory


def unpack(path, target):
    if path.suffix == ".zip":
        with zipfile.ZipFile(io.BytesIO(path.read_bytes())) as files:
            files.extractall(target)
    else:
        with tarfile.open(path) as files:
            files.extractall(target, filter="data")


def vendor_strings(directory):
    found = set()
    for path in directory.rglob("*"):
        if path.is_file() and path.suffix in (".c", ".h", ".diff", ".patch"):
            for line in path.read_text(encoding="latin-1").splitlines():
                # A patch's removed lines hold libvorbis' own string; only its added lines count.
                if line.startswith("-"):
                    continue
                found.update(VENDOR.findall(line))
    return found


def build(release):
    """Compiles libvorbis and libvorbisenc of a release into .cache/encoders and returns the
    directory of their shared libraries."""
    prefix = CACHE / f"aotuv-{release.replace(' ', '').replace('(', '-').replace(')', '')}"
    library = prefix / "lib"
    if (library / "libvorbisenc.so.2").exists():
        return library
    with tempfile.TemporaryDirectory() as temp:
        if release in TAGS:
            source = checkout(release)
            subprocess.run(["sh", "autogen.sh"], cwd=source, check=True, capture_output=True)
        else:
            unpack(fetch(release), pathlib.Path(temp))
            source = next(p for p in pathlib.Path(temp).iterdir() if p.is_dir())
        subprocess.run(["sh", "./configure", "-q", f"--prefix={prefix}", "--disable-static"], cwd=source, check=True, capture_output=True)
        # The examples do not link with today's toolchains; oggenc needs only the libraries.
        subprocess.run(["make", "-j8", "-C", "lib"], cwd=source, check=True, capture_output=True)
        subprocess.run(["make", "-C", "lib", "install"], cwd=source, check=True, capture_output=True)
    return library


def oggenc(release, arguments, source, target):
    """Encodes with Ubuntu's oggenc and the release's libraries."""
    environment = dict(os.environ, LD_LIBRARY_PATH=str(build(release)))
    subprocess.run(["oggenc", "-Q", *arguments, "-o", str(target), str(source)], check=True, env=environment)


def windows(name, arguments, source, target):
    """Encodes with a Rarewares build of oggenc2 under Wine, from a temporary directory."""
    archive, digest = WINDOWS[name]
    CACHE.mkdir(parents=True, exist_ok=True)
    path = CACHE / archive
    downloads.fetch(windows_addresses(archive), digest, path)
    with tempfile.TemporaryDirectory() as temp:
        unpack(path, pathlib.Path(temp))
        shutil.copy(source, pathlib.Path(temp) / "in.wav")
        environment = dict(os.environ, WINEPREFIX=str(xing_tool.WINE_PREFIX), WINEDEBUG="-all")
        subprocess.run(["wine", "oggenc2.exe", "-Q", *arguments, "-o", "out.ogg", "in.wav"], cwd=temp, env=environment, capture_output=True,
                       stdin=subprocess.DEVNULL, timeout=600)
        output = pathlib.Path(temp) / "out.ogg"
        if not output.exists() or output.stat().st_size == 0:
            raise SystemExit(f"oggenc2 ({name}) {' '.join(arguments)} failed")
        shutil.move(output, target)


def analyzer_table():
    """The aoTuV vendor strings VorbisParser.Report.cs names, with their names."""
    text = (ROOT / "src" / "Encspect.Formats.Vorbis" / "VorbisParser.Report.cs").read_text(encoding="utf-8")
    block = text[text.index("AotuvReleases"):]
    block = block[:block.index("};")]
    return dict(re.findall(r'\["([^"]+)"\] = "([^"]+)"', block))


def main():
    if "--vendors" not in sys.argv:
        raise SystemExit(__doc__)
    table = analyzer_table()
    problems = 0
    with tempfile.TemporaryDirectory() as temp:
        for release in SOURCES:
            target = pathlib.Path(temp) / release.replace(" ", "_")
            unpack(fetch(release), target)
            for vendor in sorted(vendor_strings(target)):
                listed = table.get(vendor)
                print(f"{release:14} {vendor!r} -> {listed}")
                problems += listed is None
        for release in TAGS:
            for vendor in sorted(vendor_strings(checkout(release) / "lib")):
                listed = table.get(vendor)
                print(f"{release:14} {vendor!r} -> {listed}")
                problems += listed is None
    if problems:
        raise SystemExit(f"{problems} vendor strings are missing from the analyzer's table")


if __name__ == "__main__":
    main()
