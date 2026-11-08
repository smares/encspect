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
downloads it by hand and puts it where CI can fetch it; until its SHA-256 and addresses are set, the job is skipped.

Usage: python3 scripts/fixtures/lossywav_tool.py [directory]    builds lossywav in the directory and prints its path.
       python3 scripts/fixtures/lossywav_tool.py --windows-pinned   prints true when the Windows build is pinned, else false.
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

# The official Windows build: the archive as the lead downloaded it, its SHA-256 and the addresses CI fetches it from.
# The empty SHA-256 is the placeholder that keeps the Windows job skipped.
WINDOWS = {"archive": "lossyWAV_1.4.2.zip", "sha256": "", "addresses": []}


def windows_pinned():
    return len(WINDOWS["sha256"]) == 64 and bool(WINDOWS["addresses"])


def sources():
    """For check_sources.py: the Windows build, once it is pinned."""
    if windows_pinned():
        yield WINDOWS["archive"], WINDOWS["sha256"], WINDOWS["addresses"]


def fetch_windows(directory):
    """The path of lossyWAV.exe, from the pinned archive (a zip, or the exe itself) fetched into directory."""
    if not windows_pinned():
        raise SystemExit("The Windows build of lossyWAV is not pinned yet (lossywav_tool.WINDOWS).")
    directory = pathlib.Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    archive = directory / WINDOWS["archive"]
    downloads.fetch(WINDOWS["addresses"], WINDOWS["sha256"], archive)
    if archive.suffix.lower() == ".exe":
        return archive
    with zipfile.ZipFile(archive) as files:
        files.extractall(directory / "build")
    found = sorted(p for p in (directory / "build").rglob("*") if p.name.lower() == "lossywav.exe")
    if not found:
        raise SystemExit(f"{archive} holds no lossyWAV.exe.")
    return found[0]


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
    if args == ["--windows-pinned"]:
        print("true" if windows_pinned() else "false")
    elif args[:1] == ["--windows"] and len(args) == 2:
        print(fetch_windows(args[1]))
    else:
        print(build(args[0] if args else None))


if __name__ == "__main__":
    main()
