"""LAME for Windows, run with Wine, for encoder-written fixtures of releases whose Windows builds
write other values than their sources compiled here, and of releases that have no source here.

Rarewares' builds of 3.20 to 3.89b (www.rarewares.org/rrw/lame.php) are the releases before the LAME
tag, which 3.90 brought. They are here for what their streams show without one: the Xing header of
their VBR modes with "LAME3.xx" after the table of contents from 3.20 on, "LAME3.80" and later in
the padding of CBR frames (3.87 writes "LAME3.87 (beta 1, Sep 29 2000)"), and nothing at all in
CBR streams before 3.80. The 3.20 to 3.70 builds print their banner when run with no arguments
and fail on --version, so they are probed that way.

LAME 3.91 as Rarewares keeps it (lame3.91.zip) writes a lowpass of 19600 Hz for --alt-preset
extreme and 20600 Hz for insane, where 3.91 compiled from its source tarball with gcc writes
19500 and 20500 Hz, as 3.90 and 3.92 do; getID3 lists the same values for 3.91. LAME 3.90.3 is
HydrogenAudio's build of the 3.90 line ("Compiled at http://www.hydrogenaudio.org"), which retuned
the alt-presets and of which no source release survives; it comes from oldversion.com, which hands
it out only through expiring links, so the project's private repository keeps the copy, checked
against a pinned SHA-256. All are unpacked into .cache/encoders, where a copy with the right
SHA-256 is used as it is. 7z unpacks the RAR.
"""

import io
import os
import pathlib
import shutil
import subprocess
import tempfile
import zipfile

import downloads
import xing_tool

CACHE = pathlib.Path(__file__).resolve().parents[2] / ".cache" / "encoders"

BUILDS = {
    "3.20": dict(
        archive="lame3.20.zip",
        sha256="4227ba7e0be851a510dd84df0c85d24fbb76975c3140deb450ac263a4bae8a3b",
        addresses=["https://www.rarewares.org/rrw/files/lame/lame3.20.zip"], probe=[],
        banner="LAME version 3.20 (www.sulaco.org/mp3)"),
    "3.34": dict(
        archive="lame3.34.zip",
        sha256="d0391dae81365de3bdb99d896f06feabb8fc47e9b57abf4928fd088303e91d41",
        addresses=["https://www.rarewares.org/rrw/files/lame/lame3.34.zip"], probe=[],
        banner="LAME version 3.34 (www.sulaco.org/mp3)"),
    "3.57": dict(
        archive="lame3.57.zip",
        sha256="9616d728a57ab77d856bd7ad7b46ac2c690c9b1a0a8b30d380887b64a2c9d8b2",
        addresses=["https://www.rarewares.org/rrw/files/lame/lame3.57.zip"], probe=[],
        banner="LAME version 3.57 (www.sulaco.org/mp3)"),
    "3.70": dict(
        archive="lame3.70.zip",
        sha256="4d3b0bcadaee5a9f7e23e4f69d33138e0384d03aeb052dee0c7c5c78c405346e",
        addresses=["https://www.rarewares.org/rrw/files/lame/lame3.70.zip"], probe=[],
        banner="LAME version 3.70 (www.sulaco.org/mp3)"),
    "3.80": dict(
        archive="lame3.80.zip",
        sha256="20d12997c7a55aa55880850fdc28e84643ad0a547084f66fe7c385e7a3214027",
        addresses=["https://www.rarewares.org/rrw/files/lame/lame3.80.zip"],
        banner="LAME version 3.80 (www.sulaco.org/mp3)"),
    "3.84": dict(
        archive="lame3.84.zip",
        sha256="a42e5be8bd69c323eef1c53e6dc299f7130b7f2806c0e5990ecbafc03107f080",
        addresses=["https://www.rarewares.org/rrw/files/lame/lame3.84.zip"],
        banner="LAME version 3.84 (www.sulaco.org/mp3)"),
    "3.87": dict(
        archive="lame3.87.zip",
        sha256="5890d0e346a1c1fd9b0fc1cdaae0913655d0571b5f7eab5f8c25f4e7ecbc001f",
        addresses=["https://www.rarewares.org/rrw/files/lame/lame3.87.zip"],
        banner="LAME version 3.87 (beta 1"),
    "3.88": dict(
        archive="lame3.88.zip",
        sha256="5db0518c9773d0b0aae1e46f8e300eae28458c9665a57cda2e2e4721329ff7a4",
        addresses=["https://www.rarewares.org/rrw/files/lame/lame3.88.zip"],
        banner="LAME version 3.88 MMX"),
    "3.89": dict(
        archive="lame3.89.zip",
        sha256="6a0d8fb57b013cbf3a082444bedb6f85eaacd69a257d8787a82872b1064f81f2",
        addresses=["https://www.rarewares.org/rrw/files/lame/lame3.89.zip"],
        banner="LAME version 3.89 MMX"),
    "3.91": dict(
        archive="lame3.91.zip",
        sha256="e1e1419591ed922dda4d649630c25639df220a545a8c51e3c78a95994450daf4",
        addresses=["https://www.rarewares.org/rrw/files/lame/lame3.91.zip"],
        banner="LAME version 3.91 MMX"),
    "3.90.3": dict(
        archive="lame3.90.3_lame3.90.3.rar",
        sha256="5927ec1e9bab261ab61b1659765a6a9903f89b46b9c6c4e4ddd6977d3d8019b0",
        addresses=[downloads.private("mirrors/tools/encoders/lame3.90.3_lame3.90.3.rar")],
        banner="LAME version 3.90.3 MMX"),
}


def fetch(version):
    """Downloads and checks the build of version, unpacks lame.exe into its own directory under
    .cache/encoders and returns its path."""
    build = BUILDS[version]
    CACHE.mkdir(parents=True, exist_ok=True)
    archive = CACHE / build["archive"]
    downloads.fetch(build["addresses"], build["sha256"], archive)
    directory = CACHE / f"lame-{version}-windows"
    if directory.exists():
        shutil.rmtree(directory)
    directory.mkdir()
    if archive.suffix == ".zip":
        with zipfile.ZipFile(io.BytesIO(archive.read_bytes())) as files:
            (directory / "lame.exe").write_bytes(files.read("lame.exe"))
    else:
        subprocess.run(["7z", "x", "-y", f"-o{directory}", str(archive), "lame.exe"], check=True, capture_output=True)
    binary = directory / "lame.exe"
    result = run(binary, build.get("probe", ["--version"]), directory)
    if build["banner"] not in result.stdout + result.stderr:
        raise SystemExit(f"{binary} is not LAME {version}: {(result.stdout + result.stderr)[:200]}")
    return binary


def run(binary, arguments, directory):
    environment = dict(os.environ, WINEPREFIX=str(xing_tool.WINE_PREFIX), WINEDEBUG="-all")
    return subprocess.run(["wine", str(binary), *arguments], cwd=directory, env=environment, capture_output=True, text=True,
                          stdin=subprocess.DEVNULL, timeout=600, errors="replace")


def encode(binary, arguments, source, path):
    """Encodes source to path from a temporary directory, where Wine reads and writes plain names."""
    with tempfile.TemporaryDirectory() as temp:
        shutil.copy(source, pathlib.Path(temp) / "in.wav")
        run(binary, [*arguments, "in.wav", "out.mp3"], temp)
        output = pathlib.Path(temp) / "out.mp3"
        if not output.exists() or output.stat().st_size == 0:
            raise SystemExit(f"LAME {binary.parent.name} {' '.join(arguments)} failed")
        shutil.move(output, path)
