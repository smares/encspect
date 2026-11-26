"""Ittiam's libxaac, which the AAC decoder follows where the specification leaves details to the decoder: github.com/ittiam-systems/libxaac at commit
2fbadd57bca46693b7a077bcf1513131106d960c (2026-10-01), under the Apache License 2.0.

The commit is the pin, as for alac_tool.py: git checks the whole tree against it, where an archive's bytes (GitHub builds it on request) are not promised to stay the
same. Nothing of it is committed or shipped; it is read where the decoder says it follows it, and its test decoder (`ia_xheaacdec`) may be built as a second reference.

- 628 of its 630 `.c` and `.h` files carry "Copyright (C) The Android Open Source Project" with the Apache 2.0 header, and none holds an ISO/IEC disclaimer
  (docs/design/lossy-decoders.md, decision 1); its `LICENSE` is the text THIRD-PARTY-NOTICES.md reproduces.
- The decoder follows `decoder/ixheaacd_channel.c` (the order of the tools, the pulse data), `ixheaacd_stereo.c`, `ixheaacd_pns_js_thumb.c` (noise substitution and
  temporal noise shaping), `ixheaacd_longblock.c` (the scalefactors and noise energies), `ixheaacd_lpfuncs.c` (the filter bank) and the tables of `ixheaacd_aac_rom.c`.
- Needs git. `build` builds the test decoder (`xaacdec`) with cmake and a C compiler, for crosscheck_aac_pcm.py --libxaac.

Usage: python3 scripts/fixtures/libxaac_tool.py [directory]    fetches the tree into the directory (a new temporary one by default) and prints its path.
"""

import pathlib
import subprocess
import sys
import tempfile

REPOSITORY = "https://github.com/ittiam-systems/libxaac.git"
COMMIT = "2fbadd57bca46693b7a077bcf1513131106d960c"
NAME = f"libxaac-{COMMIT[:12]}"


def commit_problems(only=None):
    """For check_sources.py: the problem, if any, with the repository serving the pinned commit; only, if given, is a set of addresses to check."""
    address = f"{REPOSITORY} {COMMIT}"
    if only is not None and address not in only:
        return []
    with tempfile.TemporaryDirectory(prefix="libxaac-check-") as directory:
        subprocess.run(["git", "init", "-q", directory], check=True)
        result = subprocess.run(["git", "-C", directory, "fetch", "-q", "--depth", "1", REPOSITORY, COMMIT], capture_output=True, text=True)
        if result.returncode != 0:
            return [f"{REPOSITORY} {COMMIT}: {result.stderr.strip().splitlines()[-1] if result.stderr.strip() else f'git exit {result.returncode}'}"]
        head = subprocess.run(["git", "-C", directory, "rev-parse", "FETCH_HEAD"], capture_output=True, text=True).stdout.strip()
        return [] if head == COMMIT else [f"{REPOSITORY} {COMMIT}: serves {head}"]


def fetch(directory):
    """The source tree at the pinned commit, in a folder of directory: fetched with the commit as the only history, and checked against the pin."""
    source = pathlib.Path(directory) / NAME
    if not (source / ".git").exists():
        source.mkdir(parents=True, exist_ok=True)
        subprocess.run(["git", "init", "-q", str(source)], check=True)
        subprocess.run(["git", "-C", str(source), "fetch", "-q", "--depth", "1", REPOSITORY, COMMIT], check=True)
        subprocess.run(["git", "-C", str(source), "checkout", "-q", "--detach", "FETCH_HEAD"], check=True)
    head = subprocess.run(["git", "-C", str(source), "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
    if head != COMMIT:
        raise SystemExit(f"{source} is at {head}, not the pinned commit {COMMIT}.")
    return source


def license_text(directory=None):
    """The LICENSE of the repository (the Apache License 2.0), the text THIRD-PARTY-NOTICES.md reproduces."""
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="libxaac-"))
    return (fetch(directory) / "LICENSE").read_text(encoding="utf-8")


def build(directory=None):
    """The path of xaacdec, the test decoder of libxaac, built in directory (a new temporary one by default) from the source at the pinned commit with CMake, the
    default options and no fused multiply-add (-ffp-contract=off)."""
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="libxaac-")).resolve()
    directory.mkdir(parents=True, exist_ok=True)
    binary = directory / "libxaac-build" / "xaacdec"
    if binary.exists():
        return binary
    source = fetch(directory)
    build_directory = directory / "libxaac-build"
    build_directory.mkdir(exist_ok=True)
    subprocess.run(["cmake", str(source), "-DCMAKE_BUILD_TYPE=Release", "-DCMAKE_C_FLAGS=-O2 -ffp-contract=off"], cwd=build_directory, check=True, capture_output=True)
    subprocess.run(["cmake", "--build", ".", "--target", "xaacdec", "-j4"], cwd=build_directory, check=True, capture_output=True)
    return binary


def notice_text(directory=None):
    """The NOTICE of the repository (the copyright line and the attribution to Ittiam that the Apache License asks derivative works to carry)."""
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="libxaac-"))
    return (fetch(directory) / "NOTICE").read_text(encoding="utf-8")


if __name__ == "__main__":
    print(fetch(sys.argv[1] if len(sys.argv) > 1 else tempfile.mkdtemp(prefix="libxaac-")))
