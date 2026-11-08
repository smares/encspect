#!/usr/bin/env python3
"""Writes the files the lossyWAV design (docs/design/lossywav-detection.md) is checked against: lossyWAV's output at its
quality presets and settings, and that output coded again with FLAC (lossyFLAC), WavPack and Monkey's Audio.

lossyWAV is GPL. It is used here only as a black-box tool: the workflow lossywav-fixtures.yml builds the POSIX port
(lossywav_tool.py: github.com/MoSal/lossywav-for-posix, a pinned commit of lossyWAV 1.4.2) with its own Makefile.unix on
a CI runner and passes the binary to this script; nothing of its source is read or ported, and it is never run outside
CI. The files are uploaded as an artifact and analyzed as data.

Inputs, all 44.1 kHz unless named otherwise:
- the deterministic signals of flac_signal.py (tones under a tremolo, then noise; loud noise; silence around a tone),
  in 8, 16, 24 and 32 bits, mono, stereo and six channels, at 8, 22.05, 32, 44.1, 48, 88.2, 96, 176.4 and 192 kHz,
  with a length that is not a multiple of any block size;
- the same music signal at 24 bits with its low 8 bits zero (16-bit audio in a 24-bit file);
- 30 s of two recordings from the Internet Archive, pinned by SHA-256: Kimiko Ishizaka's Goldberg Variations,
  Variation 4 (Musopen, public domain mark, 16 bits) and Anonymous420's "D'un vieux jardin" (CC0, 24 bits).

The fixtures the design commits (FIXTURES) are made at their committed length: lossyWAV runs on inputs of that length,
so each committed file is the bytes lossyWAV and the coder wrote, and the manifest holds its SHA-256, its figures
(lossywav_figures.py) and lossyWAV's own --check answer.

Every run of lossyWAV is a case: a name, the arguments and the inputs. A case that lossyWAV refuses is recorded with its
exit code and output, not dropped; the help texts are recorded first, so the next run can name the settings exactly.

Usage: make_lossywav_fixtures.py OUT --lossywav PATH [--mac PATH]
Writes OUT/fixtures/<path in the repository> (the files to commit), OUT/source (inputs), OUT/lossy/<case>/ (what lossyWAV wrote, its WAV output only for the cases of KEEP_WAV:
the flac-b512-foreign coding holds the samples and chunks of the others), OUT/coded/<case>/ (the codings), OUT/tools
(versions and help texts) and OUT/manifest.json (every file with its SHA-256, input, arguments and exit codes).
"""

import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import downloads  # noqa: E402
import flac_signal  # noqa: E402
import lossywav_figures  # noqa: E402
import lossywav_tool  # noqa: E402

# (name, addresses, SHA-256, start in seconds) of the recordings; 30 s are taken from the start.
MUSIC = [
    ("goldberg-var4-16bit",
     ["https://archive.org/download/musopen-dvd-in-lossless-flac-format/Goldberg%20Variations/"
      "Goldberg%20Variations%2C%20BWV.%20988%20-%20Variation%204.flac"],
     "b80dce4220c45419a196389271f529421f6724da65a73d2f47e254973260a011", 10),
    ("vieux-jardin-24bit",
     ["https://archive.org/download/anonymous-420-remix-what-intimidate-you/"
      "Anonymous420%20-%20Remix%20what%20intimidate%20you%20-%2002%20D%27un%20vieux%20jardin.flac"],
     "137c9129542e6ab8308bf904d8c13abde8cbf06cf6d74014d44169ead5512c6e", 60),
]
MUSIC_SECONDS = 30


def sources():
    """For check_sources.py: (name, SHA-256, addresses) of every file this script downloads."""
    for name, addresses, digest, _ in MUSIC:
        yield f"{name}.flac", digest, addresses

# (name, kind, rate, channels, bits, shift): 10 s plus 123 frames, so no block grid divides the length.
SIGNALS = [
    ("music-44k-16-st", "music", 44100, 2, 16, 0),
    ("loud-44k-16-st", "loud", 44100, 2, 16, 0),
    ("silent-44k-16-st", "silent", 44100, 2, 16, 0),
    ("music-44k-16-mono", "music", 44100, 1, 16, 0),
    ("music-44k-16-6ch", "music", 44100, 6, 16, 0),
    ("music-44k-24-st", "music", 44100, 2, 24, 0),
    ("music-44k-24in16-st", "music", 44100, 2, 24, 8),
    ("music-48k-24-st", "music", 48000, 2, 24, 0),
    ("music-96k-24-st", "music", 96000, 2, 24, 0),
    ("music-192k-24-st", "music", 192000, 2, 24, 0),
    ("music-44k-8-st", "music", 44100, 2, 8, 0),
    ("music-44k-32-st", "music", 44100, 2, 32, 0),
    ("music-48k-24-6ch", "music", 48000, 6, 24, 0),
    ("music-8k-24-st", "music", 8000, 2, 24, 0),
    ("music-22k-24-st", "music", 22050, 2, 24, 0),
    ("music-32k-24-st", "music", 32000, 2, 24, 0),
    ("music-88k-24-st", "music", 88200, 2, 24, 0),
    ("music-176k-24-st", "music", 176400, 2, 24, 0),
]

PRESETS = [("insane", "I"), ("extreme", "E"), ("high", "H"), ("standard", "S"), ("economic", "C"),
           ("portable", "P"), ("extraportable", "X")]

# Settings at the standard preset, on the music signal and the recordings only. The names follow lossyWAV 1.4.2's help
# text as the wiki quotes it; tools/help-*.txt of each run records what this build accepts.
SETTINGS = [
    ("q0", ["-q", "0"]),
    ("q10", ["-q", "10"]),
    ("standard-correction", ["-q", "S", "--correction"]),
    ("standard-linkchannels", ["-q", "S", "--linkchannels"]),
    ("standard-midside", ["-q", "S", "--midside"]),
    ("standard-limit20000", ["-q", "S", "--limit", "20000"]),
    ("standard-scale05", ["-q", "S", "--scale", "0.5"]),
    ("standard-feedback", ["-q", "S", "--feedback"]),
    ("standard-shaping-fixed", ["-q", "S", "--shaping", "fixed"]),
    ("standard-shaping-off", ["-q", "S", "--shaping", "off"]),
]
SETTINGS_INPUTS = ["music-44k-16-st", "music-44k-24-st", "goldberg-var4-16bit", "vieux-jardin-24bit"]

# The cases whose WAV output is uploaded as written and coded at every block size.
KEEP_WAV = {"preset-standard", "standard-correction"}

HELP_ARGUMENTS = [[], ["--help"], ["-h"], ["--longhelp"], ["-L"], ["--version"], ["-v"]]


def sha256(path):
    return hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()


def run(arguments, cwd=None, timeout=900):
    try:
        result = subprocess.run([str(a) for a in arguments], cwd=cwd, capture_output=True, timeout=timeout)
        return result.returncode, result.stdout.decode("utf-8", "replace"), result.stderr.decode("utf-8", "replace")
    except (OSError, subprocess.TimeoutExpired) as error:
        return -1, "", str(error)


def make_sources(out, work):
    source = out / "source"
    source.mkdir(parents=True, exist_ok=True)
    inputs = {}
    for name, kind, rate, channels, bits, shift in SIGNALS:
        path = source / f"{name}.wav"
        path.write_bytes(flac_signal.wav(kind, rate, channels, rate * 10 + 123, bits, shift=shift))
        inputs[name] = {"path": path, "origin": f"flac_signal.py {kind}, {rate} Hz, {channels} channels, {bits} bits"
                        + (f", low {shift} bits zero" if shift else "")}
    for name, addresses, digest, start in MUSIC:
        flac = work / f"{name}.flac"
        downloads.fetch(addresses, digest, flac)
        path = source / f"{name}.wav"
        code, _, error = run(["flac", "-d", "-s", "-f", f"--skip={start // 60}:{start % 60:02d}",
                              f"--until={(start + MUSIC_SECONDS) // 60}:{(start + MUSIC_SECONDS) % 60:02d}", "-o", path, flac])
        if code != 0:
            raise SystemExit(f"flac could not decode {flac}: {error}")
        inputs[name] = {"path": path, "origin": f"{addresses[0]} (SHA-256 {digest}), {MUSIC_SECONDS} s from {start} s"}
    # The stereo music signal with a LIST INFO chunk and a bext chunk before its data, to see which chunks lossyWAV keeps.
    path = source / "music-44k-16-st-chunks.wav"
    path.write_bytes(with_chunks(inputs["music-44k-16-st"]["path"].read_bytes(), [list_info(), bext()], before=b"data"))
    inputs["music-44k-16-st-chunks"] = {"path": path, "origin": "music-44k-16-st with a LIST INFO and a bext chunk before data"}
    return inputs


def riff_chunks(data):
    """(ID, body) of the chunks of a RIFF WAVE file, in order."""
    chunks, position = [], 12
    while position + 8 <= len(data):
        size = int.from_bytes(data[position + 4:position + 8], "little")
        chunks.append((data[position:position + 4], data[position + 8:position + 8 + size]))
        position += 8 + size + (size & 1)
    return chunks


def riff(chunks):
    body = b"".join(cid + len(c).to_bytes(4, "little") + c + (b"\0" if len(c) & 1 else b"") for cid, c in chunks)
    return b"RIFF" + (4 + len(body)).to_bytes(4, "little") + b"WAVE" + body


def with_chunks(data, extra, before):
    out = []
    for cid, body in riff_chunks(data):
        if cid == before:
            out += extra
        out.append((cid, body))
    return riff(out)


def list_info():
    return (b"LIST", b"INFOINAM" + (6).to_bytes(4, "little") + b"Title\0" + b"ISFT" + (9).to_bytes(4, "little") + b"Encspect\0\0")


def bext():
    return (b"bext", b"description".ljust(256, b"\0") + b"Encspect".ljust(32, b"\0") + bytes(32) + b"2026-11-08" + b"00:00:00"
            + bytes(8) + (1).to_bytes(2, "little") + bytes(64) + bytes(10) + bytes(180))


# The variants of channels and rates run at two presets only.
VARIANTS = ["music-44k-16-mono", "music-44k-16-6ch", "music-48k-24-st", "music-96k-24-st", "music-192k-24-st", "music-44k-8-st",
            "music-44k-32-st", "music-44k-16-st-chunks", "music-48k-24-6ch", "music-8k-24-st", "music-22k-24-st", "music-32k-24-st",
            "music-88k-24-st", "music-176k-24-st"]
VARIANT_PRESETS = ["standard", "portable"]
# The six-channel signals run at extraportable too.
EXTRA_VARIANTS = {"extraportable": ["music-44k-16-6ch", "music-48k-24-6ch"]}


def cases():
    every = [name for name, *_ in SIGNALS if name not in VARIANTS] + [name for name, *_ in MUSIC]
    for name, letter in PRESETS:
        yield f"preset-{name}", ["-q", letter], every + (VARIANTS if name in VARIANT_PRESETS else []) + EXTRA_VARIANTS.get(name, [])
    for name, arguments in SETTINGS:
        yield name, arguments, SETTINGS_INPUTS
    # The same run twice, to see whether the output is the same bytes.
    yield "standard-repeat", ["-q", "S"], ["music-44k-16-st", "goldberg-var4-16bit"]


def run_lossywav(lossywav, case, arguments, name, source, out):
    """Runs lossyWAV on a copy of source in an empty folder and moves every file it wrote to OUT/lossy/<case>."""
    target = out / "lossy" / case
    target.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as folder:
        folder = pathlib.Path(folder)
        copy = folder / f"{name}.wav"
        shutil.copyfile(source, copy)
        code, stdout, stderr = run([lossywav, copy.name, *arguments], cwd=folder)
        written = []
        for path in sorted(folder.iterdir()):
            if path.name != copy.name:
                destination = target / path.name
                shutil.move(str(path), destination)
                written.append(destination)
    (out / "logs").mkdir(exist_ok=True)
    (out / "logs" / f"{case}--{name}.txt").write_text(f"$ lossywav {name}.wav {' '.join(arguments)}\nexit {code}\n"
                                                    f"--- stdout\n{stdout}\n--- stderr\n{stderr}\n")
    return code, written


def grid_length(rate):
    """lossyWAV's block length as the files of the first runs show it: 512 samples up to 48 kHz, 1,024 up to 96 kHz, 2,048 above."""
    return 512 if rate <= 48000 else 1024 if rate <= 96000 else 2048


CODERS = {
    "flac-b512-foreign": (".flac", lambda i, o, m, r: ["flac", "-s", "-f", "-5", "-b", "512", "--keep-foreign-metadata", "-o", o, i]),
    "flac-b512": (".flac", lambda i, o, m, r: ["flac", "-s", "-f", "-5", "-b", "512", "-o", o, i]),
    "flac-b4096": (".flac", lambda i, o, m, r: ["flac", "-s", "-f", "-5", "-o", o, i]),
    "flac-bgrid": (".flac", lambda i, o, m, r: ["flac", "-s", "-f", "-5", "-b", str(grid_length(r)), "-o", o, i]),
    "wavpack": (".wv", lambda i, o, m, r: ["wavpack", "-q", "-y", i, "-o", o]),
    "wavpack-b512": (".wv", lambda i, o, m, r: ["wavpack", "-q", "-y", "--blocksize=512", i, "-o", o]),
    "wavpack-b512-merge": (".wv", lambda i, o, m, r: ["wavpack", "-q", "-y", "--blocksize=512", "--merge-blocks", i, "-o", o]),
    "mac-c2000": (".ape", lambda i, o, m, r: [m, i, o, "-c2000"]),
}


def codings(mac, full, rate):
    """The names of the lossless codings of each output. flac-b512-foreign is lossyFLAC with the WAV file's other chunks
    kept, so it holds every sample and chunk lossyWAV wrote; full adds the other block sizes; above 48 kHz, flac-bgrid
    codes frames of lossyWAV's block length."""
    yield "flac-b512-foreign"
    yield "wavpack"
    yield "wavpack-b512-merge"
    if mac:
        yield "mac-c2000"
    if full:
        yield from ("flac-b512", "flac-b4096", "wavpack-b512")
    if rate > 48000:
        yield "flac-bgrid"


def wav_rate(path):
    data = pathlib.Path(path).read_bytes()[:4096]
    at = data.find(b"fmt ")
    return int.from_bytes(data[at + 12:at + 16], "little") if at >= 0 else 44100


def code_file(path, folder, mac, record, full=False):
    folder.mkdir(parents=True, exist_ok=True)
    rate = wav_rate(path)
    for name in codings(mac, full, rate):
        extension, command = CODERS[name]
        output = folder / f"{path.stem}.{name}{extension}"
        code, _, stderr = run(command(path, output, mac, rate))
        entry = {"coding": name, "input": str(path), "exit": code}
        if code == 0 and output.exists():
            entry.update(path=str(output), sha256=sha256(output), size=output.stat().st_size)
        else:
            entry["error"] = stderr[-2000:]
        record.append(entry)


def tool_records(out, lossywav, mac):
    tools = out / "tools"
    tools.mkdir(parents=True, exist_ok=True)
    info = {"lossywav": {"path": str(lossywav), "sha256": sha256(lossywav), "source": f"{lossywav_tool.REPOSITORY} {lossywav_tool.COMMIT}"}}
    for arguments in HELP_ARGUMENTS:
        code, stdout, stderr = run([lossywav, *arguments], timeout=60)
        label = "-".join(a.strip("-") for a in arguments) or "none"
        (tools / f"help-{label}.txt").write_text(f"$ lossywav {' '.join(arguments)}\nexit {code}\n--- stdout\n{stdout}\n--- stderr\n{stderr}\n")
    for tool, arguments in [("flac", ["flac", "--version"]), ("wavpack", ["wavpack", "--version"]), ("gcc", ["g++", "--version"])]:
        code, stdout, stderr = run(arguments, timeout=60)
        info[tool] = (stdout or stderr).strip().splitlines()[0] if (stdout or stderr).strip() else f"exit {code}"
    if mac:
        code, stdout, stderr = run([mac], timeout=60)
        info["mac"] = {"path": str(mac), "first lines": (stdout + stderr).strip().splitlines()[:3]}
    return info


def probes(lossywav, out, inputs, manifest):
    """Black-box questions to lossyWAV itself: whether its --check finds its output with the fact chunk changed or removed,
    what it does with its own output as input, and what --stdout writes."""
    folder = out / "probes"
    folder.mkdir(exist_ok=True)
    lossy = out / "lossy" / "preset-standard" / "music-44k-16-st.lossy.wav"
    if not lossy.exists():
        return
    data = lossy.read_bytes()
    chunks = riff_chunks(data)
    variants = {
        "source": inputs["music-44k-16-st"]["path"].read_bytes(),
        "lossy": data,
        "lossy-no-fact": riff([(i, b) for i, b in chunks if i != b"fact"]),
        "lossy-fact-4-bytes": riff([(i, (44100 * 10 + 123).to_bytes(4, "little") if i == b"fact" else b) for i, b in chunks]),
        "lossy-fact-other-version": riff([(i, b.replace(b"lossyWAV 1.4.2", b"lossyWAV 9.9.9") if i == b"fact" else b) for i, b in chunks]),
        "lossy-fact-other-name": riff([(i, b.replace(b"lossyWAV", b"LOSSYwav") if i == b"fact" else b) for i, b in chunks]),
        "lossy-fact-after-data": riff([c for c in chunks if c[0] != b"fact"] + [c for c in chunks if c[0] == b"fact"]),
        "source-with-lossy-fact": riff([c for c in riff_chunks(inputs["music-44k-16-st"]["path"].read_bytes()) if c[0] == b"fmt "]
                                       + [c for c in chunks if c[0] == b"fact"]
                                       + [c for c in riff_chunks(inputs["music-44k-16-st"]["path"].read_bytes()) if c[0] == b"data"]),
    }
    record = []
    for name, content in variants.items():
        path = folder / f"{name}.wav"
        path.write_bytes(content)
        code, stdout, stderr = run([lossywav, path.name, "--check"], cwd=folder)
        (folder / f"check-{name}.txt").write_text(f"exit {code}\n--- stdout\n{stdout}\n--- stderr\n{stderr}\n")
        record.append({"probe": "check", "input": name, "sha256": sha256(path), "exit": code})
    # Its own output as input.
    code, written = run_lossywav(lossywav, "probe-reprocess", ["-q", "S"], "music-44k-16-st.lossy", lossy, out)
    record.append({"probe": "reprocess", "exit": code, "files": [str(p.relative_to(out)) for p in written]})
    # --stdout, as the lossyFLAC pipelines use it.
    with tempfile.TemporaryDirectory() as work:
        copy = pathlib.Path(work) / "music-44k-16-st.wav"
        shutil.copyfile(inputs["music-44k-16-st"]["path"], copy)
        result = subprocess.run([str(lossywav), copy.name, "-q", "S", "--stdout"], cwd=work, capture_output=True, timeout=900)
        path = folder / "stdout-music-44k-16-st.wav"
        path.write_bytes(result.stdout)
        record.append(output_record("stdout", result, path))
    # Input from STDIN ("-"), as foobar2000's lossyFLAC pipelines run it: to STDOUT, and to a file named by --stdinname.
    source = inputs["music-44k-16-st"]["path"].read_bytes()
    with tempfile.TemporaryDirectory() as work:
        result = subprocess.run([str(lossywav), "-", "-q", "S", "--stdout"], cwd=work, input=source, capture_output=True, timeout=900)
        path = folder / "stdin-stdout-music-44k-16-st.wav"
        path.write_bytes(result.stdout)
        record.append(output_record("stdin-stdout", result, path))
    with tempfile.TemporaryDirectory() as work:
        result = subprocess.run([str(lossywav), "-", "-q", "S", "--stdinname", "piped"], cwd=work, input=source, capture_output=True, timeout=900)
        written = sorted(pathlib.Path(work).iterdir())
        entry = {"probe": "stdin-file", "exit": result.returncode, "files": [p.name for p in written],
                 "stderr": result.stderr.decode("utf-8", "replace")[-1500:]}
        for p in written:
            shutil.move(str(p), folder / f"stdin-file-{p.name}")
            if p.suffix == ".wav":
                data = (folder / f"stdin-file-{p.name}").read_bytes()
                entry.setdefault("outputs", []).append(riff_summary(p.name, data))
        record.append(entry)
    manifest["probes"] = record


def riff_summary(name, data):
    """The chunks of a RIFF file with their sizes, the size its RIFF header states, and its length."""
    return {"name": name, "size": len(data), "riffSize": int.from_bytes(data[4:8], "little") if data[:4] == b"RIFF" else None,
            "chunks": [(i.decode("latin-1"), len(b)) for i, b in riff_chunks(data)] if data[:4] == b"RIFF" else None}


def output_record(probe, result, path):
    return {"probe": probe, "exit": result.returncode, "sha256": sha256(path), **riff_summary(path.name, result.stdout),
            "stderr": result.stderr.decode("utf-8", "replace")[-1500:]}


# The fixtures to commit, made at their committed length. Inputs: (name, source, seconds, frames added), where source is
# a signal of SIGNALS (made again at the length) or a recording of MUSIC (its first seconds, from the same start).
FIXTURE_INPUTS = {
    "marker": ("music-44k-16-st", 0.5, 123),
    "marker-chunks": ("music-44k-16-st", 0.5, 123),
    "jardin-3s": ("vieux-jardin-24bit", 3, 0),
    "goldberg-5s": ("goldberg-var4-16bit", 5, 0),
    "goldberg-3s": ("goldberg-var4-16bit", 3, 0),
    "synthetic-48k-3s": ("music-48k-24-st", 3, 123),
    "synthetic-96k-3s": ("music-96k-24-st", 3, 123),
    "synthetic-48k-6ch-3s": ("music-48k-24-6ch", 3, 123),
}

# (path in the repository, input, lossyWAV arguments, what is committed: "lossy", "lwcdf", "stdout" or a coding of CODERS,
# whether the file must show the pattern: at least twice the 20 changes the grid needs).
FIXTURES = [
    ("tests/fixtures/wav/lossywav-standard.wav", "marker", ["-q", "S"], "lossy", False),
    ("tests/fixtures/wav/lossywav-correction.lossy.wav", "marker", ["-q", "S", "--correction"], "lossy", False),
    ("tests/fixtures/wav/lossywav-correction.lwcdf.wav", "marker", ["-q", "S", "--correction"], "lwcdf", False),
    ("tests/fixtures/wav/lossywav-standard-chunks.wav", "marker-chunks", ["-q", "S"], "lossy", False),
    ("tests/fixtures/wav/lossywav-stdout.wav", "marker", ["-q", "S"], "stdout", False),
    ("tests/fixtures/monkeys-audio/external-lossywav.ape", "marker", ["-q", "S"], "mac-c2000", False),
    ("tests/fixtures/flac/external-lossyflac-b512.flac", "jardin-3s", ["-q", "S"], "flac-b512", True),
    ("tests/fixtures/flac/external-lossyflac-b512-foreign.flac", "jardin-3s", ["-q", "S"], "flac-b512-foreign", True),
    ("tests/fixtures/flac/external-lossyflac-b4096.flac", "jardin-3s", ["-q", "S"], "flac-b4096", True),
    ("tests/fixtures/flac/external-lossyflac-goldberg-standard.flac", "goldberg-5s", ["-q", "S"], "flac-b512", True),
    ("tests/fixtures/flac/external-lossyflac-goldberg-portable.flac", "goldberg-3s", ["-q", "P"], "flac-b512", True),
    ("tests/fixtures/flac/external-lossyflac-synthetic-b512.flac", "synthetic-48k-3s", ["-q", "P"], "flac-b512", True),
    ("tests/fixtures/flac/external-lossyflac-synthetic-b4096.flac", "synthetic-48k-3s", ["-q", "P"], "flac-b4096", True),
    ("tests/fixtures/flac/external-lossyflac-96k-b1024.flac", "synthetic-96k-3s", ["-q", "P"], "flac-bgrid", True),
    ("tests/fixtures/flac/external-lossyflac-96k-b512.flac", "synthetic-96k-3s", ["-q", "P"], "flac-b512", True),
    ("tests/fixtures/flac/external-lossyflac-6ch.flac", "synthetic-48k-6ch-3s", ["-q", "X"], "flac-b512-foreign", True),
    ("tests/fixtures/wavpack/external-lossywav.wv", "jardin-3s", ["-q", "S"], "wavpack", True),
    ("tests/fixtures/wavpack/external-lossywav-merge-blocks.wv", "jardin-3s", ["-q", "S"], "wavpack-b512-merge", True),
    ("tests/fixtures/wavpack/external-lossywav-synthetic-merge-blocks.wv", "synthetic-48k-3s", ["-q", "P"], "wavpack-b512-merge", True),
]


def fixture_input(name, inputs, folder):
    source, seconds, extra = FIXTURE_INPUTS[name]
    path = folder / f"{name}.wav"
    signal = next((s for s in SIGNALS if s[0] == source), None)
    if signal is not None:
        _, kind, rate, channels, bits, shift = signal
        path.write_bytes(flac_signal.wav(kind, rate, channels, int(rate * seconds) + extra, bits, shift=shift))
    else:
        data = inputs[source]["path"].read_bytes()
        chunks = riff_chunks(data)
        fmt = next(b for i, b in chunks if i == b"fmt ")
        align, rate = int.from_bytes(fmt[12:14], "little"), int.from_bytes(fmt[4:8], "little")
        chunks = [(i, b[:int(rate * seconds) * align] if i == b"data" else b) for i, b in chunks]
        path.write_bytes(riff(chunks))
    if name == "marker-chunks":
        path.write_bytes(with_chunks(path.read_bytes(), [list_info(), bext()], before=b"data"))
    return path


def make_fixtures(lossywav, mac, out, inputs, manifest):
    """Writes OUT/fixtures/<path> for every fixture lossyWAV and the coders could make, and records each in the manifest."""
    folder = out / "fixture-source"
    folder.mkdir(parents=True, exist_ok=True)
    made, record = {}, []
    for path, name, arguments, kind, behavior in FIXTURES:
        entry = {"path": path, "input": name, "arguments": arguments, "kind": kind}
        source = folder / f"{name}.wav"
        if not source.exists():
            fixture_input(name, inputs, folder)
        entry["inputSha256"] = sha256(source)
        key = (name, tuple(arguments))
        if key not in made:
            case = f"fixture-{name}-{'-'.join(a.strip('-') for a in arguments)}"
            code, written = run_lossywav(lossywav, case, arguments, name, source, out)
            made[key] = (code, written)
        code, written = made[key]
        lossy = next((p for p in written if p.name.endswith(".lossy.wav")), None)
        target = out / "fixtures" / path
        target.parent.mkdir(parents=True, exist_ok=True)
        if kind == "lossy" and lossy:
            shutil.copyfile(lossy, target)
        elif kind == "lwcdf":
            lwcdf = next((p for p in written if p.name.endswith(".lwcdf.wav")), None)
            if lwcdf:
                shutil.copyfile(lwcdf, target)
        elif kind == "stdout":
            with tempfile.TemporaryDirectory() as work:
                shutil.copyfile(source, pathlib.Path(work) / source.name)
                result = subprocess.run([str(lossywav), source.name, *arguments, "--stdout"], cwd=work, capture_output=True, timeout=900)
                if result.returncode == 0:
                    target.write_bytes(result.stdout)
                entry["stdoutExit"] = result.returncode
        elif lossy:
            extension, command = CODERS[kind]
            if kind != "mac-c2000" or mac:
                result = run(command(lossy, target, mac, wav_rate(lossy)))
                entry["codingExit"] = result[0]
        entry["lossyWavExit"] = code
        if target.exists():
            entry.update(sha256=sha256(target), size=target.stat().st_size)
        if lossy:
            figures = lossywav_figures.figures(lossy)
            entry["figures"] = {k: figures[k] for k in ("lowBitsUnused", "spans", "silentSpans", "quietSpans", "spansAboveLowBitsUnused",
                                                        "changes", "grid", "changesOnGrid", "timesWithEqualCounts", "times")}
            entry["passesGate"] = figures["changes"] >= 2 * lossywav_figures.MIN_CHANGES and figures["grid"] is not None
            entry["mustShowPattern"] = behavior
            check = run([lossywav, lossy.name, "--check"], cwd=lossy.parent)
            entry["lossyWavCheck"] = {"exit": check[0], "stderr": check[2].strip().splitlines()[-1:] if check[2].strip() else []}
        if target.exists() and target.suffix == ".flac":
            entry["flacUnits"] = {k: v for k, v in lossywav_figures.flac_units(target).items() if k != "file"}
        record.append(entry)
    manifest["fixtures"] = record
    return [e["path"] for e in record if e.get("mustShowPattern") and not e.get("passesGate")]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("out")
    parser.add_argument("--lossywav", required=True)
    parser.add_argument("--mac")
    options = parser.parse_args()
    out = pathlib.Path(options.out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    lossywav = pathlib.Path(options.lossywav).resolve()
    mac = pathlib.Path(options.mac).resolve() if options.mac else None

    manifest = {"tools": tool_records(out, lossywav, mac), "inputs": {}, "runs": [], "codings": []}
    with tempfile.TemporaryDirectory() as work:
        inputs = make_sources(out, pathlib.Path(work))
    for name, entry in inputs.items():
        manifest["inputs"][name] = {"path": str(entry["path"].relative_to(out)), "sha256": sha256(entry["path"]), "origin": entry["origin"]}
        code_file(entry["path"], out / "coded" / "source", mac, manifest["codings"], full=True)

    refused = 0
    for case, arguments, names in cases():
        for name in names:
            code, written = run_lossywav(lossywav, case, arguments, name, inputs[name]["path"], out)
            refused += code != 0
            manifest["runs"].append({"case": case, "arguments": arguments, "input": name, "exit": code,
                                     "files": [{"path": str(p.relative_to(out)), "sha256": sha256(p), "size": p.stat().st_size} for p in written]})
            for path in written:
                if path.suffix == ".wav" and ".lwcdf" not in path.name:
                    keep = case in KEEP_WAV
                    code_file(path, out / "coded" / case, mac, manifest["codings"], full=keep)
                    if not keep:
                        # flac-b512-foreign holds the same samples and chunks; the WAV is dropped to keep the artifact small.
                        path.unlink()

    probes(lossywav, out, inputs, manifest)
    short = make_fixtures(lossywav, mac, out, inputs, manifest)
    for entry in manifest["codings"]:
        for key in ("input", "path"):
            if key in entry:
                entry[key] = str(pathlib.Path(entry[key]).relative_to(out))
    (out / "manifest.json").write_text(json.dumps(manifest, indent=1) + "\n")
    runs = len(manifest["runs"])
    print(f"{runs} runs, {refused} refused; {sum(e['exit'] != 0 for e in manifest['codings'])} codings failed")
    print(f"{len(manifest['fixtures'])} fixtures, {sum('sha256' in e for e in manifest['fixtures'])} made; below the change gate: {short or 'none'}")
    presets = [r for r in manifest["runs"] if r["case"].startswith("preset-")]
    if not any(r["exit"] == 0 and r["files"] for r in presets):
        raise SystemExit("lossyWAV wrote nothing at any preset")


if __name__ == "__main__":
    main()
