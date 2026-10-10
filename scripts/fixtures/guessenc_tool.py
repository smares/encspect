"""Builds mp3guessenc from third_party/mp3guessenc (0.27.6 beta, the version Ubuntu 24.04 ships) and
returns the binary; the cross-checks compare its output, so it is pinned with the repository
rather than taken from whatever version a system has installed."""

import functools
import pathlib
import subprocess
import tempfile

SOURCE = pathlib.Path(__file__).resolve().parents[2] / "third_party" / "mp3guessenc"
FILES = ["mp3guessenc.c", "bit_utils.c", "tags.c", "decode.c"]


@functools.cache
def binary():
    """Compiles mp3guessenc with the flags of its Makefile's release build, once per process."""
    directory = pathlib.Path(tempfile.mkdtemp(prefix="mp3guessenc-"))
    path = directory / "mp3guessenc"
    subprocess.run(["cc", "-O2", "-fsingle-precision-constant", "-w", "-o", str(path), *(str(SOURCE / f) for f in FILES)], check=True)
    return str(path)
