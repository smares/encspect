"""Builds minimp3_side_info.c against the minimp3 in third_party/minimp3 (a pinned commit, see
third_party/README.md) and runs it (see that file)."""

import json
import pathlib
import subprocess
import tempfile

SOURCE = pathlib.Path(__file__).resolve().parent / "minimp3_side_info.c"
HEADERS = pathlib.Path(__file__).resolve().parents[2] / "third_party" / "minimp3"


def build():
    """Compiles the dumper into a temporary directory."""
    directory = pathlib.Path(tempfile.mkdtemp(prefix="minimp3-"))
    binary = directory / "minimp3_side_info"
    subprocess.run(["cc", "-O1", "-I", str(HEADERS), "-o", str(binary), str(SOURCE), "-lm"], check=True)
    return binary


def side_info(binary, path):
    result = subprocess.run([str(binary), str(path)], capture_output=True, text=True, check=True)
    return [json.loads(line) for line in result.stdout.splitlines()]
