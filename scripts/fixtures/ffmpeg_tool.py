#!/usr/bin/env python3
"""FFmpeg's releases (LGPL), built from their source tarballs as black boxes for the fixtures that show what each compression level of its FLAC
encoder writes (docs/formats/flac.md, "Compression level").

The tarballs are pinned by SHA-256 in SOURCES and come from ffmpeg.org. build(version) compiles a minimal ffmpeg program in .cache/encoders (the
WAVE reader, the FLAC encoder and muxer, and the filters that convert nothing) and returns its path; the program is only run, and no source file of it is
read by the repository's scripts. 6.1.1 is the ffmpeg package of Ubuntu 24.04, run as `ffmpeg`.

    python3 ffmpeg_tool.py --versions   prints the version line of each release's program (needs a C toolchain)
"""

import pathlib
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile

import downloads

ROOT = pathlib.Path(__file__).resolve().parents[2]
CACHE = ROOT / ".cache" / "encoders"

SYSTEM_VERSION = "6.1.1"

RELEASES = "https://ffmpeg.org/releases/"

SOURCES = {
    "0.11.5": ("ffmpeg-0.11.5.tar.bz2", "9492991f44d6757080f457c0aef83e68884dbfb925666806c7f7ea0389e5b8c0"),
    "1.0.10": ("ffmpeg-1.0.10.tar.bz2", "1dbde434c3b5c573d3b2ffc1babe3814f781c10c4bc66193a4132a44c9715176"),
    "1.2.12": ("ffmpeg-1.2.12.tar.bz2", "913ac95c7fad92c2a4ebcfd11850904f531845c75d45c3e4e4a693990fe2497d"),
    "2.4.14": ("ffmpeg-2.4.14.tar.xz", "736567ee95115876d7af7f99f2fa565417c351b748bdc05973618980da1df037"),
    "2.8.22": ("ffmpeg-2.8.22.tar.xz", "1fbbf622806a112c5131d42b280a9e980f676ffe1c81a4e0f2ae4cb121241531"),
    "3.4.14": ("ffmpeg-3.4.14.tar.xz", "a922fcfeb16df77a42eb90b9e795a551d52383d20dddd2091cb28df651278b57"),
    "4.3.9": ("ffmpeg-4.3.9.tar.xz", "9e2a718f3956fa87a7dbc73e647d74171bf23b8964b6478d868b9aa623d03374"),
    "4.4.6": ("ffmpeg-4.4.6.tar.xz", "2290461f467c08ab801731ed412d8e724a5511d6c33173654bd9c1d2e25d0617"),
    "5.0.3": ("ffmpeg-5.0.3.tar.xz", "04c70c377de233a4b217c2fdf76b19aeb225a287daeb2348bccd978c47b1a1db"),
    "5.1.8": ("ffmpeg-5.1.8.tar.xz", "56d4daf10c17330a45c8fe11bc260997677ca2432d3d5951dbeb5515c26028cb"),
    "6.0.1": ("ffmpeg-6.0.1.tar.xz", "9b16b8731d78e596b4be0d720428ca42df642bb2d78342881ff7f5bc29fc9623"),
    "7.0.2": ("ffmpeg-7.0.2.tar.xz", "8646515b638a3ad303e23af6a3587734447cb8fc0a0c064ecdb8e95c4fd8b389"),
    "7.1.3": ("ffmpeg-7.1.3.tar.xz", "f0bf043299db9e3caacb435a712fc541fbb07df613c4b893e8b77e67baf3adbe"),
    "8.0.1": ("ffmpeg-8.0.1.tar.xz", "05ee0b03119b45c0bdb4df654b96802e909e0a752f72e4fe3794f487229e5a41"),
}

CONFIGURE = [
    "--disable-everything", "--disable-doc", "--disable-network", "--disable-ffplay", "--disable-ffprobe", "--disable-asm",
    "--disable-debug", "--enable-encoder=flac", "--enable-decoder=pcm_s16le,pcm_s24le,pcm_s32le", "--enable-demuxer=wav", "--enable-muxer=flac,ogg",
    "--enable-protocol=file,pipe", "--enable-filter=aresample,anull,aformat",
]


# Releases whose program does not compile with a current toolchain (3.0.12 was tried): none in SOURCES.
def addresses(archive):
    return [RELEASES + archive]


def sources():
    for archive, digest in SOURCES.values():
        yield archive, digest, addresses(archive)


def fetch(version):
    archive, digest = SOURCES[version]
    CACHE.mkdir(parents=True, exist_ok=True)
    path = CACHE / archive
    downloads.fetch(addresses(archive), digest, path)
    return path


def build(version):
    """Compiles release `version` in .cache/encoders and returns the path of its ffmpeg program."""
    if version == SYSTEM_VERSION:
        return pathlib.Path(shutil.which("ffmpeg") or "ffmpeg")
    program = CACHE / f"ffmpeg-{version}-bin" / "ffmpeg"
    if program.exists():
        return program
    with tempfile.TemporaryDirectory() as temp:
        with tarfile.open(fetch(version)) as files:
            files.extractall(temp, filter="data")
        source = next(p for p in pathlib.Path(temp).iterdir() if p.is_dir())
        # An option an older configure does not know is dropped (--disable-autodetect and --disable-asm are later than the oldest releases).
        options = ["--disable-autodetect", *CONFIGURE]
        while True:
            configured = subprocess.run(["./configure", *options], cwd=source, capture_output=True, text=True)
            unknown = re.search(r"Unknown option \"(--[^\"]+)\"", configured.stdout + configured.stderr)
            if configured.returncode == 0 or not unknown or unknown.group(1) not in options:
                break
            options.remove(unknown.group(1))
        if configured.returncode != 0:
            raise SystemExit(f"ffmpeg {version} did not configure: {(configured.stdout + configured.stderr)[-300:]}")
        subprocess.run(["make", "-j4", "-k"], cwd=source, check=False, capture_output=True)
        if not (source / "ffmpeg").exists():
            raise SystemExit(f"ffmpeg {version} did not build")
        program.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(source / "ffmpeg"), str(program))
    return program


def version_line(version):
    return subprocess.run([str(build(version)), "-version"], capture_output=True, text=True).stdout.splitlines()[0]


def main():
    if "--versions" not in sys.argv:
        raise SystemExit(__doc__)
    for version in [*SOURCES, SYSTEM_VERSION]:
        try:
            print(f"{version}: {version_line(version)}")
        except SystemExit as error:
            print(f"{version}: {error}")


if __name__ == "__main__":
    main()
