#!/usr/bin/env python3
"""Compares the PCM that Encspect's TTA decoder delivers with the TTA authors' decoder and FFmpeg's, sample for sample
(docs/formats/tta.md, "Decoding"; docs/development.md, "TTA decoding").

    crosscheck_tta_pcm.py [--pcmdump CMD] [--tta PATH] [--ffmpeg PATH] [--keep DIR] [FILE ...]

CMD runs a built tests/Encspect.PcmDump, for example "dotnet tests/Encspect.PcmDump/bin/Release/net10.0/Encspect.PcmDump.dll" (the default is that
path relative to the repository). PATH is a libtta-c 2.3 `tta` (libtta_tool.build() builds it from the pinned archive when --tta is not given; the
program is a black box, its source is not read: docs/design/decoders.md, decision 10). With no FILE the script takes the encoder-written TTA fixtures
(tests/fixtures/tta/external-*.tta) and the TTA tracks of Matroska (tests/fixtures/matroska/external-*tta*.mka).

For each file:

- PcmDump writes the samples of the decoded stream, in the canonical layout and with the runs of positions they cover.
- `tta -d` decodes the file to WAVE; the reference is the bytes after the header of its data chunk (never the size the header declares). For a Matroska
  track it decodes the TTA file the muxer was given (the manifest's encoder inputs), whose frames the track holds. `tta -dp secret` decodes the file
  written with that password; the analyzer does not (audio format 2), so that file is checked to produce no stream.
- FFmpeg decodes the file to raw PCM of the same width: a second reference.
- Which file has no reference of a tool, and why, is listed below (TTA_REFUSES, NO_TTA_SOURCE, FFMPEG_SHORT); a tool that refuses or fails on any other
  file fails the run, and so does a listed refusal that no longer happens.
- Each reference's MD5 must equal the manifest's decoded_pcm_md5 (the samples of the signal the encoder was given; for a Matroska track the source's).
- compare_pcm.py compares the analyzer's samples with each reference by position, with no difference allowed (--max-lsb 0 --rms-lsb 0); the analyzer must
  also have as many samples as the reference, in one run from position 0 (FFmpeg's prefix for the files of FFMPEG_SHORT).

The script fails if a sample differs, if a count differs, if a tool fails where it is not listed to, or if no stream was compared.
"""

import argparse
import hashlib
import json
import pathlib
import shlex
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import libtta_tool  # noqa: E402

ROOT = HERE.parents[1]
FIXTURES = ROOT / "tests" / "fixtures"
DEFAULT_PCMDUMP = "dotnet " + str(ROOT / "tests" / "Encspect.PcmDump" / "bin" / "Release" / "net10.0" / "Encspect.PcmDump.dll")
FORMATS = {8: ("int8", "s8"), 16: ("int16", "s16le"), 24: ("int24", "s24le")}
PASSWORDS = {"external-libtta-16-stereo-password.tta": "secret"}

# Files `tta -d` refuses ("not compatible file format"; libtta-c 2.3 takes 16 and 24 bits and at most 6 channels).
TTA_REFUSES = {"external-8-stereo.tta": "8 bits per sample", "external-16-8ch.tta": "8 channels"}

# Matroska tracks whose frames no TTA file holds, so `tta -d` has nothing to decode.
NO_TTA_SOURCE = {"external-ffmpeg-tta.mka": "FFmpeg encoded the track from a signal"}

# Tracks FFmpeg decodes short: without the header it takes the last frame for a whole one and fails on it ("Invalid data found"), and writes the frames
# before it. Its samples are compared as a prefix of the analyzer's.
FFMPEG_SHORT = {
    "external-ffmpeg-tta-last-frame-one-sample.mka": "the last frame holds one sample",
    "external-mkvmerge-tta-last-frame-one-sample.mka": "the last frame holds one sample",
}


def default_files():
    return sorted(FIXTURES.glob("tta/external-*.tta")) + sorted(FIXTURES.glob("matroska/external-*tta*.mka"))


def data_chunk(wav):
    """The bytes after the header of the data chunk of a WAVE file that `tta -d` wrote, up to the end of the file."""
    if wav[:4] != b"RIFF" or wav[8:12] != b"WAVE":
        return None
    position = 12
    while position + 8 <= len(wav):
        name = wav[position:position + 4]
        size = int.from_bytes(wav[position + 4:position + 8], "little")
        if name == b"data":
            return wav[position + 8:]
        position += 8 + size + (size & 1)
    return None


def tta_reference(program, path, directory):
    """The raw samples `tta -d` writes, or (None, why)."""
    password = PASSWORDS.get(path.name)
    target = directory / "tta.wav"
    target.unlink(missing_ok=True)
    arguments = [str(program), "-dp", password] if password else [str(program), "-d"]
    result = subprocess.run([*arguments, str(path), str(target)], capture_output=True, text=True)
    if not target.exists() or target.stat().st_size == 0:
        return None, f"tta -d exits {result.returncode} and writes nothing"
    pcm = data_chunk(target.read_bytes())
    if result.returncode != 0 or pcm is None or not pcm:
        return None, f"tta -d exits {result.returncode} and writes {'no data' if not pcm else 'a file'}"
    return pcm, None


def ffmpeg_reference(ffmpeg, path, bits):
    result = subprocess.run([ffmpeg, "-v", "error", "-nostdin", "-i", str(path), "-f", FORMATS[bits][1], "-"], capture_output=True)
    if result.returncode != 0 or not result.stdout:
        return None, f"ffmpeg exits {result.returncode}: {result.stderr.decode('utf-8', 'replace')[-200:]}"
    return result.stdout, None


def compare(manifest, reference, bits, channels, label, is_prefix=False):
    """Runs compare_pcm.py with no difference allowed; returns the problems."""
    raw = manifest.parent / f"{label}.raw"
    raw.write_bytes(reference)
    output = subprocess.run([sys.executable, str(HERE / "compare_pcm.py"), str(manifest), "--reference", str(raw), "--reference-format", FORMATS[bits][0],
                             "--reference-channels", str(channels), "--max-lsb", "0", "--rms-lsb", "0", "--json"], capture_output=True, text=True)
    if output.returncode == 2:
        return [f"{label}: {output.stderr.strip()}"]
    result = json.loads(output.stdout)
    problems = []
    if output.returncode != 0:
        problems.append(f"{label}: the samples differ (largest {max(c['max'] for c in result['channels'])} LSB)")
    tail = result["notComparedOnlyInAnalyzer"]
    if is_prefix and len(tail) == 1 and tail[0][1] - tail[0][0] > 0 and not result["notComparedOnlyInReference"]:
        return problems
    if result["notComparedOnlyInReference"] or tail:
        problems.append(f"{label}: positions only one side has: reference {result['notComparedOnlyInReference']}, analyzer {result['notComparedOnlyInAnalyzer']}")
    return problems


def manifest_of(path):
    manifest_file = path.with_name(path.name.rsplit(".", 1)[0] + ".expected.json")
    return json.loads(manifest_file.read_text(encoding="utf-8")) if manifest_file.exists() else None


def source_of(path):
    """The TTA file a Matroska fixture's muxer was given, or None."""
    manifest = manifest_of(path) or {}
    inputs = manifest.get("encoder", {}).get("inputs", [])
    return FIXTURES / inputs[0] if inputs and inputs[0].endswith(".tta") else None


def manifest_problems(path, references, channels, bits):
    """The references against the manifest's decoded_pcm_md5 and decoded_samples."""
    manifest_file = path.with_name(path.name.rsplit(".", 1)[0] + ".expected.json")
    if not manifest_file.exists():
        return []
    manifest = json.loads(manifest_file.read_text(encoding="utf-8"))
    if "decoded_pcm_md5" not in manifest:
        # A Matroska manifest states no samples; its track's are those of its source, or (a track FFmpeg encoded) FFmpeg's and the analyzer's alone.
        return [] if path.suffix != ".tta" else [f"{manifest_file.name} has no decoded_pcm_md5"]
    problems = []
    for label, reference in references:
        if hashlib.md5(reference).hexdigest() != manifest["decoded_pcm_md5"]:
            problems.append(f"{label} decodes to MD5 {hashlib.md5(reference).hexdigest()}, the manifest says {manifest['decoded_pcm_md5']}")
        if len(reference) != manifest["decoded_samples"] * channels * (bits // 8):
            problems.append(f"{label} decodes {len(reference) // (channels * (bits // 8))} samples, the manifest says {manifest['decoded_samples']}")
    return problems


def header_of(path):
    """(bits, channels) of the TTA1 header in a TTA file, or of the TrackEntry of a Matroska fixture's manifest; None where neither states them."""
    if path.suffix != ".tta":
        tracks = (manifest_of(path) or {}).get("tracks", [])
        return (tracks[0]["bits"], tracks[0]["channels"]) if tracks and tracks[0].get("bits") else None
    data = path.read_bytes()
    at = data.find(b"TTA1") if path.suffix == ".tta" else -1
    if at < 0 or len(data) < at + 22:
        return None
    return int.from_bytes(data[at + 8:at + 10], "little"), int.from_bytes(data[at + 6:at + 8], "little")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("files", nargs="*", type=pathlib.Path)
    parser.add_argument("--pcmdump", default=DEFAULT_PCMDUMP)
    parser.add_argument("--tta", type=pathlib.Path, help="a libtta-c 2.3 tta program (default: built by libtta_tool.py)")
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--keep", type=pathlib.Path, help="keep the dumps and the references in this folder")
    args = parser.parse_args(argv)
    files = args.files or default_files()
    if not files:
        print("no TTA file")
        return 2
    program = args.tta or libtta_tool.build()
    failed = compared = 0
    with tempfile.TemporaryDirectory() as temporary:
        base = args.keep or pathlib.Path(temporary)
        base.mkdir(parents=True, exist_ok=True)
        dump = base / "dump"
        for number, path in enumerate(files):
            result = subprocess.run(shlex.split(args.pcmdump) + [str(dump), str(path)], capture_output=True)
            if result.returncode != 0:
                print(f"FAIL {path.name}: PcmDump exits {result.returncode}: {result.stderr.decode('utf-8', 'replace')[-300:]}")
                failed += 1
                continue
            # PcmDump numbers the files of one call, and each call here has one.
            manifest = dump / f"0000-{path.name}.pcm.json"
            streams = json.loads(manifest.read_text())["streams"] if manifest.exists() else []
            header = header_of(path)
            if path.name in PASSWORDS:
                problems = [f"the analyzer decoded {len(streams)} streams, but the file has audio format 2"] if streams else []
                pcm, reason = tta_reference(program, path, base)
                problems += manifest_problems(path, [("tta -dp", pcm)], header[1], header[0]) if pcm is not None else [reason]
                failed += bool(problems)
                print(f"FAIL {path.name}: " + "; ".join(problems) if problems else f"ok   {path.name}: no decoded stream (audio format 2, which the decoder does not take)")
                continue
            if header is None or len(streams) != 1:
                print(f"FAIL {path.name}: the analyzer decoded {len(streams)} streams" + ("" if header else "; no bit depth and channels known"))
                failed += 1
                continue
            bits, channels = header
            entry = streams[0]
            problems = []
            references = []
            notes = []
            work = base / f"work{number}"
            work.mkdir(exist_ok=True)
            source = path if path.suffix == ".tta" else source_of(path)
            if source is None:
                if path.name not in NO_TTA_SOURCE:
                    problems.append("no TTA file to give tta -d")
                notes.append(NO_TTA_SOURCE.get(path.name, ""))
            else:
                pcm, why = tta_reference(program, source, work)
                if pcm is not None:
                    references.append(("tta -d", pcm))
                    if path.name in TTA_REFUSES:
                        problems.append(f"tta -d is listed to refuse it ({TTA_REFUSES[path.name]}) and decodes it")
                elif path.name in TTA_REFUSES:
                    notes.append(f"tta -d refuses {TTA_REFUSES[path.name]}")
                else:
                    problems.append(why)
            ffmpeg, ffmpeg_why = ffmpeg_reference(args.ffmpeg, path, bits)
            if ffmpeg is not None:
                references.append(("ffmpeg", ffmpeg))
            else:
                problems.append(ffmpeg_why)
            if path.name in FFMPEG_SHORT:
                notes.append(f"FFmpeg decodes it short: {FFMPEG_SHORT[path.name]}")
            if source is not None and source != path:
                problems += manifest_problems(source, [r for r in references if r[0] == "tta -d"], channels, bits)
            else:
                problems += manifest_problems(path, [r for r in references if not (r[0] == "ffmpeg" and path.name in FFMPEG_SHORT)], channels, bits)
            if entry["runs"] != [[0, entry["samples"]]]:
                problems.append(f"the analyzer's runs are {entry['runs']}, not one from position 0")
            for label, reference in references:
                if len(reference) % (channels * (bits // 8)):
                    problems.append(f"{label}: {len(reference)} bytes are not whole sample frames")
                    continue
                problems += compare(manifest, reference, bits, channels, label.split()[0], is_prefix=label == "ffmpeg" and path.name in FFMPEG_SHORT)
            note = "; ".join(n for n in notes if n)
            if problems:
                failed += 1
                print(f"FAIL {path.name}: " + "; ".join(problems))
            else:
                compared += 1
                print(f"ok   {path.name}: {entry['samples']} samples per channel equal {' and '.join(label for label, _ in references)}{f' ({note})' if note else ''}")
    if compared == 0:
        print("FAIL: no stream was compared")
        return 1
    print(f"{compared} files compared, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
