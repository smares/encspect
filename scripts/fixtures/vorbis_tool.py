"""Builds vorbis_pcm.c against the system's libvorbis (libvorbis-dev, libogg-dev) and runs it (see that file)."""

import json
import pathlib
import subprocess
import tempfile

PCM_SOURCE = pathlib.Path(__file__).resolve().parent / "vorbis_pcm.c"


def build_pcm():
    """Compiles vorbis_pcm.c (the libvorbis decoder of the PCM cross-check) into a temporary directory."""
    directory = pathlib.Path(tempfile.mkdtemp(prefix="vorbis-"))
    binary = directory / "vorbis_pcm"
    subprocess.run(["cc", "-O1", "-ffp-contract=off", "-o", str(binary), str(PCM_SOURCE), "-lvorbis", "-logg", "-lm"], check=True)
    return binary


def decode_pcm(binary, path, output):
    """Decodes path with libvorbis to the file output (interleaved float32) and returns (the packets, one dict each, and the stream's channels and rate)."""
    result = subprocess.run([str(binary), str(path), str(output)], capture_output=True, text=True)
    if result.returncode != 0:
        return None
    lines = [json.loads(line) for line in result.stdout.splitlines()]
    return lines[:-1], lines[-1]
