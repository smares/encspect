#!/usr/bin/env python3
"""The original WavPack releases 1.0 to 3.97 and the DOS build of 4.31, run as black boxes for the files they wrote
(docs/design/wavpack-legacy.md, "The original sources and documentation" and "Running the old encoders").

ReallyRareWares (https://www.rarewares.org/rrw/wavpack.php) keeps every release, "kindly contributed by David Bryant himself". Each archive is pinned by
SHA-256 and has the Wayback Machine's copy and the project's private repository as further addresses (downloads.py). The programs of 1.0 to 3.4 are real-mode
DOS programs, 4.31 is a DOS/32A program: DOSBox runs them headless (SDL_VIDEODRIVER=dummy) with a folder of the cache mounted as drive C, the commands in its
autoexec, and the output of each command redirected into a file (the programs print to the console only). Names inside DOSBox are 8.3, upper case; run() takes
and returns the names the caller likes and maps them.

The release 3.97 is a Win32 console program: on Windows the tool runs it directly, each command through cmd.exe in a folder of its own, and elsewhere through
Wine's cmd (the 32-bit Wine of the Ubuntu 24.04 container this was first written in crashes in its loader before it reads a file; its files were written on
Windows 10). Its sources (wpsources.zip, BSD) and the 4.31 sources (WVSRC431.ZIP, BSD) are pinned too, for the code they show and for check_sources.py;
nothing is built from them.

    python3 wavpack_legacy_tool.py --list      prints each archive and its SHA-256
    python3 wavpack_legacy_tool.py RELEASE     prints the release's program banner (the encoder run without arguments)
"""

import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile

import downloads

ROOT = pathlib.Path(__file__).resolve().parents[2]
CACHE = ROOT / ".cache" / "wavpack-legacy"
SITE = "https://www.rarewares.org/rrw/files/lossless/"
PAGE = "https://www.rarewares.org/rrw/wavpack.php"

# Release: (archive, SHA-256, year of the Wayback capture, the programs the release holds, as the archive names them).
RELEASES = {
    "1.0": ("WAVPACK1.ZIP", "d639e5b41356a708bf021daed3857a63cb1569de3177f4f2adedd82b87f7bb64", "2024", ("WAVPACK.EXE", "WVUNPACK.EXE")),
    "2.0": ("WAVPACK2.ZIP", "bcf12e827c8efe7113bea7755993e1975b4fa8103cd23b18fb67694a37d85bcb", "2024", ("WAVPACK.EXE", "WVUNPACK.EXE")),
    "3.0": ("WAVPACK3.ZIP", "759d289227b8d6eaec959152bf73aa091795b92c57f6591c2e4f1e549ccbc680", "2024", ("WAVPACK.EXE", "WVUNPACK.EXE")),
    "3.2": ("wavpck32.zip", "b026627c15ee8706d143dee951143a09a95007697cbe931b29f20d6baf0d0f8f", "2024", ("WAVPACK.EXE", "WVUNPACK.EXE")),
    "3.4": ("wavpck34.zip", "6f957f7552820f336a93f6876103a510526af1a891e93e5b046ccfeb8f32586e", "2024", ("wavpack.exe", "wvunpack.exe")),
    "3.97": ("wavpack3.97.zip", "792216c2e16b5bb5f6694ca1341065423e341de210687fe5aa46685439cba870", "2024", ("wavpack.exe", "wvunpack.exe")),
    "4.31": ("WVDOS431.ZIP", "95a27acac8c6a791772b8ce98443273f5feb9eb69215ceafcb977e595897d4a7", "2024", ("WAVPACK.EXE", "WVUNPACK.EXE")),
}
# The sources: 3.97's (wavpack.c, pack.c, unpack.c, words1.c to words4.c) and the 4.31 DOS build's (the library and the programs for OpenWatcom, UNPACK3.C).
SOURCES = {
    "3.97 sources": ("wpsources.zip", "b978e090a45106cd7a4d997861ec3a403f3ad0192f1a1f56897d7eec9de3a329", "2024"),
    "4.31 sources": ("WVSRC431.ZIP", "e7d21d0dc0a1af181be6cdb7af98fa222b7016afeded11a3bd70f005d1b61f64", "2024"),
}
MIRROR = "mirrors/tools/wavpack-legacy/"


def addresses(archive, year):
    url = SITE + archive
    return [url, downloads.archived(url, year), downloads.private(MIRROR + archive)]


def sources():
    """(name, SHA-256, addresses) of every file this script downloads."""
    for archive, digest, year, _ in RELEASES.values():
        yield archive, digest, addresses(archive, year)
    for archive, digest, year in SOURCES.values():
        yield archive, digest, addresses(archive, year)


def unpacked(release):
    """The folder with the programs of the release, fetched and unpacked in the cache."""
    archive, digest, year, programs = RELEASES[release]
    folder = CACHE / "unpacked" / release
    if all((folder / p).exists() for p in programs):
        return folder
    CACHE.mkdir(parents=True, exist_ok=True)
    path = CACHE / "rrw" / archive
    path.parent.mkdir(parents=True, exist_ok=True)
    downloads.fetch(addresses(archive, year), digest, path)
    folder.mkdir(parents=True, exist_ok=True)
    # The archives of 1.0 to 3.0 use the shrink and implode methods, which Python's zipfile cannot read.
    if shutil.which("unzip") is None:
        sys.exit("unzip is needed to unpack the WavPack archives.")
    with tempfile.TemporaryDirectory(prefix="wavpack-zip-") as temporary:
        subprocess.run(["unzip", "-q", "-o", "-j", str(path), "-d", temporary], check=True)
        for found in pathlib.Path(temporary).iterdir():
            for program in programs:
                if found.name.lower() == program.lower():
                    shutil.copy(found, folder / program)
    return folder


def dos_name(name):
    base, dot, extension = name.upper().rpartition(".")
    if not dot:
        base, extension = name.upper(), ""
    if not re.fullmatch(r"[A-Z0-9_\-]{1,8}", base) or len(extension) > 3:
        raise ValueError(f"{name} is not an 8.3 name")
    return base + ("." + extension if extension else "")


def check_dosbox():
    if shutil.which("dosbox") is None:
        sys.exit("DOSBox is needed to run the DOS builds of WavPack (apt install dosbox).")


WIN32 = ("3.97",)


def check_runner(release):
    if release in WIN32:
        if sys.platform != "win32" and shutil.which("wine") is None:
            sys.exit("Windows or Wine is needed to run WavPack 3.97.")
    else:
        check_dosbox()


def run_win32(release, files, commands, timeout=600):
    """run() for the Win32 build: each command runs through cmd.exe (Wine's on other systems) in a folder that holds the programs and the files, with its
    standard output and standard error captured; names keep the spelling the caller gives, and the folder's files come back under upper-case names."""
    check_runner(release)
    programs = RELEASES[release][3]
    source = unpacked(release)
    with tempfile.TemporaryDirectory(prefix="wavpack-win-") as temporary:
        folder = pathlib.Path(temporary) / "c"
        folder.mkdir()
        for program in programs:
            shutil.copy(source / program, folder / program)
        for name, data in files.items():
            (folder / name).write_bytes(data)
        # The folder goes first on the path: cmd.exe does not look in the current folder where NoDefaultCurrentDirectoryInExePath is set.
        environment = dict(os.environ, PATH=str(folder) + os.pathsep + os.environ.get("PATH", ""))
        prefix = ["cmd", "/d", "/c"]
        if sys.platform != "win32":
            environment.update(WINEPREFIX=str(CACHE / "wine"), WINEDEBUG="-all")
            prefix = ["wine"] + prefix
        texts = []
        for command in commands:
            # The programs ask on the console where an answer is needed; -y answers, and an empty standard input refuses anything else.
            result = subprocess.run(prefix + [command], cwd=folder, env=environment, stdin=subprocess.DEVNULL, capture_output=True, timeout=timeout, check=False)
            texts.append((result.stdout + result.stderr).decode("latin-1").replace("\r", "\n"))
        found = {p.name.upper(): p.read_bytes() for p in folder.iterdir() if p.is_file() and p.name not in programs}
    return texts, found


def run(release, files, commands, redirect=True, timeout=600):
    """Runs commands (lines for the DOS prompt, WAVPACK and WVUNPACK being the release's programs) in a folder that holds files (name: bytes, 8.3 names)
    under DOSBox, and returns (what the programs printed, one text for each command, the files the folder holds afterwards as {upper-case name: bytes}).
    The programs themselves are not returned. The encoder of 3.0 stops when its output is redirected (it has written the header, and nothing more, when the run times out),
    so redirect=False runs the commands without capturing their text, which then comes back empty. 3.97 runs as run_win32() says, redirect aside."""
    if release in WIN32:
        return run_win32(release, files, commands, timeout)
    check_dosbox()
    programs = RELEASES[release][3]
    source = unpacked(release)
    with tempfile.TemporaryDirectory(prefix="wavpack-dos-") as temporary:
        folder = pathlib.Path(temporary) / "c"
        folder.mkdir()
        for program in programs:
            shutil.copy(source / program, folder / program.upper())
        for name, data in files.items():
            (folder / dos_name(name)).write_bytes(data)
        lines = ["mount c .", "c:"]
        lines += [f"{command} > O{i:02d}.TXT" if redirect else command for i, command in enumerate(commands)]
        lines.append("exit")
        configuration = pathlib.Path(temporary) / "dosbox.conf"
        configuration.write_text("[sdl]\nfullscreen=false\n[cpu]\ncycles=max\n[dosbox]\nmachine=svga_s3\n[midi]\nmpu401=none\n[autoexec]\n" + "\n".join(lines) + "\n", encoding="ascii")
        environment = dict(os.environ, SDL_VIDEODRIVER="dummy", SDL_AUDIODRIVER="dummy", HOME=temporary)
        # Mounted in the folder itself, as DOSBox needs the path of a folder that holds the programs and the files.
        subprocess.run(["dosbox", "-conf", str(configuration), "-noconsole"], cwd=folder, env=environment, capture_output=True, timeout=timeout, check=False)
        found = {p.name.upper(): p.read_bytes() for p in folder.iterdir() if p.is_file()}
    texts = []
    for i in range(len(commands)):
        texts.append(found.pop(f"O{i:02d}.TXT", b"").decode("latin-1").replace("\r", "\n"))
    for program in programs:
        found.pop(program.upper(), None)
    return texts, found


def banner(release):
    texts, _ = run(release, {}, ["WAVPACK"])
    return " ".join(texts[0].split())


if __name__ == "__main__":
    if "--list" in sys.argv:
        for name, digest, _ in sources():
            print(name, digest)
    elif len(sys.argv) == 2 and sys.argv[1] in RELEASES:
        print(banner(sys.argv[1]))
    else:
        sys.exit(__doc__)
