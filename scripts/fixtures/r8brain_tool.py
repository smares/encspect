"""r8brain-free-src, the sample rate converter the Nyquist edge fixtures are resampled with: github.com/avaneev/r8brain-free-src at commit
5792291ad66b04e87fd61a3d92606214632c6cfc, under the MIT license (Aleksey Vaneev; the notice is in the repository's LICENSE).

The commit is the pin, as for alac_tool.py: git checks the whole tree against it. check_sources.py asks the repository whether it still serves
the commit. The library is header-only; r8brain_resample.cpp is this project's own front end (raw float64 samples in, `CDSPResampler::oneshot`,
raw float64 samples out), compiled with g++ -O2 -ffp-contract=off (no fused multiply-add, no -march, so the doubles do not depend on the target) against the pinned tree. The converter is run as a black box on the fixtures' sources, never read.

Usage: python3 scripts/fixtures/r8brain_tool.py [directory]    builds r8brain_resample in the directory and prints its path.
"""

import pathlib
import subprocess
import sys
import tempfile

REPOSITORY = "https://github.com/avaneev/r8brain-free-src.git"
COMMIT = "5792291ad66b04e87fd61a3d92606214632c6cfc"
NAME = f"r8brain-free-src-{COMMIT[:12]}"
FRONT_END = pathlib.Path(__file__).resolve().parent / "r8brain_resample.cpp"


def commit_problems(only=None):
    """For check_sources.py: the problem, if any, with the repository serving the pinned commit; only, if given, is a set of addresses to check."""
    address = f"{REPOSITORY} {COMMIT}"
    if only is not None and address not in only:
        return []
    with tempfile.TemporaryDirectory(prefix="r8brain-check-") as directory:
        subprocess.run(["git", "init", "-q", directory], check=True)
        result = subprocess.run(["git", "-C", directory, "fetch", "-q", "--depth", "1", REPOSITORY, COMMIT], capture_output=True, text=True)
        if result.returncode != 0:
            return [f"{REPOSITORY} {COMMIT}: {result.stderr.strip().splitlines()[-1] if result.stderr.strip() else f'git exit {result.returncode}'}"]
        head = subprocess.run(["git", "-C", directory, "rev-parse", "FETCH_HEAD"], capture_output=True, text=True).stdout.strip()
        return [] if head == COMMIT else [f"{REPOSITORY} {COMMIT}: serves {head}"]


def build(directory=None):
    """The path of r8brain_resample, built in directory (a new temporary one by default) from the tree at the pinned commit."""
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="r8brain-"))
    source = directory / NAME
    if not (source / ".git").exists():
        source.mkdir(parents=True, exist_ok=True)
        subprocess.run(["git", "init", "-q", str(source)], check=True)
        subprocess.run(["git", "-C", str(source), "fetch", "-q", "--depth", "1", REPOSITORY, COMMIT], check=True)
        subprocess.run(["git", "-C", str(source), "checkout", "-q", "--detach", "FETCH_HEAD"], check=True)
    head = subprocess.run(["git", "-C", str(source), "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
    if head != COMMIT:
        raise SystemExit(f"{source} is at {head}, not the pinned commit {COMMIT}.")
    binary = directory / "r8brain_resample"
    if not binary.exists():
        subprocess.run(["g++", "-O2", "-ffp-contract=off", "-std=c++17", "-I", str(source), str(FRONT_END), "-o", str(binary)], check=True, stdout=sys.stderr)
    return binary


def main():
    print(build(sys.argv[1] if len(sys.argv) > 1 else None))


if __name__ == "__main__":
    main()
