"""Builds exhale, Christian Helmrich's xHE-AAC (USAC) encoder, for encoder-written fixtures: version 1.2.2
from gitlab.com/ecodis/exhale at its tag's commit, with its own makefile.

exhale is under the exhale Copyright License; it is built for encoding this project's own test signals and
not redistributed.
"""

import pathlib
import subprocess
import tempfile

REPOSITORY = "https://gitlab.com/ecodis/exhale.git"
COMMIT = "8cddccddec9d44b156fc7390a926e87691ce2668"  # v1.2.2
VERSION = "version 1.2.2"


def build():
    directory = pathlib.Path(tempfile.mkdtemp(prefix="exhale-"))
    subprocess.run(["git", "clone", "-q", REPOSITORY, str(directory)], check=True)
    subprocess.run(["git", "-C", str(directory), "checkout", "-q", COMMIT], check=True)
    subprocess.run(["make", "release", "-j4"], cwd=directory, check=True, capture_output=True)
    binary = directory / "bin" / "exhale"
    result = subprocess.run([str(binary)], capture_output=True, text=True)
    if VERSION not in result.stdout + result.stderr:
        raise SystemExit(f"{binary} is not exhale {VERSION}")
    return binary
