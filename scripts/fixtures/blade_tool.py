"""Builds BladeEnc (Tord Jansson, 1998 to 2001, LGPL) from its source releases, for encoder-written fixtures.

BladeEnc encodes MPEG-1 Layer III in plain stereo or mono only, without scalefactor scaling, and
from 0.82 on leaves ancillary data at most in its last frame, which is how mp3guessenc recognizes
it. 0.80 and 0.81 drain an overflowing bit reservoir into ancillary data mid-stream, as the ISO
dist10 encoder they grew out of does. Both of its sites, home.swipnet.se/~w-82625 (to 1999) and
bladeenc.mp3.no, are gone; the Wayback Machine keeps the source releases, each checked against a
pinned SHA-256. RELEASES holds one release per group that writes the same bytes: 0.81 writes what
0.80 does, 0.83 what 0.82 does, 0.85 what 0.84 does, and 0.93.10, 0.94.0 and 0.94.1 what 0.94.2
does. The 0.93.7 tarball calls itself 0.93.6.

The releases before 0.92 read the WAV header into a struct of longs and only work where long has
32 bits, so they are built with -m32 (gcc-multilib); the 32-bit and 64-bit builds of 0.92.0 write
the same bytes. 0.94.2 built here writes the same bytes as the Windows build Rarewares keeps
(blade0942.zip), and 0.82 the same mono and CRC-protected files as the Windows build of 0.82
(BladeEnc082.zip); its stereo encodes differ in a few frames, as x87 precision would explain. The
Windows builds of 0.90 and 0.91, whose sources are lost, write what 0.92.0 does at 44.1 kHz stereo.
"""

import pathlib
import re
import subprocess
import tarfile
import tempfile

import downloads

CACHE = pathlib.Path(__file__).resolve().parents[2] / ".cache" / "encoders"

OLD_SITE = "http://home.swipnet.se/~w-82625/source/"
SITE = "http://bladeenc.mp3.no:80/source/"

RELEASES = {
    "0.80": dict(archive="bladeenc-080-src-stable.tar.gz", url=OLD_SITE, timestamp="20140820162244",
                 sha256="a02ec6325093b5de53ac5c4b31a0d69d128c66fd0c655a645bb7ff9826fad11e"),
    "0.82": dict(archive="bladeenc-082-src-stable.tar.gz", url=OLD_SITE, timestamp="20140820162245",
                 sha256="0ebbd3524661b79d8b806459e7b24e531785f2f53540b159e5883da695ebbb35"),
    "0.84": dict(archive="bladeenc-084-src-unstable.tar.gz", url=OLD_SITE, timestamp="20140820162246",
                 sha256="a513a734668af872d39e1b999506db221de262bc350d124952539d23f92741e2"),
    "0.92.0": dict(archive="bladeenc-0.92.0-src-stable.tar.gz", url=SITE, timestamp="20010611205504",
                   sha256="41a9330b6d753ecd6165fbafc2504d6f797db9f8cac9cb2de7b272c93306618b"),
    "0.92.7": dict(archive="bladeenc-0.92.7-src-stable.tar.gz", url=SITE, timestamp="20060715004958",
                   sha256="d45c4e94084a592c5b824463a44695819450cf0963364f3e2978a856c2330f99"),
    "0.93.6": dict(archive="bladeenc-0.93.7-src-unstable-2.tar.gz", url=SITE, timestamp="20010612055911",
                   sha256="5cf51575deba81a81b2178aa1739ad4398620fd57ae6bc5eb17951fdf92fae33"),
    "0.94.2": dict(archive="bladeenc-0.94.2-src-stable.tar.gz", url=SITE, timestamp="20010611193234",
                   sha256="c5e9dd180f28d1865599d0755e36fa1a4c146bb5e1b7ba9ad099955261fc1164"),
}


def addresses(release):
    return [downloads.archived(release["url"] + release["archive"], release["timestamp"])]


def build(version="0.94.2"):
    """Downloads and checks the source of version, and compiles the encoder in a temporary directory."""
    release = RELEASES[version]
    directory = pathlib.Path(tempfile.mkdtemp(prefix="bladeenc-"))
    # The Wayback Machine resets connections now and then, so a checked copy is kept and reused.
    CACHE.mkdir(parents=True, exist_ok=True)
    path = CACHE / release["archive"]
    downloads.fetch(addresses(release), release["sha256"], path)
    with tarfile.open(path) as archive:
        archive.extractall(directory / "src", filter="data")
    # The tarballs nest their sources differently; the encoder is the directory with the most C files.
    root = directory / "src"
    sources = max((sorted(d.glob("*.c")) for d in [root, *root.rglob("*")] if d.is_dir()), key=len)
    if version == "0.80":
        patch_080(sources[0].parent / "main.c")
    binary = directory / "bladeenc"
    # The sources predate GCC 10's -fno-common default.
    flags = ["-m32"] if old(version) else []
    subprocess.run(["gcc", *flags, "-O2", "-w", "-fcommon", "-o", str(binary), *map(str, sources), "-lm"], check=True)
    banner = subprocess.run([str(binary), "-quit"], capture_output=True, text=True, stdin=subprocess.DEVNULL, errors="replace").stdout
    if f"BladeEnc {version} " not in banner:
        raise SystemExit(f"{binary} is not BladeEnc {version}")
    return binary


def old(version):
    """The releases before 0.92, which need a 32-bit build and have no -nocfg."""
    return not version.startswith("0.9")


def patch_080(path):
    # glibc's stdout is no constant, so 0.80's static initializer "textout = stdout" does not compile;
    # main() assigns it instead. 0.81 made the same change.
    text = path.read_text(encoding="latin-1")
    text = text.replace("FILE\t* textout = stdout;", "FILE\t* textout;", 1)
    text, count = re.subn(r"(main\( int argc, char\* argv\[\] \)\n\{\n(?:\t[^\n]*;\n)*)", r"\1\ttextout = stdout;\n", text, count=1)
    if count != 1:
        raise SystemExit("BladeEnc 0.80's main() is not where the patch expects it")
    path.write_text(text, encoding="latin-1")


def encode(binary, version, arguments, source, path):
    options = ["-quit", "-quiet"] + ([] if old(version) else ["-nocfg"])
    result = subprocess.run([str(binary), *options, *arguments, str(source), str(path)], capture_output=True, text=True,
                            stdin=subprocess.DEVNULL, errors="replace")
    if result.returncode != 0 or not path.exists():
        raise SystemExit(f"BladeEnc {version} {' '.join(arguments)} failed: {result.stdout[-500:]}")
