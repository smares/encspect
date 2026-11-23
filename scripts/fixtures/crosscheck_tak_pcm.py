#!/usr/bin/env python3
"""Compares the PCM that Encspect's TAK decoder delivers with TAK's own decoder and FFmpeg's, sample for sample
(docs/formats/tak.md, "Decoding"; docs/development.md, "TAK decoding (clean-room)").

    crosscheck_tak_pcm.py [--pcmdump CMD] [--ffmpeg PATH] [--sdk-records FILE] [--keep DIR] [FILE ...]

CMD runs a built tests/Encspect.PcmDump, for example "dotnet tests/Encspect.PcmDump/bin/Release/net10.0/Encspect.PcmDump.dll" (the default is that path
relative to the repository). With no FILE the script takes the encoder-written TAK fixtures (tests/fixtures/tak/external-*.tak).

TAK's own decoder runs on Windows only (Takc.exe and the SDK's decoder library, tak-fixtures.yml), so this Linux job reads its results from records:

- Every provenance record (tests/fixtures/tak/<name>.tak.provenance.json) holds the decode check of the Windows run that wrote the file: `Takc -d` of it gave a
  WAVE file whose audio equals the input's (decode.audioEqual). That input is the signal the manifest's decoded_pcm_md5 is taken from (tak_decoded.py checks
  its SHA-256), so the check says that TAK's decoder gives the manifest's samples; the script fails where a record says otherwise.
- --sdk-records names the file the `decode` job of tak-fixtures.yml writes (decode_windows_tak_fixtures.ps1: for each committed fixture the MD5 and the
  number of samples of what `Takc -d` decodes, in the canonical layout); the default is tests/fixtures/tak/sdk-decoded.json where it exists. Each record of an
  encoder-written file must equal the manifest's decoded_pcm_md5 and decoded_samples. Without a file the column is the provenance records alone.

For each file:

- PcmDump writes the samples of the decoded stream, in the canonical layout and with the runs of positions they cover.
- FFmpeg decodes the file to raw PCM of the same width: the second reference. (FFmpeg is a black box; its source is not read.)
- Each reference's MD5 must equal the manifest's decoded_pcm_md5 (the samples of the signal the encoder was given).
- compare_pcm.py compares the analyzer's samples with FFmpeg's by position, with no difference allowed (--max-lsb 0 --rms-lsb 0); the analyzer must also have as many
  samples as the reference, in one run from position 0.

The script fails if a sample differs, if a count differs, if FFmpeg fails, if a record disagrees or if no stream was compared.
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
ROOT = HERE.parents[1]
FIXTURES = ROOT / "tests" / "fixtures"
DEFAULT_PCMDUMP = "dotnet " + str(ROOT / "tests" / "Encspect.PcmDump" / "bin" / "Release" / "net10.0" / "Encspect.PcmDump.dll")
DEFAULT_RECORDS = FIXTURES / "tak" / "sdk-decoded.json"
FORMATS = {8: ("int8", "s8"), 16: ("int16", "s16le"), 24: ("int24", "s24le")}


def default_files():
    return sorted(FIXTURES.glob("tak/external-*.tak"))


def manifest_of(path):
    return json.loads(path.with_name(path.name.rsplit(".", 1)[0] + ".expected.json").read_text(encoding="utf-8"))


def provenance_of(path):
    return json.loads(path.with_name(path.name + ".provenance.json").read_text(encoding="utf-8"))


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


def sdk_problems(path, manifest, provenance, records):
    """TAK's own decoder, by the records of the Windows runs."""
    problems = []
    decode = provenance.get("decode") or {}
    if decode.get("exit") != 0 or decode.get("audioEqual") is not True:
        problems.append(f"the provenance's decode check of Takc -d is {decode}, not an exit of 0 with audio equal to the input")
    if records is not None:
        record = records.get("files", {}).get(path.name)
        if record is None:
            problems.append(f"{path.name} has no record in the SDK records")
        elif record.get("pcmMd5") != manifest["decoded_pcm_md5"] or record.get("samples") != manifest["decoded_samples"]:
            problems.append(f"Takc -d decodes {record.get('samples')} samples with MD5 {record.get('pcmMd5')}, the manifest says {manifest['decoded_samples']} and {manifest['decoded_pcm_md5']}")
    return problems


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("files", nargs="*", type=pathlib.Path)
    parser.add_argument("--pcmdump", default=DEFAULT_PCMDUMP)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--sdk-records", type=pathlib.Path, default=DEFAULT_RECORDS)
    parser.add_argument("--keep", type=pathlib.Path, help="keep the dumps and the references in this folder")
    args = parser.parse_args(argv)
    files = args.files or default_files()
    if not files:
        print("no TAK file")
        return 2
    records = json.loads(args.sdk_records.read_text(encoding="utf-8")) if args.sdk_records.exists() else None
    failed = compared = 0
    with tempfile.TemporaryDirectory() as temporary:
        base = args.keep or pathlib.Path(temporary)
        base.mkdir(parents=True, exist_ok=True)
        dump = base / "dump"
        for path in files:
            manifest = manifest_of(path)
            provenance = provenance_of(path)
            info = manifest["stream_info"]
            bits, channels = info["bits_per_sample"], info["channels"]
            result = subprocess.run(shlex.split(args.pcmdump) + [str(dump), str(path)], capture_output=True)
            if result.returncode != 0:
                print(f"FAIL {path.name}: PcmDump exits {result.returncode}: {result.stderr.decode('utf-8', 'replace')[-300:]}")
                failed += 1
                continue
            # PcmDump numbers the files of one call, and each call here has one.
            dump_manifest = dump / f"0000-{path.name}.pcm.json"
            streams = json.loads(dump_manifest.read_text())["streams"] if dump_manifest.exists() else []
            if len(streams) != 1:
                print(f"FAIL {path.name}: the analyzer decoded {len(streams)} streams")
                failed += 1
                continue
            entry = streams[0]
            problems = sdk_problems(path, manifest, provenance, records)
            reference, why = ffmpeg_reference(args.ffmpeg, path, bits)
            if reference is None:
                problems.append(why)
            else:
                if hashlib.md5(reference).hexdigest() != manifest["decoded_pcm_md5"]:
                    problems.append(f"ffmpeg decodes to MD5 {hashlib.md5(reference).hexdigest()}, the manifest says {manifest['decoded_pcm_md5']}")
                if len(reference) != manifest["decoded_samples"] * channels * (bits // 8):
                    problems.append(f"ffmpeg decodes {len(reference) // (channels * (bits // 8))} samples, the manifest says {manifest['decoded_samples']}")
                problems += compare(dump_manifest, reference, bits, channels, "ffmpeg")
            if entry["samples"] != manifest["decoded_samples"]:
                problems.append(f"the analyzer decodes {entry['samples']} samples, the manifest says {manifest['decoded_samples']}")
            if entry["runs"] != [[0, entry["samples"]]]:
                problems.append(f"the analyzer's runs are {entry['runs']}, not one from position 0")
            if problems:
                failed += 1
                print(f"FAIL {path.name}: " + "; ".join(problems))
            else:
                compared += 1
                column = "Takc -d (records) and ffmpeg" if records is not None else "Takc -d (the provenance's decode check) and ffmpeg"
                print(f"ok   {path.name}: {entry['samples']} samples per channel equal {column}")
    if compared == 0:
        print("FAIL: no stream was compared")
        return 1
    print(f"{compared} files compared, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
