"""The Monkey's Audio source the fixtures and checks are built from: SDK 13.42 (MAC_1342_SDK.zip), pinned by SHA-256.

- The archive is on monkeysaudio.com (Matthew T. Ashland's site), under the 3-clause BSD license of its License.txt. The
  Internet Archive's Wayback Machine had no copy of this release yet when it was pinned (the site had released 13.43 days
  before), so the site is the only address; check_sources.py, which a scheduled workflow runs, says when it stops serving it.
  The source is not redistributed; its license allows it, and the parts of Encspect's decoder that follow it carry its notice
  (THIRD-PARTY-NOTICES.md).
- mac, the SDK's console tool (Source/Console, built by the SDK's own CMakeLists.txt as a static library and an executable
  that is named mac on Linux), writes the encoder-made fixtures and decodes them again: `mac in.wav out.ape -c2000`, `mac in.ape
  out.wav -d`, `mac in.ape -v` (quick verify with the descriptor MD5) and `-V` (full verify, which decodes and compares every frame CRC).
- The encoder writes the current format only (file version 3.99, descriptor and header), so no fixture of an older version can
  be made with it (docs/formats/monkeys-audio.md, "Not covered").

Usage: python3 scripts/fixtures/monkeys_audio_tool.py [directory]    builds mac in the directory and prints its path.
"""

import pathlib
import shutil
import subprocess
import sys
import tempfile
import zipfile

import downloads

NAME = "MAC_1342_SDK.zip"
SHA256 = "0a763b3592d13183bbd6c04b12eb637d364de16791602733a897b72df8e87326"
ADDRESSES = ["https://monkeysaudio.com/files/MAC_1342_SDK.zip"]
VERSION = "13.42"


def sources():
    """(name, SHA-256, addresses) of every file this script downloads."""
    yield NAME, SHA256, ADDRESSES


def fetch(directory):
    """The path of the archive in directory, downloaded and checked."""
    path = pathlib.Path(directory) / NAME
    downloads.fetch(ADDRESSES, SHA256, path)
    return path


def extract(directory):
    """The directory the SDK is extracted to (a folder in directory), after the archive's names are checked to stay inside it."""
    directory = pathlib.Path(directory)
    target = directory / "sdk"
    if not target.exists():
        with zipfile.ZipFile(fetch(directory)) as archive:
            for member in archive.namelist():
                if not (target / member).resolve().is_relative_to(target.resolve()):
                    raise ValueError(f"{member} is outside the SDK folder")
            archive.extractall(target)
    return target


def license_text(directory=None):
    """License.txt of the SDK, the text THIRD-PARTY-NOTICES.md reproduces."""
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="mac-"))
    return (extract(directory) / "License.txt").read_text(encoding="utf-8")


def build_mac(directory=None):
    """The path of mac, built in directory (a new temporary one by default) from the SDK with CMake and the system compiler."""
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="mac-"))
    directory.mkdir(parents=True, exist_ok=True)
    binary = directory / "build" / "mac"
    if binary.exists():
        return binary
    source = extract(directory)
    build = directory / "build"
    subprocess.run(["cmake", "-S", str(source), "-B", str(build), "-DCMAKE_BUILD_TYPE=Release", "-DBUILD_SHARED=OFF"], check=True, stdout=subprocess.DEVNULL)
    subprocess.run(["cmake", "--build", str(build), "--parallel", "4"], check=True, stdout=subprocess.DEVNULL)
    return binary


def run(binary, *arguments):
    """Runs mac and returns its output; the tool prints a progress line that rewrites itself, so only the last line counts."""
    result = subprocess.run([str(binary), *map(str, arguments)], capture_output=True, text=True, check=False)
    return result.returncode, (result.stdout + result.stderr).replace("\r", "\n")


if __name__ == "__main__":
    print(build_mac(sys.argv[1] if len(sys.argv) > 1 else None))
