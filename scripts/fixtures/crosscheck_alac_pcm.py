#!/usr/bin/env python3
"""Compares the PCM that Encspect's ALAC decoder delivers with Apple's decoder, bit for bit (docs/design/lossy-decoders.md, "The PCM cross-checks";
docs/development.md, "Comparing decoded PCM").

For every undamaged fixture of tests/fixtures/alac (a manifest with no ALAC finding and a complete cookie), tests/Encspect.PcmDump writes the samples the analyzer
delivers, and `alacconvert`, Apple's reference implementation built unpatched from its pinned source by alac_tool.py (a black box here), decodes the same frames; the
two are compared by position with compare_pcm.py at a bound of 0 LSB. Streams of up to two channels are decoded by FFmpeg too, as a second column. Samples of 20 bits are
not compared: alacconvert reads and writes 16, 24 and 32 bits only.

    python3 scripts/fixtures/crosscheck_alac_pcm.py [--pcmdump CMD] [name ...]

CMD is the command that runs the built PcmDump, by default `dotnet tests/Encspect.PcmDump/bin/Release/net10.0/Encspect.PcmDump.dll`. Exit status 1 where a sample differs.
"""

import argparse
import contextlib
import io
import json
import pathlib
import shlex
import subprocess
import sys
import tempfile

import alac_tool
import compare_pcm
import crosscheck_alac_fixtures as cc
import tool_versions

ROOT = pathlib.Path(__file__).resolve().parents[2]
DEFAULT_PCMDUMP = "dotnet " + str(ROOT / "tests" / "Encspect.PcmDump" / "bin" / "Release" / "net10.0" / "Encspect.PcmDump.dll")
FORMATS = {2: "int16", 3: "int24", 4: "int32"}


def interleave(planes, width):
    """Interleaved little-endian samples from one bytes object per channel."""
    count = len(planes[0]) // width
    out = bytearray()
    for i in range(count):
        for plane in planes:
            out += plane[i * width:(i + 1) * width]
    return bytes(out)


def compare(manifest_path, reference_file, fmt, channels, extra=()):
    """compare_pcm.py's verdict at 0 LSB, as (passes, the report's text)."""
    arguments = [str(manifest_path), "--reference", str(reference_file), "--reference-format", fmt, "--reference-channels", str(channels), "--max-lsb", "0", "--rms-lsb", "0", *extra]
    text = io.StringIO()
    with contextlib.redirect_stdout(text):
        status = compare_pcm.main(arguments)
    return status == 0, text.getvalue()


def wanted_decoders(manifest):
    """The decoders the manifest says read the stream: the files with no input (Apple's afconvert) are read by both."""
    return manifest["input"]["decoders"] if manifest.get("input") else ["ffmpeg", "alacconvert"]


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--pcmdump", default=DEFAULT_PCMDUMP)
    parser.add_argument("names", nargs="*")
    args = parser.parse_args()
    tool_versions.require("ffmpeg")
    chosen = set(args.names)
    failures = 0
    compared = 0
    with tempfile.TemporaryDirectory() as folder:
        work = pathlib.Path(folder)
        program = alac_tool.build(pathlib.Path(tempfile.gettempdir()) / "alac-build")
        candidates = []
        for manifest_path in sorted(cc.DIRECTORY.glob("*.expected.json")):
            name = manifest_path.name[:-len(".expected.json")]
            if chosen and name not in chosen:
                continue
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            if cc.is_damaged(manifest) or manifest["stream_infos"] is None or not manifest["cookie"] or not manifest["cookie"]["complete"]:
                cc.skip(name, "the PCM comparison", "the manifest lists a finding of the parser, so Apple's decoder's answer is its own choice")
            elif manifest["cookie"]["bit_depth"] == 20:
                cc.skip(name, "the PCM comparison", "alacconvert reads and writes no 20-bit samples")
            elif "alacconvert" not in wanted_decoders(manifest):
                cc.skip(name, "the PCM comparison", "the manifest lists no alacconvert among the decoders that read this stream")
            else:
                candidates.append((name, manifest_path, manifest, cc.find(manifest_path)))
        dump = work / "dump"
        dump.mkdir()
        result = subprocess.run([*shlex.split(args.pcmdump), str(dump), *[str(p) for _, _, _, p in candidates]], capture_output=True, text=True)
        if result.returncode != 0:
            print(f"FAIL PcmDump exited with {result.returncode}: {result.stderr[-300:]}")
            sys.exit(1)
        for number, (name, _, manifest, path) in enumerate(candidates):
            cookie = manifest["cookie"]
            width = {16: 2, 24: 3, 32: 4}[cookie["bit_depth"]]
            pcm_json = dump / f"{number:04d}-{path.name}.pcm.json"
            problems = []
            if not pcm_json.exists():
                problems.append("PcmDump wrote no stream")
            else:
                planes, error = cc.alacconvert_planes(program, path.read_bytes(), manifest, path, work)
                if planes is None:
                    problems.append(error)
                else:
                    reference = work / "alacconvert.raw"
                    reference.write_bytes(interleave(planes, width))
                    passes, text = compare(pcm_json, reference, FORMATS[width], cookie["channels"])
                    if not passes:
                        problems.append("alacconvert: " + text)
                    if "ffmpeg" not in wanted_decoders(manifest):
                        cc.skip(name, "the PCM comparison with FFmpeg", "the manifest lists no ffmpeg among the decoders that read this stream")
                    elif cookie["channels"] <= 2:
                        planes, error = cc.ffmpeg_planes(path, cookie["channels"], width, work)
                        if planes is None:
                            problems.append("ffmpeg: " + error)
                        else:
                            reference.write_bytes(interleave(planes, width))
                            passes, text = compare(pcm_json, reference, FORMATS[width], cookie["channels"])
                            if not passes:
                                problems.append("ffmpeg: " + text)
                    else:
                        cc.skip(name, "the PCM comparison with FFmpeg", "FFmpeg orders the channels of more than two by its own layouts")
                compared += 1
            if problems:
                failures += 1
                print(f"FAIL {name}")
                for problem in problems:
                    print("    " + problem.replace("\n", "\n    "))
    for (check, reason), names in sorted(cc.SKIPS.items()):
        print(f"skipped {check} for {len(names)} fixtures: {reason}")
    print(f"{compared} fixtures compared, {failures} with a difference.")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
