"""Builds the Helix MP3 Encoder (hmp3 5.2.4, RealNetworks' encoder with HydrogenAudio members'
maintenance) from github.com/maikmerten/hmp3 at a pinned commit, for encoder-written fixtures.

RealNetworks released the encoder under the RealNetworks Public Source License; it is built for
encoding this project's own test signals and not redistributed.
"""

import pathlib
import subprocess
import tempfile

REPOSITORY = "https://github.com/maikmerten/hmp3"
COMMIT = "7f7dfc7680db3c05f8e4a8fbd9861cbb06427d92"
VERSION = "hmp3 MPEG Layer III audio encoder 5.2.4"


def build():
    """Clones the pinned commit and builds the release binary in a temporary directory."""
    directory = pathlib.Path(tempfile.mkdtemp(prefix="hmp3-"))
    subprocess.run(["git", "clone", "-q", REPOSITORY, str(directory)], check=True)
    subprocess.run(["git", "-C", str(directory), "checkout", "-q", COMMIT], check=True)
    subprocess.run(["make", "-s", "-j4"], cwd=directory, check=True, capture_output=True)
    binary = directory / "builds" / "release" / "hmp3"
    result = subprocess.run([str(binary)], capture_output=True, text=True)
    banner = result.stdout + result.stderr
    if VERSION not in banner:
        raise SystemExit(f"{binary} is not {VERSION}")
    return binary


def encode(binary, arguments, source, path):
    result = subprocess.run([str(binary), str(source), str(path), "-D", *arguments], capture_output=True, text=True)
    if result.returncode != 0 or not path.exists():
        raise SystemExit(f"hmp3 {' '.join(arguments)} failed: {result.stdout[-500:]}")
