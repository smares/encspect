"""Builds the Helix MP3 Encoder for encoder-written fixtures: hmp3 5.2.4, RealNetworks' encoder with
HydrogenAudio members' maintenance, from github.com/maikmerten/hmp3 at a pinned commit, and the
5.0 release (r11, 2005) that RealNetworks published, from the source archive Rarewares keeps,
checked against a pinned SHA-256.

RealNetworks released the encoder under the RealNetworks Public Source License; it is built for
encoding this project's own test signals and not redistributed. 5.0 r11 shipped Visual C++ project
files only; its C versions of the assembly routines build with GCC once the Windows-only setmode
call in the command-line front end is defined away.
"""

import pathlib
import subprocess
import tarfile
import tempfile

import downloads

REPOSITORY = "https://github.com/maikmerten/hmp3"
COMMIT = "7f7dfc7680db3c05f8e4a8fbd9861cbb06427d92"
VERSION = "hmp3 MPEG Layer III audio encoder 5.2.4"

R11_URL = "https://www.rarewares.org/rrw/files/xing/helix_mp3enc_5.0r11.tar.gz"
R11_SHA256 = "bd575d86ada10b963c51fa3bf9ffe36ef1e61e667d685f715a5ec5b45e3c338b"
# No copy of the archive is known besides Rarewares'; the fixtures it writes are committed.
R11_ADDRESSES = [R11_URL]
R11_VERSION = "file-file MPEG Layer III audio encode v5.0 TEST [CML MOD r11]"


def build():
    """Clones the pinned commit and builds the release binary in a temporary directory."""
    directory = pathlib.Path(tempfile.mkdtemp(prefix="hmp3-"))
    subprocess.run(["git", "clone", "-q", REPOSITORY, str(directory)], check=True)
    subprocess.run(["git", "-C", str(directory), "checkout", "-q", COMMIT], check=True)
    subprocess.run(["make", "-s", "-j4"], cwd=directory, check=True, capture_output=True)
    binary = directory / "builds" / "release" / "hmp3"
    result = subprocess.run([str(binary)], capture_output=True, text=True)
    banner = result.stdout + result.stderr
    if VERSION not in banner:
        raise SystemExit(f"{binary} is not {VERSION}")
    return binary


def encode(binary, arguments, source, path):
    result = subprocess.run([str(binary), str(source), str(path), "-D", *arguments], capture_output=True, text=True)
    if result.returncode != 0 or not path.exists():
        raise SystemExit(f"hmp3 {' '.join(arguments)} failed: {result.stdout[-500:]}")


def build_r11():
    """Downloads and checks the 5.0 r11 source and builds its command-line encoder, tomp3."""
    directory = pathlib.Path(tempfile.mkdtemp(prefix="helix-r11-"))
    path = directory / "helix_mp3enc_5.0r11.tar.gz"
    downloads.fetch(R11_ADDRESSES, R11_SHA256, path)
    with tarfile.open(path) as archive:
        archive.extractall(directory, filter="data")
    source = directory / "helix_mp3enc_r11" / "src"
    makefile = (source / "Umakefil").read_text(encoding="latin-1")
    names = [line.strip().strip("',)") for line in makefile[makefile.index("AddSources(") + len("AddSources("):].split(")")[0].split(",")]
    objects = []
    for name in [*names, "test/tomp3.cpp"]:
        target = directory / (pathlib.Path(name).name + ".o")
        compiler = ["gcc", "-fcommon"] if name.endswith(".c") else ["g++", "-fpermissive", "-Dsetmode(f,m)=0", "-Itest"]
        subprocess.run([*compiler, "-O2", "-w", "-I.", "-Ipub", "-c", name, "-o", str(target)], cwd=source, check=True)
        objects.append(str(target))
    binary = directory / "tomp3"
    subprocess.run(["g++", "-o", str(binary), *objects], check=True)
    result = subprocess.run([str(binary)], capture_output=True, text=True, stdin=subprocess.DEVNULL)
    if R11_VERSION not in result.stdout + result.stderr:
        raise SystemExit(f"{binary} is not {R11_VERSION}")
    return binary


def encode_r11(binary, arguments, source, path):
    """tomp3 returns the number of frames plus one as its exit code (test/tomp3.cpp), so only the
    output tells whether it worked."""
    result = subprocess.run([str(binary), str(source), str(path), "-D", *arguments], capture_output=True, text=True,
                            stdin=subprocess.DEVNULL)
    if not path.exists() or path.stat().st_size == 0:
        raise SystemExit(f"tomp3 {' '.join(arguments)} failed: {(result.stdout + result.stderr)[-500:]}")
