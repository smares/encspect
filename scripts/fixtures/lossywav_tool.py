"""lossyWAV for POSIX, the port of lossyWAV 1.4.2 the lossyWAV fixtures are made with: github.com/MoSal/lossywav-for-posix at
commit c3a22be0e1d8d516e3aa1761b6822ec68c06f920, under the GPL 3.

The commit is the pin, as for alac_tool.py: git checks the whole tree against it. check_sources.py asks the repository whether
it still serves the commit.

- lossyWAV is GPL and is a black-box tool only: the workflow lossywav-fixtures.yml builds it with the repository's own
  Makefile.unix on a CI runner and make_lossywav_fixtures.py runs it there. Nothing of its source is read or ported, and it is
  not built or run outside CI (docs/design/lossywav-detection.md, "The build").
- Needs git, make and g++.

The official Windows build of lossyWAV 1.4.2 (WINDOWS) is pinned by its SHA-256 and run by the workflow's Windows job on the
same inputs (decision 9 of the design). It is attached to the Hydrogenaudio forum, which scripts cannot reach, so the lead
downloaded it by hand into the private repository (downloads.private, with ENCSPECT_PRIVATE_TOKEN). The zip holds
bin/32-Bit/lossyWAV.exe, bin/64-Bit/lossyWAV.exe, fftw.txt and gpl.txt; the job runs the 64-bit exe.

Usage: python3 scripts/fixtures/lossywav_tool.py [directory]    builds lossywav in the directory and prints its path.
       python3 scripts/fixtures/lossywav_tool.py --windows DIR      fetches the Windows build into DIR and prints the exe's path.
"""

import pathlib
import subprocess
import sys
import tempfile
import zipfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import downloads  # noqa: E402

REPOSITORY = "https://github.com/MoSal/lossywav-for-posix.git"
COMMIT = "c3a22be0e1d8d516e3aa1761b6822ec68c06f920"
NAME = f"lossywav-for-posix-{COMMIT[:12]}"

# The official Windows build (955,107 bytes), in the private repository, and the exe of it the job runs.
WINDOWS = {
    "archive": "lossyWAV_v1.4.2.zip",
    "sha256": "d5c1a9e17c9569b8fd30c28254c4ff7a7a7213783c7a55f448003cd3e720ffb1",
    "addresses": [downloads.private("lossywav/lossyWAV_v1.4.2.zip")],
    "exe": "bin/64-Bit/lossyWAV.exe",
}


def sources():
    """For check_sources.py: the Windows build (checked when ENCSPECT_PRIVATE_TOKEN is set)."""
    yield WINDOWS["archive"], WINDOWS["sha256"], WINDOWS["addresses"]


def fetch_windows(directory):
    """The path of the 64-bit lossyWAV.exe, from the pinned zip fetched into directory and checked by its SHA-256."""
    directory = pathlib.Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    archive = directory / WINDOWS["archive"]
    downloads.fetch(WINDOWS["addresses"], WINDOWS["sha256"], archive)
    with zipfile.ZipFile(archive) as files:
        files.extract(WINDOWS["exe"], directory / "build")
    return directory / "build" / WINDOWS["exe"]


def commit_problems(only=None):
    """For check_sources.py: the problem, if any, with the repository serving the pinned commit; only, if given, is a set of addresses to check."""
    address = f"{REPOSITORY} {COMMIT}"
    if only is not None and address not in only:
        return []
    with tempfile.TemporaryDirectory(prefix="lossywav-check-") as directory:
        subprocess.run(["git", "init", "-q", directory], check=True)
        result = subprocess.run(["git", "-C", directory, "fetch", "-q", "--depth", "1", REPOSITORY, COMMIT], capture_output=True, text=True)
        if result.returncode != 0:
            return [f"{REPOSITORY} {COMMIT}: {result.stderr.strip().splitlines()[-1] if result.stderr.strip() else f'git exit {result.returncode}'}"]
        head = subprocess.run(["git", "-C", directory, "rev-parse", "FETCH_HEAD"], capture_output=True, text=True).stdout.strip()
        return [] if head == COMMIT else [f"{REPOSITORY} {COMMIT}: serves {head}"]


def build(directory=None):
    """The path of lossywav, built in directory (a new temporary one by default) from the tree at the pinned commit."""
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="lossywav-"))
    source = directory / NAME
    if not (source / ".git").exists():
        source.mkdir(parents=True, exist_ok=True)
        subprocess.run(["git", "init", "-q", str(source)], check=True)
        subprocess.run(["git", "-C", str(source), "fetch", "-q", "--depth", "1", REPOSITORY, COMMIT], check=True)
        subprocess.run(["git", "-C", str(source), "checkout", "-q", "--detach", "FETCH_HEAD"], check=True)
    head = subprocess.run(["git", "-C", str(source), "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
    if head != COMMIT:
        raise SystemExit(f"{source} is at {head}, not the pinned commit {COMMIT}.")
    binary = source / "lossywav"
    if not binary.exists():
        subprocess.run(["make", "-f", "Makefile.unix", "CXX=g++"], cwd=source, check=True, stdout=sys.stderr)
    return binary


def main():
    args = sys.argv[1:]
    if args[:1] == ["--windows"] and len(args) == 2:
        print(fetch_windows(args[1]))
    else:
        print(build(args[0] if args else None))


if __name__ == "__main__":
    main()
