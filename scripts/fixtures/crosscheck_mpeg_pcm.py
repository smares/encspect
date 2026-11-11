#!/usr/bin/env python3
"""Compares the PCM that Encspect's MPEG audio decoder delivers with minimp3's and FFmpeg's, by position
(docs/formats/mpeg-audio.md, "Decoding"; docs/design/lossy-decoders.md, "The PCM cross-checks"; docs/development.md, "MPEG audio decoding").

    crosscheck_mpeg_pcm.py [--pcmdump CMD] [--ffmpeg PATH] [--keep DIR] [FILE ...]

CMD runs a built tests/Encspect.PcmDump, for example "dotnet tests/Encspect.PcmDump/bin/Release/net10.0/Encspect.PcmDump.dll" (the default is that path
relative to the repository). With no FILE the script takes every MPEG audio fixture of tests/fixtures/mpeg that its reference can decode as a whole (see EXPLAINED).

For each file:

- PcmDump writes the samples of the decoded stream (floats at full scale 1.0, interleaved) with the runs of positions they cover. Positions are untrimmed
  and count from the first audio frame: a Xing, Info or VBRI header frame holds no audio and is not a position.
- minimp3 (CC0, third_party/minimp3, built by minimp3_tool.py with -ffp-contract=off) decodes the file to floats with the same full scale. Its first frame is dropped
  where it is a VBR header frame, which minimp3 decodes as a frame of silence. A frame it skips (no main data, or damage) is zeros in the reference and its positions
  are left out of the comparison and listed.
- FFmpeg decodes the file to 32-bit floats as a second reference, with `-flags2 +skip_manual` so that it applies none of the encoder delay a LAME tag states.
  Its demuxer reads the VBR header frame itself, so the positions agree. A file FFmpeg decodes with another frame count than minimp3's is listed.
- compare_pcm.py compares by position within the bounds of the design (per channel the RMS of the difference at most 2^-15 / sqrt(12) of full scale, its
  largest magnitude at most 2^-14): the floats are compared as floats, nothing is rounded to 16 bits.
- Positions only one side has are listed: those the analyzer withheld after a gap or for missing main data, and those a reference conceals. A file whose
  reference differs for a reason listed in EXPLAINED is compared with that reference's frames left out; a file outside the bound for any other reason fails the run.

Exit status 0 where every bound holds, 1 where one fails, 2 for a usage error.
"""

import argparse
import json
import pathlib
import shlex
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import minimp3_tool  # noqa: E402

ROOT = HERE.parents[1]
FIXTURES = ROOT / "tests" / "fixtures"
DEFAULT_PCMDUMP = "dotnet " + str(ROOT / "tests" / "Encspect.PcmDump" / "bin" / "Release" / "net10.0" / "Encspect.PcmDump.dll")

# Fixtures a reference decodes differently, by reference ("minimp3" or "ffmpeg"), with what is known. An entry is a pending item for the lead, not an explanation: the
# bound is not widened for it (docs/design/lossy-decoders.md, "The PCM cross-checks").
EXPLAINED = {
    "external-xing-mpeg2.mp3": {
        "ffmpeg": "the references disagree: in the granules of frames 61 to 64 of this tompg file (MPEG-2 joint stereo, intensity and mid/side stereo, a transient after near silence) "
                  "FFmpeg 8's mp3float and mp3 decoders (which agree with each other), mpg123 1.32 and minimp3 (which the analyzer follows) differ pairwise by up to 0.024 of full scale; "
                  "the analyzer keeps minimp3, the reference it follows operation by operation"
    },
    "side-info-mono.mp3": {
        "minimp3": "the private bits of this hand-built mono stream are set, and minimp3 reads them as scfsi bits that its decoder uses; the analyzer reads the side info as the specification lays it out"
    },
    "side-info-long-blocks.mp3": {
        "minimp3": "the private bits of this hand-built stereo stream are 111, which minimp3 reads as scfsi bits of the first granule (as in side-info-mono.mp3): it takes the scalefactors of "
                   "the second channel's first granule from the previous values and reads 6 bits of them instead of 41, so its Huffman data start 35 bits earlier than the specification places them"
    },
    "reservoir-across-gap.mp3": {
        "ffmpeg": "FFmpeg conceals the frames after the gap with audio built from the missing main data; the analyzer withholds them (a gap is never concealed audio)"
    },
}

MAGICS = (b"Xing", b"Info", b"VBRI")


# Fixtures that make mixed blocks, which no encoder writes: the short granules of these files with mixed_block_flag set (ISO/IEC 11172-3 2.4.2.7), one for each MPEG-1
# sample rate row of the band tables, written next to the dumps. minimp3 and the analyzer must agree on them.
MIXED_SOURCES = ["external-helix-r11-mono.mp3", "external-blade-stereo.mp3", "external-fhg-music-cbr.mp3", "external-fhg-mono-cbr.mp3"]


def mixed_variant(source, directory, binary):
    """Writes the source with mixed_block_flag set on every short granule of an MPEG-1 Layer III file; returns the path, or None where the file has none."""
    data = bytearray(source.read_bytes())
    frames = minimp3_tool.decode_pcm(binary, source, directory / "mixed-probe.f32")
    changed = 0
    for frame in frames:
        base = frame["offset"]
        header = data[base:base + 4]
        if frame["layer"] != 3 or (header[1] >> 3) & 3 != 3:
            continue
        channels = 1 if (header[3] >> 6) == 3 else 2
        start = (4 + (0 if header[1] & 1 else 2)) * 8 + 9 + (5 if channels == 1 else 3) + 4 * channels
        for index in range(2 * channels):
            at = start + index * 59 + 33
            bit = lambda n: (data[base + (at + n) // 8] >> (7 - (at + n) % 8)) & 1
            if bit(0) and (bit(1) << 1 | bit(2)) == 2:
                data[base + (at + 3) // 8] |= 0x80 >> ((at + 3) % 8)
                changed += 1
    if not changed:
        return None
    target = directory / ("mixed-" + source.name)
    target.write_bytes(data)
    return target


def default_files():
    skip = ("external-mp3pro", "ape-", "id3")
    return [p for p in sorted(FIXTURES.glob("mpeg/**/*.mp3")) + sorted(FIXTURES.glob("mpeg/**/*.mp2")) + sorted(FIXTURES.glob("mpeg/**/*.mp1")) if not p.name.startswith(skip)]


def is_header_frame(data, offset, length):
    frame = data[offset:offset + min(length, 200)]
    return any(magic in frame for magic in MAGICS)


def minimp3_reference(binary, path, work):
    """(raw bytes of the floats at the analyzer's positions, channels, skipped position ranges), or None where minimp3 decodes nothing."""
    target = work / "minimp3.f32"
    frames = minimp3_tool.decode_pcm(binary, path, target)
    decoded = [f for f in frames if f["samples"]]
    if not decoded:
        return None
    data = path.read_bytes()
    raw = target.read_bytes()
    channels = decoded[0]["channels"]
    out = bytearray()
    skipped = []
    position = 0
    cursor = 0
    for number, frame in enumerate(frames):
        count = frame["samples"] * frame["channels"] * 4
        if number == 0 and frame["samples"] and is_header_frame(data, frame["offset"], frame["bytes"]):
            cursor += count
            continue
        if frame["samples"] and frame["channels"] != channels:
            return None
        if frame["samples"] == 0:
            # A frame minimp3 skips is zeros here, and its positions are left out of the comparison.
            nominal = 384 if frame["layer"] == 1 else 576 if frame["layer"] == 3 and frame["hz"] < 32000 else 1152
            if frame["channels"] == 0:
                continue
            out += bytes(nominal * channels * 4)
            skipped.append((position, position + nominal))
            position += nominal
            continue
        out += raw[cursor:cursor + count]
        cursor += count
        position += frame["samples"]
    return bytes(out), channels, skipped, decoded[0]["layer"]


def report_codes(path):
    """The finding codes of the fixture's committed report; empty for a file without one (the built mixed-block variants)."""
    reports = list(path.parent.glob(path.stem + ".report.json"))
    if not reports:
        return set()
    codes = set()

    def walk(node):
        if isinstance(node, dict):
            if "code" in node and "severity" in node:
                codes.add(node["code"])
            for value in node.values():
                walk(value)
        elif isinstance(node, list):
            for value in node:
                walk(value)

    walk(json.loads(reports[0].read_text()))
    return codes


def position_problems(path, outcome, layer):
    """Positions that only the reference has are the frames the analyzer does not decode (a truncated, cut or header frame) or withholds after a gap: whole frames,
    each optionally followed by the withheld 1,056 samples (Layer III) or 480 (Layers I and II). A file without a finding has none."""
    if path.name.startswith("mixed-"):
        return []
    frame = 384 if layer == 1 else 1152
    withheld = 480 if layer in (1, 2) else 1056
    codes = report_codes(path) - {"mpeg.music-crc-not-verified", "mpeg.vbr-byte-count-mismatch"}
    problems = []
    for first, end in outcome["notComparedOnlyInReference"]:
        length = end - first
        if not codes:
            problems.append(f"minimp3 decodes {length} samples at {first} that the analyzer does not output, and the file has no finding")
        elif length % frame not in (0, withheld % frame):
            problems.append(f"minimp3 decodes {length} samples at {first} that the analyzer does not output: not whole frames, with or without {withheld} withheld")
    return problems


def ffmpeg_reference(ffmpeg, path, layer):
    # The float decoder of the layer: FFmpeg's default decoders of Layers I and II are fixed-point and round to 16 bits.
    decoder = {1: "mp1float", 2: "mp2float", 3: "mp3float"}.get(layer)
    result = subprocess.run([ffmpeg, "-v", "error", "-nostdin", "-flags2", "+skip_manual", *(["-c:a", decoder] if decoder else []), "-i", str(path),
                             "-f", "f32le", "-acodec", "pcm_f32le", "-"], capture_output=True)
    if result.returncode != 0 or not result.stdout:
        return None
    return result.stdout


def compare(manifest, reference, channels, label, extra=()):
    raw = manifest.parent / f"{label}.f32"
    raw.write_bytes(reference)
    output = subprocess.run([sys.executable, str(HERE / "compare_pcm.py"), str(manifest), "--reference", str(raw), "--reference-format", "float32",
                             "--reference-channels", str(channels), "--json", *extra], capture_output=True, text=True)
    if output.returncode == 2:
        return None, [f"{label}: {output.stderr.strip()}"]
    result = json.loads(output.stdout)
    problems = []
    if output.returncode != 0:
        worst = max(result["channels"], key=lambda c: c["max"])
        problems.append(f"{label}: outside the bound (largest {worst['max']:.3g}, RMS {worst['rms']:.3g})")
    return result, problems


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("files", nargs="*", type=pathlib.Path)
    parser.add_argument("--pcmdump", default=DEFAULT_PCMDUMP)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--keep", type=pathlib.Path, help="keep the dumps and the references in this folder")
    args = parser.parse_args(argv)
    files = args.files or default_files()
    if not files:
        print("no MPEG audio file")
        return 2
    binary = minimp3_tool.build_pcm()
    failed = compared = 0
    mixed_directory = pathlib.Path(tempfile.mkdtemp(prefix="mixed-"))
    if not args.files:
        files = files + [m for name in MIXED_SOURCES if (m := mixed_variant(FIXTURES / "mpeg" / name, mixed_directory, binary))]
    with tempfile.TemporaryDirectory() as temporary:
        base = args.keep or pathlib.Path(temporary)
        base.mkdir(parents=True, exist_ok=True)
        for number, path in enumerate(files):
            dump = base / f"dump{number}"
            result = subprocess.run(shlex.split(args.pcmdump) + [str(dump), str(path)], capture_output=True)
            if result.returncode != 0:
                print(f"FAIL {path.name}: PcmDump exits {result.returncode}: {result.stderr.decode('utf-8', 'replace')[-300:]}")
                failed += 1
                continue
            manifest = dump / f"0000-{path.name}.pcm.json"
            streams = json.loads(manifest.read_text())["streams"] if manifest.exists() else []
            if len(streams) != 1:
                print(f"skip {path.name}: {len(streams)} decoded streams (one is compared)")
                continue
            work = base / f"work{number}"
            work.mkdir(exist_ok=True)
            problems = []
            notes = []
            reference = minimp3_reference(binary, path, work)
            layer = reference[3] if reference is not None else None
            if reference is None:
                notes.append("minimp3 decodes no frames or changes its channels")
            else:
                raw, channels, skipped, _ = reference
                if streams[0]["channels"] != channels:
                    problems.append(f"the analyzer decoded {streams[0]['channels']} channels, minimp3 {channels}")
                else:
                    outcome, found = compare(manifest, raw, channels, "minimp3", [a for first, end in skipped for a in ("--exclude", f"{first}:{end}")])
                    problems += found
                    if outcome is not None:
                        compared += 1
                        only = outcome["notComparedOnlyInAnalyzer"] + outcome["notComparedOnlyInReference"]
                        if only:
                            notes.append(f"minimp3: positions only one side has: analyzer {outcome['notComparedOnlyInAnalyzer']}, reference {outcome['notComparedOnlyInReference']}")
                        problems += position_problems(path, outcome, layer)
            # FFmpeg and minimp3 do not treat mixed blocks alike (they differ by up to 1.5 of full scale on these files), so only minimp3 is the reference for them.
            second = None if path.name.startswith("mixed-") else ffmpeg_reference(args.ffmpeg, path, layer)
            if path.name.startswith("mixed-"):
                notes.append("mixed blocks: minimp3 alone is the reference")
            elif second is None:
                notes.append("ffmpeg decodes nothing")
            else:
                outcome, found = compare(manifest, second, streams[0]["channels"], "ffmpeg")
                problems += found
                if outcome is not None:
                    only = outcome["notComparedOnlyInAnalyzer"] + outcome["notComparedOnlyInReference"]
                    if only:
                        notes.append(f"ffmpeg: positions only one side has: analyzer {outcome['notComparedOnlyInAnalyzer']}, reference {outcome['notComparedOnlyInReference']}")
            note = "; ".join(notes)
            for reference, reason in EXPLAINED.get(path.name, {}).items():
                listed = [p for p in problems if p.startswith(reference + ":")]
                if listed:
                    problems = [p for p in problems if p not in listed]
                    note = (note + "; " if note else "") + f"{reference} listed: {reason}"
            if problems:
                failed += 1
                print(f"FAIL {path.name}: " + "; ".join(problems))
            else:
                print(f"ok   {path.name}" + (f" ({note})" if note else ""))
    print(f"{compared} files compared with minimp3, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
