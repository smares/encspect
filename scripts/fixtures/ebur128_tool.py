"""Builds libebur128 as a black-box tool for the loudness cross-checks (docs/design/spectral-analysis.md, "Cross-checks").

libebur128 (Jan Kokemuller, MIT) is cloned at its pinned tag, the commit is checked, and its single source file is compiled with ebur128_driver.c, a
small program of Encspect's own that calls the library's public API and prints the figures. The source is neither committed nor followed by Encspect's
code; the program is a tool the cross-check runs, as mp3guessenc is. Needs git and a C compiler.

    python3 scripts/fixtures/ebur128_tool.py [directory]     # builds into the directory (default .cache/ebur128) and prints the path of the driver
"""

import pathlib
import subprocess
import sys

REPOSITORY = "https://github.com/jiixyj/libebur128"
TAG = "v1.2.6"
COMMIT = "67b33abe1558160ed76ada1322329b0e9e058b02"
ROOT = pathlib.Path(__file__).resolve().parents[2]


def build(directory):
    """The path of the built ebur128_driver; clones and compiles only when it is not there."""
    directory = pathlib.Path(directory)
    driver = directory / "ebur128_driver"
    if driver.exists():
        return driver
    source = directory / "libebur128"
    directory.mkdir(parents=True, exist_ok=True)
    if not source.exists():
        subprocess.run(["git", "-c", "advice.detachedHead=false", "clone", "--quiet", "--depth", "1", "--branch", TAG, REPOSITORY, str(source)], check=True)
    commit = subprocess.run(["git", "-C", str(source), "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
    if commit != COMMIT:
        sys.exit(f"libebur128 {TAG} is commit {commit}, the cross-check is pinned to {COMMIT}.")
    subprocess.run(["gcc", "-O2", "-I", str(source / "ebur128"), "-I", str(source / "ebur128" / "queue"), str(source / "ebur128" / "ebur128.c"),
                    str(ROOT / "scripts" / "fixtures" / "ebur128_driver.c"), "-lm", "-o", str(driver)], check=True)
    return driver


if __name__ == "__main__":
    print(build(sys.argv[1] if len(sys.argv) > 1 else ROOT / ".cache" / "ebur128"))
