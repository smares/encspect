"""Builds EncSpot Console 2.01 (Francis Russell's port of GuerillaSoft's console source,
third_party/encspot) and reads its report; the cross-checks compare its reservoir figures, its
scalefactor scaling share, bitrate histogram, frame count, mode and encoder guess with the
analyzer's. Its quality rating is an opinion about encoders, which the analyzer does not give, so
it is not read."""

import functools
import pathlib
import re
import subprocess
import tempfile

SOURCE = pathlib.Path(__file__).resolve().parents[2] / "third_party" / "encspot"
FILES = ["src/EncSpotConsole.cpp", "lib/Common.cpp", "lib/ID3tag.cpp", "lib/MediaFile.cpp", "lib/Mp3File.cpp", "lib/Mp3Structs.cpp"]


@functools.cache
def binary():
    """Compiles EncSpot with the flags of its CMakeLists.txt (-O2), once per process."""
    directory = pathlib.Path(tempfile.mkdtemp(prefix="encspot-"))
    path = directory / "encspot"
    subprocess.run(["c++", "-O2", "-w", "-I", str(SOURCE / "include"), "-o", str(path), *(str(SOURCE / f) for f in FILES)], check=True)
    return str(path)


def report(path):
    """EncSpot's report of a file: its labeled lines ("Frames", "Av. Reservoir", "Scalefac",
    "Encoder" and so on) by label, and "bitrates", the share of the frames at each bitrate in kbps
    as printed; or {"error": message} where it does not analyze the file."""
    result = subprocess.run([binary(), str(path)], capture_output=True, text=True, timeout=60)
    if "An error occured" in result.stderr or not result.stdout.strip():
        return {"error": result.stderr.strip().splitlines()[-1] if result.stderr.strip() else "no output"}
    lines = {"bitrates": {}}
    for line in result.stdout.splitlines():
        bar = re.match(r"^\s*(\d+)\s+\|*\s+([\d.]+)%$", line)
        if bar:
            lines["bitrates"][int(bar[1])] = bar[2]
        elif " : " in line or line.rstrip().endswith(":"):
            key, _, value = line.partition(":")
            lines.setdefault(key.strip(), value.strip())
    return lines
