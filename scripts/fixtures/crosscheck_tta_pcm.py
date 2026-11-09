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
- `tta -d` decodes the file to WAVE; the reference is the bytes after the header of its data chunk (never the size the header declares).
  A file it refuses or stops on (8-bit, 8 channels) has no `tta -d` reference. `tta -dp secret` decodes the file written with that password; the analyzer does
  not (audio format 2), so that file is checked to produce no stream.
- FFmpeg decodes the file to raw PCM of the same width: a second reference, and the only one where `tta -d` gives none.
- Each reference's MD5 must equal the manifest's decoded_pcm_md5 (the samples of the signal the encoder was given), also for the file written with a password,
  which `tta -dp secret` decodes and the analyzer does not.
- compare_pcm.py compares the analyzer's samples with each reference by position, with no difference allowed (--max-lsb 0 --rms-lsb 0); the analyzer must
  also have as many samples as the reference, in one run from position 0.

A file in a Matroska track that stores no header (neither muxer writes one) has no decoded stream: it is checked to have none. The script fails if a
sample differs, if a count differs, if a tool fails where it is expected to work, or if no stream was compared.
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


def compare(manifest, reference, bits, channels, label):
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
    if result["notComparedOnlyInReference"] or result["notComparedOnlyInAnalyzer"]:
        problems.append(f"{label}: positions only one side has: reference {result['notComparedOnlyInReference']}, analyzer {result['notComparedOnlyInAnalyzer']}")
    return problems


def manifest_problems(path, references, channels, bits):
    """The references against the manifest's decoded_pcm_md5 and decoded_samples."""
    manifest_file = path.with_name(path.name.rsplit(".", 1)[0] + ".expected.json")
    if not manifest_file.exists():
        return []
    manifest = json.loads(manifest_file.read_text(encoding="utf-8"))
    problems = []
    for label, reference in references:
        if hashlib.md5(reference).hexdigest() != manifest["decoded_pcm_md5"]:
            problems.append(f"{label} decodes to MD5 {hashlib.md5(reference).hexdigest()}, the manifest says {manifest['decoded_pcm_md5']}")
        if len(reference) != manifest["decoded_samples"] * channels * (bits // 8):
            problems.append(f"{label} decodes {len(reference) // (channels * (bits // 8))} samples, the manifest says {manifest['decoded_samples']}")
    return problems


def header_of(path):
    """(bits, channels) of the TTA1 header in a TTA file, or None (a Matroska track without a CodecPrivate stores none)."""
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
            if path.name in PASSWORDS or header is None:
                why = "audio format 2, which the decoder does not take" if path.name in PASSWORDS else "no TTA1 header, so the samples of its last frame are not known"
                problems = []
                if streams:
                    problems.append(f"the analyzer decoded {len(streams)} streams, but the file has {why}")
                if path.name in PASSWORDS:
                    password_bits, password_channels = header_of(path)
                    pcm, reason = tta_reference(program, path, base)
                    problems += manifest_problems(path, [("tta -dp", pcm)], password_channels, password_bits) if pcm is not None else [reason]
                failed += bool(problems)
                print(f"FAIL {path.name}: " + "; ".join(problems) if problems else f"ok   {path.name}: no decoded stream ({why})")
                continue
            bits, channels = header
            if len(streams) != 1:
                print(f"FAIL {path.name}: the analyzer decoded {len(streams)} streams")
                failed += 1
                continue
            entry = streams[0]
            problems = []
            references = []
            work = base / f"work{number}"
            work.mkdir(exist_ok=True)
            pcm, why = tta_reference(program, path, work)
            if pcm is not None:
                references.append(("tta -d", pcm))
            ffmpeg, ffmpeg_why = ffmpeg_reference(args.ffmpeg, path, bits)
            if ffmpeg is not None:
                references.append(("ffmpeg", ffmpeg))
            if not references:
                print(f"FAIL {path.name}: no reference ({why}; {ffmpeg_why})")
                failed += 1
                continue
            problems += manifest_problems(path, references, channels, bits)
            if entry["runs"] != [[0, entry["samples"]]]:
                problems.append(f"the analyzer's runs are {entry['runs']}, not one from position 0")
            for label, reference in references:
                if len(reference) % (channels * (bits // 8)):
                    problems.append(f"{label}: {len(reference)} bytes are not whole sample frames")
                    continue
                problems += compare(manifest, reference, bits, channels, label.split()[0])
            if why:
                note = f" ({why}; ffmpeg is the only reference)"
            else:
                note = "" if len(references) == 2 else " (ffmpeg is the only reference)"
            if problems:
                failed += 1
                print(f"FAIL {path.name}: " + "; ".join(problems))
            else:
                compared += 1
                print(f"ok   {path.name}: {entry['samples']} samples per channel equal {' and '.join(label for label, _ in references)}{note}")
    if compared == 0:
        print("FAIL: no stream was compared")
        return 1
    print(f"{compared} files compared, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
