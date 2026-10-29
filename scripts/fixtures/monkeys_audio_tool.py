"""The Monkey's Audio source the fixtures and checks are built from: SDK 13.42 (MAC_1342_SDK.zip), pinned by SHA-256.

- The archive is on monkeysaudio.com (Matthew T. Ashland's site), under the 3-clause BSD license of its License.txt. The
  Internet Archive's Wayback Machine had no copy of this release yet when it was pinned (the site had released 13.43 days
  before), so the site is the only address; check_sources.py, which a scheduled workflow runs, says when it stops serving it.
  The source is not redistributed; its license allows it, and the parts of Encspect's decoder that follow it carry its notice
  (THIRD-PARTY-NOTICES.md).
- mac, the SDK's console tool (Source/Console, built by the SDK's own CMakeLists.txt as a static library and an executable
  that is named mac on Linux), writes the encoder-made fixtures and decodes them again: `mac in.wav out.ape -c2000`, `mac in.ape
  out.wav -d`, `mac in.ape -v` (quick verify with the descriptor MD5) and `-V` (full verify, which decodes and compares every frame CRC).
- The encoder writes the current format only (file version 3.99, descriptor and header), so the fixtures of older versions come from
  monkeys_audio_legacy.py, whose files `mac -V` verifies (docs/formats/monkeys-audio.md, "Decoding").
- build_interim_mac builds a second mac from the same source with one line of the encoder changed, so that it writes 24-bit files the way the encoders
  of Monkey's Audio 5.01 to 8.51 did (their predictor summed the first stage in 64 bits, which the decoder's "interim mode" undoes,
  Interim.cpp). The unchanged mac verifies and decodes those files through that mode; no unchanged encoder can write one.
- build_w64_mac builds a third mac with one line of the SDK's GUID structure changed (its first field is 32 bits wide), so that the SDK's Wave64 reader
  accepts files on 64-bit Linux and macOS, where the unchanged reader refuses every Wave64 file; the files it writes are read by the unchanged mac.

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


# The one line of the encoder (NewPredictor.cpp, CPredictorCompress::CompressValue, the branch for samples over 16 bits) that differs: the 2019-2022
# encoders did not cut the two sums to 32 bits before the shift, and the decoder of that period (Interim.cpp, CPredictorDecompressInterim) adds them in 64
# bits. The source file has CRLF line ends; the line is matched without its end.
INTERIM_LINE = "nOutput = nA - static_cast<INTTYPE>((static_cast<int>(nPredictionA) + (static_cast<int>(nPredictionB) >> 1)) >> 10);"
INTERIM_PATCHED = "nOutput = nA - static_cast<INTTYPE>((nPredictionA + (nPredictionB >> 1)) >> 10);"


def build_patched_mac(directory, path, line, replacement):
    """The path of a mac built in directory from a copy of the SDK with the one line of the source file `path` (relative to the SDK's Source folder) replaced.
    The source files have CRLF line ends; the line is matched without its end."""
    directory = pathlib.Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    binary = directory / "build" / "mac"
    if binary.exists():
        return binary
    source = directory / "sdk"
    shutil.copytree(extract(directory / "original"), source, dirs_exist_ok=True)
    file = source / "Source" / path
    text = file.read_bytes().decode("utf-8")
    if text.count(line) != 1:
        raise RuntimeError("the line of the source to change is not in the SDK source exactly once")
    file.write_bytes(text.replace(line, replacement).encode("utf-8"))
    build = directory / "build"
    subprocess.run(["cmake", "-S", str(source), "-B", str(build), "-DCMAKE_BUILD_TYPE=Release", "-DBUILD_SHARED=OFF"], check=True, stdout=subprocess.DEVNULL)
    subprocess.run(["cmake", "--build", str(build), "--parallel", "4"], check=True, stdout=subprocess.DEVNULL)
    return binary


def build_interim_mac(directory=None):
    """The path of a mac whose encoder writes 24-bit files in the 64-bit first stage of the 2019-2022 encoders, built in directory from a copy of the SDK
    with INTERIM_LINE replaced."""
    return build_patched_mac(directory or tempfile.mkdtemp(prefix="mac-interim-"), "MACLib/NewPredictor.cpp", INTERIM_LINE, INTERIM_PATCHED)


# The SDK's Wave64 reader (WAVInputSource.cpp, CW64InputSource) reads and compares GUIDs as the 16-byte structure of Windows, whose first field is a 32-bit
# unsigned long. NoWindows.h, the SDK's header for platforms other than Windows, declares that field as an unsigned long too, which is 64 bits on LP64 platforms
# (64-bit Linux and macOS): the structure is 24 bytes there, and the reader refuses every Wave64 file with error 1002. The patch gives the field the 32 bits it has on Windows.
GUID_LINE = "    unsigned long  Data1;"
GUID_PATCHED = "    unsigned int   Data1;"


def build_w64_mac(directory=None):
    """The path of a mac that reads Wave64 files on 64-bit Linux: the SDK with the first field of its GUID structure made 32 bits wide (GUID_LINE). The
    files it writes are read by the unchanged mac like any other; only its input source differs."""
    return build_patched_mac(directory or tempfile.mkdtemp(prefix="mac-w64-"), "Shared/NoWindows.h", GUID_LINE, GUID_PATCHED)


def run(binary, *arguments):
    """Runs mac and returns its output; the tool prints a progress line that rewrites itself, so only the last line counts."""
    result = subprocess.run([str(binary), *map(str, arguments)], capture_output=True, text=True, check=False)
    return result.returncode, (result.stdout + result.stderr).replace("\r", "\n")


if __name__ == "__main__":
    print(build_mac(sys.argv[1] if len(sys.argv) > 1 else None))
