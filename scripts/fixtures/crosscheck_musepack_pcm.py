#!/usr/bin/env python3
"""Compares the PCM that Encspect's Musepack decoder delivers with libmpcdec's and FFmpeg's, by position
(docs/formats/musepack.md, "Decoding"; docs/design/lossy-decoders.md, "The PCM cross-checks"; docs/development.md, "Musepack decoding").

    crosscheck_musepack_pcm.py [--pcmdump CMD] [--ffmpeg PATH] [--keep DIR] [--work DIR] [FILE ...]

CMD runs a built tests/Encspect.PcmDump, for example "dotnet tests/Encspect.PcmDump/bin/Release/net10.0/Encspect.PcmDump.dll" (the default is that path relative to
the repository). With no FILE the script takes every file of tests/fixtures/musepack.

For each file:

- PcmDump writes the samples of the decoded stream (floats at full scale 1.0, interleaved) with the runs of positions they cover. Positions are untrimmed, at the coded stage.
- libmpcdec decodes the same file through musepack_pcm.c (musepack_tool.py builds libmpcdec of musepack_src r495 from its pinned archive for stream versions 7 and 8, and libmpcdec 1.2.6 for
  versions 4 to 6, with floating-point arithmetic and no fused multiply-add) and writes floats; its first sample is at 481 plus the beginning silence at the coded stage, which the
  program prints.
- compare_pcm.py compares by position within the bounds of the design (per channel the RMS of the difference at most 2^-15 / sqrt(12) of full scale, its largest magnitude at
  most 2^-14); the decoder follows libmpcdec operation by operation, so the figures are 0 on every file libmpcdec decodes.
- Positions only the analyzer has are the 481 samples of the synthesis filter's delay and the beginning silence and, after the end, the frames that carry no sample libmpcdec outputs
  (they are trimmed); positions only libmpcdec has are listed and must be pinned in EXPECTED.
- FFmpeg's mpc and mpc8 decoders (a black box here, no source read) are a second reference for the stream versions 7 and 8: when they output as many samples as libmpcdec does, they are
  compared at the same positions.

Exit status 0 where every bound holds, 1 where one fails, 2 for a usage error.
"""

import argparse
import json
import pathlib
import re
import shlex
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import musepack_tool  # noqa: E402

ROOT = HERE.parents[1]
FIXTURES = ROOT / "tests" / "fixtures" / "musepack"
DEFAULT_PCMDUMP = "dotnet " + str(ROOT / "tests" / "Encspect.PcmDump" / "bin" / "Release" / "net10.0" / "Encspect.PcmDump.dll")

# The positions only libmpcdec has, by file: [first, end) of samples at the coded stage.
# - sv8-truncated.mpc: the audio packet the data cuts short holds whole frames, which libmpcdec decodes as far as the data holds them; the analyzer counts the packet's frames as lost.
EXPECTED = {
    "sv8-truncated.mpc": [[41472, 43776]],
}

# Files FFmpeg's decoder decodes differently from libmpcdec and the analyzer: none known. (FFmpeg fills the subbands of resolution -1 with a generator of its own,
# so a stream with noise-substituted subbands cannot agree sample by sample and is not compared with it; the stream's report says whether it has any.)
FFMPEG_DIFFERS = {}


def has_noise(path):
    """Whether the committed report of the file counts noise-substituted subband channels (the first "Noise-Substituted Channels" value in it)."""
    report = path.with_suffix(".report.json")
    if not report.exists():
        return False
    found = re.search(r'"name": "Noise-Substituted Channels",\s*"value": (\d+)', report.read_text(encoding="utf-8"))
    return bool(found and int(found.group(1)) > 0)

# Files where the analyzer decodes samples and libmpcdec none (or the reverse): the reason.
NOT_COMPARED = {
    "sv5-seek-tag.mpc": "libmpcdec 1.2.6's stream reader does not skip the ID3v2 tag the file starts with",
    "sv6-seek-tag.mpc": "libmpcdec 1.2.6's stream reader does not skip the ID3v2 tag the file starts with",
    "sv8-sample-count-unknown.mpc": "the header's sample count is 0 (unknown), so libmpcdec's trimmed range is empty",
    "sv7-resolution-invalid.mpc": "libmpcdec stops at the first frame, which has a resolution it rejects; the analyzer loses it, withholds the 32 frames after it and decodes the rest",
    "sv8-audio-before-header.mpc": "an audio packet before the stream header (libmpcdec decodes no sample; the analyzer reads the stream header and decodes the packets)",
    "sv8-invalid-key-in-run.mpc": "libmpcdec stops decoding at a packet key outside A to Z; the analyzer reports it and decodes the packets it can read",
}


def version_of(path):
    """The stream version family by the magic number after any ID3v2 tag: 8, 7, or 6 for versions 4 to 6, which have none."""
    data = path.read_bytes()
    start = 0
    if data[:3] == b"ID3" and len(data) >= 10:
        start = 10 + ((data[6] << 21) | (data[7] << 14) | (data[8] << 7) | data[9])
    head = data[start:start + 4]
    return 8 if head == b"MPCK" else 7 if head[:3] == b"MP+" else 6


def reference(binary, path, target):
    """(first position, channels, rate, samples) libmpcdec's program prints, and the floats it wrote; None where it decodes nothing."""
    result = subprocess.run([str(binary), str(path), str(target)], capture_output=True, text=True)
    found = re.match(r"first (\d+) channels (\d+) rate (\d+) samples (\d+)", result.stdout)
    if result.returncode != 0 or found is None or int(found.group(4)) == 0:
        return None
    return tuple(int(g) for g in found.groups())


def compare(manifest, raw, channels, first, label, extra=()):
    output = subprocess.run([sys.executable, str(HERE / "compare_pcm.py"), str(manifest), "--reference", str(raw), "--reference-format", "float32",
                             "--reference-channels", str(channels), "--reference-first-position", str(first), "--json", *extra], capture_output=True, text=True)
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
    parser.add_argument("--work", type=pathlib.Path, help="build libmpcdec in this folder")
    args = parser.parse_args(argv)
    files = args.files or sorted(FIXTURES.glob("*.mpc"))
    work = args.work or pathlib.Path(tempfile.mkdtemp(prefix="musepack-"))
    work.mkdir(parents=True, exist_ok=True)
    binaries = {8: musepack_tool.build_pcm(work / "r495"), 6: musepack_tool.build_pcm_126(work / "126")}
    binaries[7] = binaries[8]
    failed = compared = 0
    with tempfile.TemporaryDirectory() as temporary:
        base = args.keep or pathlib.Path(temporary)
        base.mkdir(parents=True, exist_ok=True)
        for number, path in enumerate(files):
            name = path.name
            dump = base / f"dump{number}"
            result = subprocess.run(shlex.split(args.pcmdump) + [str(dump), str(path)], capture_output=True)
            if result.returncode != 0:
                print(f"FAIL {name}: PcmDump exits {result.returncode}: {result.stderr.decode('utf-8', 'replace')[-300:]}")
                failed += 1
                continue
            manifest = dump / f"0000-{path.name}.pcm.json"
            streams = json.loads(manifest.read_text())["streams"] if manifest.exists() else []
            version = version_of(path)
            raw = base / f"{path.stem}.libmpcdec.f32"
            found = reference(binaries[version], path, raw)
            problems, notes = [], []
            if found is None:
                if streams and streams[0]["samples"]:
                    if name in NOT_COMPARED:
                        notes.append(f"libmpcdec decodes no sample: {NOT_COMPARED[name]}")
                    else:
                        problems.append("libmpcdec decodes no sample and the analyzer decodes some; the file is not in NOT_COMPARED with its reason")
                else:
                    notes.append("libmpcdec decodes no sample, and the analyzer decodes none")
            elif not streams:
                problems.append("the analyzer decodes no stream, libmpcdec decodes samples")
            else:
                first, channels, _, samples = found
                if streams[0]["channels"] != channels:
                    problems.append(f"the analyzer decoded {streams[0]['channels']} channels, libmpcdec {channels}")
                else:
                    outcome, bad = compare(manifest, raw, channels, first, "libmpcdec")
                    problems += bad
                    if outcome is not None:
                        compared += 1
                        pinned = EXPECTED.get(name, [])
                        if outcome["notComparedOnlyInReference"] != pinned:
                            problems.append(f"libmpcdec: positions only libmpcdec has: {outcome['notComparedOnlyInReference']}, expected {pinned}")
                    if version >= 7 and not problems:
                        second = subprocess.run([args.ffmpeg, "-v", "error", "-nostdin", "-i", str(path), "-f", "f32le", "-acodec", "pcm_f32le", "-"], capture_output=True)
                        if second.returncode != 0 or not second.stdout:
                            notes.append("ffmpeg decodes nothing")
                        else:
                            # FFmpeg drops no delay: it outputs every frame from position 0 (and stops at the header's sample count in SV8 and at the last frame's samples in SV7).
                            count = len(second.stdout) // (4 * channels)
                            start = first if count == samples else 0 if count == streams[0]["samples"] else None
                            if has_noise(path):
                                notes.append("ffmpeg fills the noise-substituted subbands with a generator of its own: not compared sample by sample")
                            elif start is None:
                                notes.append(f"ffmpeg outputs {count} samples, libmpcdec {samples}: not compared sample by sample")
                            else:
                                (base / f"{path.stem}.ffmpeg.f32").write_bytes(second.stdout)
                                # FFmpeg's decoders are not libmpcdec's: their window and sums are not the same single-precision code, so the bound is that of a 16-bit reference
                                # (2 LSB largest, 1 LSB RMS of 16 bits), and the samples at full scale, which a 16-bit decoder clips, are left out.
                                outcome, bad = compare(manifest, base / f"{path.stem}.ffmpeg.f32", channels, start, "ffmpeg", ["--max-abs", str(2 / 32768), "--rms", str(1 / 32768), "--skip-full-scale"])
                                if name in FFMPEG_DIFFERS:
                                    if not bad:
                                        problems.append(f"ffmpeg now agrees: {FFMPEG_DIFFERS[name]}")
                                    notes.append(f"ffmpeg differs: {FFMPEG_DIFFERS[name]}")
                                else:
                                    problems += bad
            note = "; ".join(notes)
            if problems:
                failed += 1
                print(f"FAIL {name}: " + "; ".join(problems))
            else:
                print(f"ok   {name}" + (f" ({note})" if note else ""))
    print(f"{compared} files compared with libmpcdec, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
