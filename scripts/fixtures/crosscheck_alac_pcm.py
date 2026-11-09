#!/usr/bin/env python3
"""Compares the PCM that Encspect's ALAC decoder delivers with Apple's decoder, bit for bit (docs/design/lossy-decoders.md, "The PCM cross-checks";
docs/development.md, "Comparing decoded PCM").

For every fixture of tests/fixtures/alac with a complete cookie, tests/Encspect.PcmDump writes the samples the analyzer delivers, and `alacconvert`, Apple's reference
implementation built unpatched from its pinned source by alac_tool.py (a black box here), decodes the same frames; the two are compared by position with compare_pcm.py at a
bound of 0 LSB. The fixtures that break the format are compared too, except those in SKIPS, each with its reason (the cookies Apple's decoder crashes on or Encspect
refuses, and a frame whose error path writes what Apple's buffers held). compare_pcm.py passes where the analyzer leaves a gap, so the positions only Apple's decoder has
are counted: they may not exceed the samples the parser counts as lost (the manifest's lost_samples) plus those of the frames the decoder refuses (GAPS), so a frame that
Apple decodes and the analyzer drops is a failure. Streams of up to two channels without a finding are decoded by FFmpeg too, as a second column. Samples of 20 bits are
compared with FFmpeg's only: alacconvert decodes them but writes channels * 2.5 bytes a frame of 3-byte samples, so its file is unusable.

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

SKIPS = {
    "built-channels-zero": "the cookie states 0 channels; Apple's decoder is given no channels",
    "built-frame-length-zero": "the cookie states a frame length of 0, so Apple's decoder allocates no buffers and overflows them",
    "built-frame-length-too-large": "Encspect refuses a frame length above 2^20 (decode.not-possible)",
    "built-partial-frame-too-long": "an element states more samples than Apple's buffers hold",
    "built-compatible-version": "the cookie states the compatible version 1, which Apple's decoder refuses",
    "built-sample-rate-zero": "Encspect refuses a sample rate of 0 (decode.not-possible); Apple's decoder ignores the rate",
    "built-tuning-kb-zero": "the maximum Golomb parameter 0 leaves the residual code undefined; no compressed frame is decoded",
    "built-residual-run-too-long": "Apple's error path writes 4,096 samples of what its buffers held",
    "built-bit-depth-12": "a bit depth of 12 is no width the comparison reads; Apple's decoder converts no sample of it",
}

# The samples of the frames the decoder refuses (decode.frame-invalid) that Apple's decoder writes, by fixture.
GAPS = {
    "built-channels-missing": 4096,
    "built-element-samples-differ": 4096,
}


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
    arguments = [str(manifest_path), "--reference", str(reference_file), "--reference-format", fmt, "--reference-channels", str(channels), "--max-lsb", "0", "--rms-lsb", "0", "--json", *extra]
    text = io.StringIO()
    with contextlib.redirect_stdout(text):
        status = compare_pcm.main(arguments)
    try:
        result = json.loads(text.getvalue())
    except json.JSONDecodeError:
        return False, text.getvalue(), {}
    return status == 0, text.getvalue() if status else "", result


def gap(result):
    """The samples only the reference has: positions the analyzer did not deliver."""
    return sum(b - a for a, b in result.get("notComparedOnlyInReference", []))


def compare_20_bit(manifest_path, reference_planes, channels):
    """The analyzer's 20-bit samples (3 bytes each) against FFmpeg's 32-bit ones shifted right by 12, all of them; the problem, or None."""
    entry = json.loads(pathlib.Path(manifest_path).read_text(encoding="utf-8"))["streams"][0]
    raw = (pathlib.Path(manifest_path).parent / entry["pcm"]).read_bytes()
    ours = [int.from_bytes(raw[i:i + 3], "little", signed=True) for i in range(0, len(raw), 3)]
    theirs = [[int.from_bytes(p[i:i + 4], "little", signed=True) >> 12 for i in range(0, len(p), 4)] for p in reference_planes]
    if len(raw) % 3 or [ours[c::channels] for c in range(channels)] != theirs:
        return f"the 20-bit samples differ from FFmpeg's ({len(ours)} against {sum(len(t) for t in theirs)} samples)"
    return None


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
            if manifest["stream_infos"] is None or not manifest["cookie"] or not manifest["cookie"]["complete"]:
                cc.skip(name, "the PCM comparison", "the manifest lists no complete cookie, so there is nothing to decode")
            elif name in SKIPS:
                cc.skip(name, "the PCM comparison", SKIPS[name])
            elif manifest["cookie"]["bit_depth"] == 20:
                candidates.append((name, manifest_path, manifest, cc.find(manifest_path)))
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
            is_damaged = cc.is_damaged(manifest)
            pcm_json = dump / f"{number:04d}-{path.name}.pcm.json"
            problems = []
            if not pcm_json.exists():
                problems.append("PcmDump wrote no stream")
            elif cookie["bit_depth"] == 20:
                planes, error = cc.ffmpeg_planes(path, cookie["channels"], 4, work)
                if planes is None:
                    problems.append("ffmpeg: " + error)
                elif (problem := compare_20_bit(pcm_json, planes, cookie["channels"])) is not None:
                    problems.append("ffmpeg: " + problem)
                cc.skip(name, "the PCM comparison with alacconvert", "alacconvert writes channels * 2.5 bytes a frame of 3-byte samples for 20 bits, so its file is unusable")
                compared += 1
            else:
                width = {16: 2, 24: 3, 32: 4}[cookie["bit_depth"]]
                planes, error = cc.alacconvert_planes(program, path.read_bytes(), manifest, path, work)
                if planes is None:
                    problems.append(error)
                else:
                    reference = work / "alacconvert.raw"
                    reference.write_bytes(interleave(planes, width))
                    passes, text, result = compare(pcm_json, reference, FORMATS[width], cookie["channels"])
                    allowed = (manifest["stream_infos"][0] or {}).get("lost_samples", 0) + GAPS.get(name, 0)
                    if not passes:
                        problems.append("alacconvert: " + text)
                    elif gap(result) > allowed:
                        problems.append(f"alacconvert: Apple's decoder writes {gap(result)} samples that the analyzer does not deliver, of which only {allowed} are lost or refused frames")
                    if is_damaged:
                        cc.skip(name, "the PCM comparison with FFmpeg", "the manifest lists a finding of the parser, so FFmpeg's answer is its own choice")
                    elif "ffmpeg" not in wanted_decoders(manifest):
                        cc.skip(name, "the PCM comparison with FFmpeg", "the manifest lists no ffmpeg among the decoders that read this stream")
                    elif cookie["channels"] <= 2:
                        planes, error = cc.ffmpeg_planes(path, cookie["channels"], width, work)
                        if planes is None:
                            problems.append("ffmpeg: " + error)
                        else:
                            reference.write_bytes(interleave(planes, width))
                            passes, text, result = compare(pcm_json, reference, FORMATS[width], cookie["channels"])
                            if not passes or gap(result) > 0:
                                problems.append("ffmpeg: " + (text or f"{gap(result)} samples not delivered"))
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
