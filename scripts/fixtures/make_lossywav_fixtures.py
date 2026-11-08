#!/usr/bin/env python3
"""Writes the files the lossyWAV design (docs/design/lossywav-detection.md) is checked against: lossyWAV's output at its
quality presets and settings, and that output coded again with FLAC (lossyFLAC), WavPack and Monkey's Audio.

lossyWAV is GPL. It is used here only as a black-box tool: the workflow lossywav-fixtures.yml builds the POSIX port
(github.com/MoSal/lossywav-for-posix, a pinned commit of lossyWAV 1.4.2) with its own Makefile.unix on a CI runner and
passes the binary to this script; nothing of its source is read or ported, and it is never run outside CI. The files
are uploaded as an artifact and analyzed as data.

Inputs, all 44.1 kHz unless named otherwise:
- the deterministic signals of flac_signal.py (tones under a tremolo, then noise; loud noise; silence around a tone),
  in 16 and 24 bits, mono, stereo and six channels, at 44.1, 48 and 96 kHz, with a length that is not a multiple of
  any block size;
- the same music signal at 24 bits with its low 8 bits zero (16-bit audio in a 24-bit file);
- 30 s of two recordings from the Internet Archive, pinned by SHA-256: Kimiko Ishizaka's Goldberg Variations,
  Variation 4 (Musopen, public domain mark, 16 bits) and Anonymous420's "D'un vieux jardin" (CC0, 24 bits).

Every run of lossyWAV is a case: a name, the arguments and the inputs. A case that lossyWAV refuses is recorded with its
exit code and output, not dropped; the help texts are recorded first, so the next run can name the settings exactly.

Usage: make_lossywav_fixtures.py OUT --lossywav PATH [--mac PATH]
Writes OUT/source (inputs), OUT/lossy/<case>/ (what lossyWAV wrote, its WAV output only for the cases of KEEP_WAV:
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

COMMIT = "c3a22be0e1d8d516e3aa1761b6822ec68c06f920"

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
    return inputs


# The variants of channels and rates run at two presets only.
VARIANTS = ["music-44k-16-mono", "music-44k-16-6ch", "music-48k-24-st", "music-96k-24-st"]
VARIANT_PRESETS = ["standard", "portable"]


def cases():
    every = [name for name, *_ in SIGNALS if name not in VARIANTS] + [name for name, *_ in MUSIC]
    for name, letter in PRESETS:
        yield f"preset-{name}", ["-q", letter], every + (VARIANTS if name in VARIANT_PRESETS else [])
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


def codings(mac, full):
    """(name, extension, command builder) of the lossless codings of each output. flac-b512-foreign is lossyFLAC with the
    WAV file's other chunks kept, so it holds every sample and chunk lossyWAV wrote; full adds the other block sizes."""
    yield "flac-b512-foreign", ".flac", lambda i, o: ["flac", "-s", "-f", "-5", "-b", "512", "--keep-foreign-metadata", "-o", o, i]
    yield "wavpack", ".wv", lambda i, o: ["wavpack", "-q", "-y", i, "-o", o]
    if mac:
        yield "mac-c2000", ".ape", lambda i, o: [mac, i, o, "-c2000"]
    if full:
        yield "flac-b512", ".flac", lambda i, o: ["flac", "-s", "-f", "-5", "-b", "512", "-o", o, i]
        yield "flac-b4096", ".flac", lambda i, o: ["flac", "-s", "-f", "-5", "-o", o, i]
        yield "wavpack-b512", ".wv", lambda i, o: ["wavpack", "-q", "-y", "--blocksize=512", i, "-o", o]


def code_file(path, folder, mac, record, full=False):
    folder.mkdir(parents=True, exist_ok=True)
    for name, extension, command in codings(mac, full):
        output = folder / f"{path.stem}.{name}{extension}"
        code, _, stderr = run(command(path, output))
        entry = {"coding": name, "input": str(path), "exit": code}
        if code == 0 and output.exists():
            entry.update(path=str(output), sha256=sha256(output), size=output.stat().st_size)
        else:
            entry["error"] = stderr[-2000:]
        record.append(entry)


def tool_records(out, lossywav, mac):
    tools = out / "tools"
    tools.mkdir(parents=True, exist_ok=True)
    info = {"lossywav": {"path": str(lossywav), "sha256": sha256(lossywav), "source": f"github.com/MoSal/lossywav-for-posix {COMMIT}"}}
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

    for entry in manifest["codings"]:
        for key in ("input", "path"):
            if key in entry:
                entry[key] = str(pathlib.Path(entry[key]).relative_to(out))
    (out / "manifest.json").write_text(json.dumps(manifest, indent=1) + "\n")
    runs = len(manifest["runs"])
    print(f"{runs} runs, {refused} refused; {sum(e['exit'] != 0 for e in manifest['codings'])} codings failed")
    presets = [r for r in manifest["runs"] if r["case"].startswith("preset-")]
    if not any(r["exit"] == 0 and r["files"] for r in presets):
        raise SystemExit("lossyWAV wrote nothing at any preset")


if __name__ == "__main__":
    main()
