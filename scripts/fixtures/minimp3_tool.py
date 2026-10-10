"""Builds minimp3_side_info.c against a pinned minimp3 and runs it (see that file)."""

import hashlib
import json
import pathlib
import subprocess
import tempfile

MINIMP3_COMMIT = "ea99364f61c14656440e8d77e9c233ccf3124633"
MINIMP3_SHA256 = "57e437c5c1f0e8b243885d3929c8973b5e6c778451e0100ab4251d19915cb3ad"
SOURCE = pathlib.Path(__file__).resolve().parent / "minimp3_side_info.c"


def build():
    """Downloads minimp3.h at the pinned commit, checks it, and compiles the dumper into a temporary directory."""
    directory = pathlib.Path(tempfile.mkdtemp(prefix="minimp3-"))
    header = directory / "minimp3.h"
    url = f"https://raw.githubusercontent.com/lieff/minimp3/{MINIMP3_COMMIT}/minimp3.h"
    subprocess.run(["curl", "-fsSL", "--retry", "3", "-o", str(header), url], check=True)
    digest = hashlib.sha256(header.read_bytes()).hexdigest()
    if digest != MINIMP3_SHA256:
        raise SystemExit(f"minimp3.h has SHA-256 {digest}, expected {MINIMP3_SHA256}")
    binary = directory / "minimp3_side_info"
    subprocess.run(["cc", "-O1", "-I", str(directory), "-o", str(binary), str(SOURCE), "-lm"], check=True)
    return binary


def side_info(binary, path):
    result = subprocess.run([str(binary), str(path)], capture_output=True, text=True, check=True)
    return [json.loads(line) for line in result.stdout.splitlines()]
