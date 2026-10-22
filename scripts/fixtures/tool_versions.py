"""The versions of the tools the cross-checks run, asserted so that a cross-check fails loudly, naming the tool, when it
runs against others than the ones its skips and comparisons were written for: Ubuntu 26.04's packages (and GPAC 2.2.1,
built by gpac_tool.py), the runner image of the cross-check jobs.

    tool_versions.require("ffmpeg", "ffprobe", "MP4Box")

A program is checked by what it prints (PROGRAMS), a package whose programs or libraries print no version (musepack-tools,
liba52, libdca, madplay, speex, opus-tools) by the version dpkg holds for it (PACKAGES): madplay prints the version of its library, and the
speex and opus programs print versions that differ from their packages'. A version matches when it starts with the expected one and the next character does not continue the number, so
"8.0.1-3ubuntu2" is 8.0.1 and "8.0.10" is not. MediaInfo is checked by mediainfo_tool.require().
"""

import re
import subprocess
import sys

# name: (command, pattern whose first group is the version in the command's output, expected version). Output of both streams.
PROGRAMS = {
    "ffmpeg": (["ffmpeg", "-version"], r"^ffmpeg version (\S+)", "8.0.1"),
    "ffprobe": (["ffprobe", "-version"], r"^ffprobe version (\S+)", "8.0.1"),
    "mkvinfo": (["mkvinfo", "--version"], r"^mkvinfo v(\S+)", "97.0"),
    "wvunpack": (["wvunpack", "--version"], r"^wvunpack (\S+)", "5.9.0"),
    "MP4Box": (["MP4Box", "-version"], r"GPAC version (\S+)", "2.2.1"),
    "faad": (["faad", "-h"], r"MPEG-4 AAC Decoder V(\S+)", "2.11.2"),
    "mpg123": (["mpg123", "--version"], r"^mpg123 (\S+?);?\s", "1.33.3"),
    "php": (["php", "-r", "echo PHP_VERSION;"], r"^(\d+\.\d+)", "8.5"),
    "oggz": (["oggz", "--version"], r"^oggz version (\S+)", "1.1.3"),
    "ogginfo": (["ogginfo", "-V"], r"^ogginfo from vorbis-tools (\S+)", "1.4.3"),
    "flac": (["flac", "--version"], r"^flac (\S+)", "1.5.0"),
    "metaflac": (["metaflac", "--version"], r"^metaflac (\S+)", "1.5.0"),
}

# name: (dpkg package, expected upstream version).
PACKAGES = {
    "musepack": ("musepack-tools", "0.1~r495"),
    "a52": ("liba52-0.7.4-dev", "0.7.4"),
    "dca": ("libdca-dev", "0.0.7"),
    "madplay": ("madplay", "0.15.2b"),
    "speex": ("speex", "1.2.1"),
    "opus-tools": ("opus-tools", "0.2"),
}


def matches(found, expected):
    """Whether the version found (an epoch is ignored) is the expected one, as the module's description says."""
    found = re.sub(r"^\d+:", "", found.strip())
    return found.startswith(expected) and not re.match(r"\d|\.\d", found[len(expected):])


def program_version(name):
    """(found version or None, what the tool printed) of a program in PROGRAMS."""
    command, pattern, _ = PROGRAMS[name]
    try:
        result = subprocess.run(command, capture_output=True, text=True, errors="replace")
    except OSError as error:
        return None, str(error)
    output = result.stdout + result.stderr
    match = re.search(pattern, output, re.MULTILINE)
    return (match.group(1) if match else None), " ".join(output.split())[:120]


def package_version(name):
    package = PACKAGES[name][0]
    try:
        result = subprocess.run(["dpkg-query", "-W", "-f", "${Version}", package], capture_output=True, text=True)
    except OSError as error:
        return None, str(error)
    return (result.stdout if result.returncode == 0 and result.stdout else None), result.stderr.strip() or "not installed"


def problems(*names):
    """One message per tool among names whose version is not the expected one."""
    found = []
    for name in names:
        if name in PROGRAMS:
            version, printed = program_version(name)
            expected = PROGRAMS[name][2]
            label = name
        elif name in PACKAGES:
            version, printed = package_version(name)
            expected = PACKAGES[name][1]
            label = f"{PACKAGES[name][0]} (dpkg)"
        else:
            raise KeyError(f"no version is asserted for {name}")
        if version is None:
            found.append(f"{label} {expected} is needed, but no version was found: {printed}")
        elif not matches(version, expected):
            found.append(f"{label} {expected} is needed, found {version}")
    return found


def require(*names):
    """Exits, listing every tool among names whose version differs from the one the cross-check is written for."""
    found = problems(*names)
    if found:
        sys.exit("The cross-check needs Ubuntu 26.04's tools:\n  " + "\n  ".join(found))
