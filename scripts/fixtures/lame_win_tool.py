"""LAME for Windows, run with Wine, for encoder-written fixtures of releases whose Windows builds
write other values than their sources compiled here.

LAME 3.91 as Rarewares keeps it (lame3.91.zip) writes a lowpass of 19600 Hz for --alt-preset
extreme and 20600 Hz for insane, where 3.91 compiled from its source tarball with gcc writes
19500 and 20500 Hz, as 3.90 and 3.92 do; getID3 lists the same values for 3.91. LAME 3.90.3 is
HydrogenAudio's build of the 3.90 line ("Compiled at http://www.hydrogenaudio.org"), which retuned
the alt-presets and of which no source release survives; it comes from oldversion.com, which hands
it out only through expiring links, so the project's private repository keeps the copy, checked
against a pinned SHA-256. Both are unpacked into .cache/encoders, where a copy with the right
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

CACHE = pathlib.Path(__file__).resolve().parents[2] / ".cache" / "encoders"

BUILDS = {
    "3.91": dict(
        archive="lame3.91.zip",
        sha256="e1e1419591ed922dda4d649630c25639df220a545a8c51e3c78a95994450daf4",
        addresses=["https://www.rarewares.org/rrw/files/lame/lame3.91.zip"],
        banner="LAME version 3.91 MMX"),
    "3.90.3": dict(
        archive="lame3.90.3_lame3.90.3.rar",
        sha256="5927ec1e9bab261ab61b1659765a6a9903f89b46b9c6c4e4ddd6977d3d8019b0",
        addresses=[downloads.private("encoders/lame3.90.3_lame3.90.3.rar")],
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
    result = run(binary, ["--version"], directory)
    if build["banner"] not in result.stdout + result.stderr:
        raise SystemExit(f"{binary} is not LAME {version}: {(result.stdout + result.stderr)[:200]}")
    return binary


def run(binary, arguments, directory):
    environment = dict(os.environ, WINEPREFIX=str(CACHE / "wine"), WINEDEBUG="-all")
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
