"""Apple's ALAC reference implementation, which the ALAC parser follows and the fixtures are made and checked with: github.com/macosforge/alac at
commit c38887c5c5e64a4b31108733bd79ca9b2496d987, under the Apache License 2.0.

The commit is the pin, as for faac_tool.py: git checks the whole tree against it, where an archive's bytes (GitHub builds it on request) are not
promised to stay the same. The repository has no tags. check_sources.py asks the repository whether it still serves the commit.

- The parser's walk follows ALACDecoder.cpp, ag_dec.c and ALACMagicCookieDescription.txt, and carries Apple's notice (THIRD-PARTY-NOTICES.md).
- `alacconvert`, the utility of the source (convert-utility), is built unpatched from the files the repository's makefiles name, with the system's
  C and C++ compilers, and run from the command line only. It encodes a WAVE file or a CAF file of PCM into a CAF file of ALAC (16, 24 and 32 bits,
  1 to 8 channels, the default frame length of 4,096 samples) and decodes a CAF file of ALAC into a WAVE file (two channels or fewer) or a CAF file of PCM.
  Its encoder is Apple's reference encoder, not the encoder of afconvert or iTunes, and writes the cookie and the 'chan' chunk the cookie description shows.
- Needs git, gcc and g++.

Usage: python3 scripts/fixtures/alac_tool.py [directory]    builds alacconvert in the directory and prints its path.
"""

import pathlib
import subprocess
import sys
import tempfile

REPOSITORY = "https://github.com/macosforge/alac.git"
COMMIT = "c38887c5c5e64a4b31108733bd79ca9b2496d987"
NAME = f"alac-{COMMIT[:12]}"

# The files of the codec library and of the utility, as codec/makefile and convert-utility/makefile name them.
CODEC_C = ["ALACBitUtilities.c", "ag_dec.c", "ag_enc.c", "dp_dec.c", "dp_enc.c", "matrix_dec.c", "matrix_enc.c", "EndianPortable.c"]
CODEC_CPP = ["ALACDecoder.cpp", "ALACEncoder.cpp"]
UTILITY_CPP = ["main.cpp", "CAFFileALAC.cpp"]


def commit_problems(only=None):
    """For check_sources.py: the problem, if any, with the repository serving the pinned commit; only, if given, is a set of addresses to check."""
    address = f"{REPOSITORY} {COMMIT}"
    if only is not None and address not in only:
        return []
    with tempfile.TemporaryDirectory(prefix="alac-check-") as directory:
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
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="alac-"))
    return (fetch(directory) / "LICENSE").read_text(encoding="utf-8")


def build(directory=None):
    """The path of alacconvert, built in directory (a new temporary one by default) from the source at the pinned commit."""
    directory = pathlib.Path(directory or tempfile.mkdtemp(prefix="alac-"))
    directory.mkdir(parents=True, exist_ok=True)
    binary = directory / "alacconvert"
    if binary.exists():
        return binary
    source = fetch(directory)
    codec = source / "codec"
    objects = []
    for name in CODEC_C + CODEC_CPP + UTILITY_CPP:
        path = (source / "convert-utility" if name in UTILITY_CPP else codec) / name
        compiler = "gcc" if name.endswith(".c") else "g++"
        output = directory / (name.rsplit(".", 1)[0] + ".o")
        subprocess.run([compiler, "-O2", "-c", f"-I{codec}", str(path), "-o", str(output)], check=True, capture_output=True)
        objects.append(str(output))
    subprocess.run(["g++", *objects, "-o", str(binary)], check=True, capture_output=True)
    return binary


def encode(binary, source, target):
    """Encodes the WAVE or CAF file source (PCM) into the CAF file target with Apple's reference encoder."""
    pathlib.Path(target).unlink(missing_ok=True)
    result = subprocess.run([str(binary), str(source), str(target)], capture_output=True, text=True)
    if result.returncode != 0 or not pathlib.Path(target).exists():
        raise SystemExit(f"alacconvert failed to encode {source}: {result.stdout} {result.stderr}")
    return target


def decode(binary, source, target):
    """Decodes the CAF file source (ALAC) into target, a WAVE file for two channels or fewer, else a CAF file."""
    pathlib.Path(target).unlink(missing_ok=True)
    result = subprocess.run([str(binary), str(source), str(target)], capture_output=True, text=True)
    if result.returncode != 0 or not pathlib.Path(target).exists():
        raise SystemExit(f"alacconvert failed to decode {source}: {result.stdout} {result.stderr}")
    return target


def main():
    print(build(sys.argv[1] if len(sys.argv) > 1 else None))


if __name__ == "__main__":
    main()
