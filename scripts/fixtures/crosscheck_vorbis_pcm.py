#!/usr/bin/env python3
"""Compares the PCM that Encspect's Vorbis decoder delivers with libvorbis's and FFmpeg's, by position
(docs/formats/vorbis.md, "Decoding"; docs/design/lossy-decoders.md, "The PCM cross-checks"; docs/development.md, "Vorbis decoding").

    crosscheck_vorbis_pcm.py [--pcmdump CMD] [--ffmpeg PATH] [--keep DIR] [FILE ...]

CMD runs a built tests/Encspect.PcmDump, for example "dotnet tests/Encspect.PcmDump/bin/Release/net10.0/Encspect.PcmDump.dll" (the default is that path relative to
the repository). With no FILE the script takes every Ogg file of tests/fixtures/ogg whose first stream is Vorbis.

For each file:

- PcmDump writes the samples of the decoded stream (floats at full scale 1.0, interleaved) with the runs of positions they cover. Positions are untrimmed, at the coded
  stage: the first block returns nothing.
- libvorbis (the system's libvorbis-dev, 1.3.7, vorbis_pcm.c) is given each packet alone with granulepos -1 and e_o_s 0, as the official-decoder harness does, so that it
  trims nothing; its output is at the same stage. A file whose headers libvorbis refuses must not be decoded by the analyzer either.
- FFmpeg's native Vorbis decoder is a second reference for mono and stereo files, whose channel order it keeps (it reorders the others). It trims the end by the last
  granule position, which leaves positions only the analyzer has, and they are listed.
- compare_pcm.py compares by position within the bounds of the design (per channel the RMS of the difference at most 2^-15 / sqrt(12) of full scale, its largest magnitude at
  most 2^-14): the floats are compared as floats, nothing is rounded to 16 bits.
- Randomized variants: for each of RANDOMIZED, the headers of an encoder's file with 120 audio packets of random bytes behind valid mode and window bits (several seeds), which
  libvorbis decodes to samples of every magnitude, packets that end inside floors, residue partitions and codewords included. The samples must be as many as libvorbis's and
  within 10^-5 of the largest sample's magnitude (RMS 10^-6): relative, since random codewords make loud and unbounded spectra.
- Positions only one side has are listed: those the analyzer withheld after a gap, and those a reference conceals or never receives (a page libogg drops). A file outside
  the bound fails the run, and one with positions listed that EXPECTED does not pin fails too.

Exit status 0 where every bound holds, 1 where one fails, 2 for a usage error.
"""

import argparse
import array
import json
import math
import random
import pathlib
import shlex
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import ogg_model  # noqa: E402
import vorbis_tool  # noqa: E402
import vorbis_walk  # noqa: E402

ROOT = HERE.parents[1]
FIXTURES = ROOT / "tests" / "fixtures"
DEFAULT_PCMDUMP = "dotnet " + str(ROOT / "tests" / "Encspect.PcmDump" / "bin" / "Release" / "net10.0" / "Encspect.PcmDump.dll")

# The positions only one side has in the fixtures where they differ, by file. vorbis-cut-last-packet.ogg: the file ends inside its last page, whose whole packets the analyzer
# decodes and libogg drops with the page (the analyzer's own count, 26,816 to 39,936, is the packets of that page). vorbis-end-page-repeated.ogg: libogg reports a hole at the
# copy of the end-of-stream page and libvorbis decodes the packet after it on a stale overlap, which the analyzer withholds after a gap (the span, 1,024 samples, is that
# packet's). Pinned, so that a change in what the analyzer withholds or decodes fails here.
EXPECTED = {
    "vorbis-cut-last-packet.ogg": {"analyzer": [[26816, 39936]], "reference": []},
    "vorbis-end-page-repeated.ogg": {"analyzer": [], "reference": [[29696, 30720]]},
}

# Open differences: a fixture a reference decodes differently, by reference ("libvorbis" or "ffmpeg"), with what is known. Not an explanation and not a pass: the line prints
# "OPEN", the exit status stays 0 only for the bound problem listed here (any other problem on the file still fails), and the lead decides (docs/design/lossy-decoders.md,
# "The PCM cross-checks"). libvorbis stays the reference.
PENDING = {
    "external-ffmpeg-vorbis.ogg": {
        "ffmpeg": "FFmpeg 6.1.1's native Vorbis decoder differs from libvorbis and the analyzer on this stream, which FFmpeg's own native encoder wrote (largest difference 0.286 of "
                  "full scale, RMS 0.056). Black-box facts: channel 0 is bit-identical in all three decoders and the whole difference is in channel 1 (the stream has one coupling "
                  "step, so a stage after the inverse coupling that touches channel 1 alone, such as its floor curve, is the lead); against the encoder's input signal FFmpeg's decoder "
                  "reaches 15.1 dB SNR on this file and libvorbis 10.8 dB, while on external-ffmpeg-libvorbis.ogg both reach 33 dB; on the 14 other fixtures with libvorbis audio all "
                  "three agree to 1.5e-7. The cause is not established"
    },
}

# Hand-built streams that broke the walk or the decoder (scripts/fixtures/make_vorbis_stress_fixtures.py), decoded by both and compared with the fixtures' own bounds.
STRESS = ["vorbis-residue-book-without-dimensions.bin", "vorbis-two-mappings-different-muxes.bin"]

# A setup with a floor 0 on one channel. libvorbis's floor 0 gain is not clamped and overflows to infinity near the roots of the envelope, where the analyzer limits the exponent
# to a float's range: the the analyzer's samples must all be finite; samples whose magnitude is beyond 1000 on either side, or not finite in libvorbis's output, are set to 0 on both sides (and counted in the output), and the rest are compared within 10^-5 of their peak (at least the fixtures' bounds).
FLOOR0 = "vorbis-setup-rich.ogg"
FLOOR0_FILES = 3
FLOOR0_LIMIT = 1e3


RANDOMIZED = ["external-oggenc-q5.ogg", "external-oggenc-mono.ogg", "external-oggenc-51.ogg", "external-ffmpeg-vorbis.ogg", "external-oggenc-q10.ogg", "external-oggenc-q0.ogg"]
SEEDS = range(3)
RELATIVE_MAX = 1e-5
RELATIVE_RMS = 1e-6


def ogg_packets(data):
    packets, position, current = [], 0, b""
    while position < len(data):
        count = data[position + 26]
        lacing = data[position + 27:position + 27 + count]
        body = position + 27 + count
        for value in lacing:
            current += data[body:body + value]
            body += value
            if value < 255:
                packets.append(current)
                current = b""
        position = body
    return packets


def randomized(source, seed, target):
    """Writes the headers of source with 120 random audio packets behind valid mode and window bits."""
    packets = ogg_packets(source.read_bytes())
    ident, setup = packets[0], packets[2]
    model = vorbis_walk.Setup(setup, ident[11])
    rnd = random.Random(seed)
    audio = []
    for _ in range(120):
        mode = rnd.randrange(len(model.modes))
        value, position = mode << 1, 1 + model.mode_bits
        if model.modes[mode][0]:
            value |= rnd.randrange(2) << position
            value |= rnd.randrange(2) << (position + 1)
            position += 2
        needed = (position + 7) // 8
        body = bytearray(rnd.randrange(256) for _ in range(max(needed, rnd.choice([1, 2, 3, 5, 9, 17, 33, 60, 120, 250, 400, 700]))))
        if rnd.random() < 0.3:
            body = bytearray(0 if rnd.random() < 0.7 else b for b in body)
        head = value.to_bytes(needed, "little")
        for i in range(needed):
            mask = (1 << min(8, position - 8 * i)) - 1
            body[i] = (body[i] & ~mask & 0xFF) | (head[i] & mask)
        audio.append(bytes(body))
    everything = [ident, packets[1], setup] + audio
    pages = ogg_model.pages_for(1, everything, [-1] * len(everything), [1, 2] + [80] * 100, 0)
    target.write_bytes(b"".join(ogg_model.page(p.serial, p.sequence, p.granule, p.flags, p.lacing, p.body)[0] for p in pages))


def default_files():
    return sorted(p for p in FIXTURES.glob("ogg/*.ogg"))


def first_stream_is_vorbis(path):
    data = path.read_bytes()
    return data[:4] == b"OggS" and b"\x01vorbis" in data[:200]


def ffmpeg_reference(ffmpeg, path):
    result = subprocess.run([ffmpeg, "-v", "error", "-nostdin", "-i", str(path), "-f", "f32le", "-acodec", "pcm_f32le", "-"], capture_output=True)
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


def check_file(args, binary, base, path, kind):
    """Decodes path with the analyzer and libvorbis and compares them (kind "stress": the bounds of the fixtures; "relative": 10^-5 of the peak); returns 1 for a failure."""
    name = path.name
    dump = base / f"dump-{name}"
    subprocess.run(shlex.split(args.pcmdump) + [str(dump), str(path)], capture_output=True)
    manifest = dump / f"0000-{name}.pcm.json"
    reference = vorbis_tool.decode_pcm(binary, path, base / "check.f32")
    streams = json.loads(manifest.read_text())["streams"] if manifest.exists() else []
    if reference is None or len(streams) != 1:
        print(f"FAIL {name}: libvorbis or the analyzer does not decode the stream")
        return 1
    extra = []
    note = ""
    if kind == "floor0":
        reference_file = base / "check.f32"
        values = array.array("f")
        values.frombytes(reference_file.read_bytes())
        analyzer_file = dump / streams[0]["pcm"]
        mine = array.array("f")
        mine.frombytes(analyzer_file.read_bytes())
        if not all(math.isfinite(x) for x in mine):
            print(f"FAIL {name}: the analyzer wrote samples that are not finite")
            return 1
        excluded = 0
        for i, value in enumerate(values):
            if not math.isfinite(value) or abs(value) > FLOOR0_LIMIT or abs(mine[i]) > FLOOR0_LIMIT:
                values[i] = 0.0
                mine[i] = 0.0
                excluded += 1
        if excluded > 0.9 * len(values):
            print(f"FAIL {name}: {excluded} of {len(values)} samples are beyond {FLOOR0_LIMIT:g} or not finite in libvorbis's output, too few to compare")
            return 1
        kind = "relative"
        note = f", {excluded} of {len(values)} samples beyond {FLOOR0_LIMIT:g} or non-finite left out"
        reference_file.write_bytes(values.tobytes())
        analyzer_file.write_bytes(mine.tobytes())
    if kind == "relative":
        values = array.array("f")
        values.frombytes((base / "check.f32").read_bytes())
        peak = max(abs(v) for v in values) or 1.0
        extra = ["--max-abs", str(max(peak * RELATIVE_MAX, 2.0 ** -14)), "--rms", str(max(peak * RELATIVE_RMS, 2.0 ** -15 / math.sqrt(12.0)))]
    outcome, found = compare(manifest, (base / "check.f32").read_bytes(), reference[1]["channels"], "libvorbis", extra)
    if outcome is not None and outcome["notComparedOnlyInAnalyzer"] + outcome["notComparedOnlyInReference"]:
        found = found or [f"libvorbis: positions only one side has: {outcome}"]
    print(("FAIL " if found else "ok   ") + name + ("; " + "; ".join(found) if found else f" ({kind}{note})"))
    return 1 if found else 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("files", nargs="*", type=pathlib.Path)
    parser.add_argument("--pcmdump", default=DEFAULT_PCMDUMP)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--keep", type=pathlib.Path, help="keep the dumps and the references in this folder")
    args = parser.parse_args(argv)
    files = [p for p in (args.files or default_files()) if first_stream_is_vorbis(p)]
    if not files:
        print("no Ogg Vorbis file")
        return 2
    binary = vorbis_tool.build_pcm()
    failed = compared = 0
    opened = []
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
            work = base / f"work{number}"
            work.mkdir(exist_ok=True)
            reference = vorbis_tool.decode_pcm(binary, path, work / "libvorbis.f32")
            problems, notes = [], []
            if reference is None:
                if streams:
                    problems.append("libvorbis refuses the stream, and the analyzer decodes it")
                else:
                    notes.append("libvorbis refuses the stream, and the analyzer decodes nothing")
            elif not streams:
                problems.append("the analyzer decodes no stream, libvorbis decodes one")
            else:
                info = reference[1]
                if streams[0]["channels"] != info["channels"]:
                    problems.append(f"the analyzer decoded {streams[0]['channels']} channels, libvorbis {info['channels']}")
                else:
                    outcome, found = compare(manifest, (work / "libvorbis.f32").read_bytes(), info["channels"], "libvorbis")
                    problems += found
                    if outcome is not None:
                        compared += 1
                        only = outcome["notComparedOnlyInAnalyzer"] + outcome["notComparedOnlyInReference"]
                        pinned = EXPECTED.get(path.name, {"analyzer": [], "reference": []})
                        if outcome["notComparedOnlyInAnalyzer"] != pinned["analyzer"] or outcome["notComparedOnlyInReference"] != pinned["reference"]:
                            problems.append(f"libvorbis: positions only one side has: analyzer {outcome['notComparedOnlyInAnalyzer']}, reference {outcome['notComparedOnlyInReference']}, expected {pinned}")
                        elif only:
                            notes.append(f"libvorbis: positions only one side has: analyzer {outcome['notComparedOnlyInAnalyzer']}, reference {outcome['notComparedOnlyInReference']}")
                    if info["channels"] <= 2 and not problems:
                        second = ffmpeg_reference(args.ffmpeg, path)
                        if second is None:
                            notes.append("ffmpeg decodes nothing")
                        else:
                            outcome, found = compare(manifest, second, info["channels"], "ffmpeg")
                            problems += found
                            if outcome is not None and outcome["notComparedOnlyInAnalyzer"] + outcome["notComparedOnlyInReference"]:
                                notes.append(f"ffmpeg: positions only one side has: analyzer {outcome['notComparedOnlyInAnalyzer']}, reference {outcome['notComparedOnlyInReference']}")
            note = "; ".join(notes)
            for name, reason in PENDING.get(path.name, {}).items():
                listed = [p for p in problems if p.startswith(name + ": outside the bound")]
                if listed:
                    problems = [p for p in problems if p not in listed]
                    opened.append(path.name)
                    note = (note + "; " if note else "") + f"{name} OPEN: {reason}"
            if problems:
                failed += 1
                print(f"FAIL {path.name}: " + "; ".join(problems))
            else:
                print(("OPEN " if path.name in opened else "ok   ") + f"{path.name}" + (f" ({note})" if note else ""))
        for source in RANDOMIZED:
            for seed in SEEDS:
                name = f"random-{pathlib.Path(source).stem}-{seed}.ogg"
                path = base / name
                randomized(FIXTURES / "ogg" / source, seed, path)
                dump = base / f"dump-{name}"
                subprocess.run(shlex.split(args.pcmdump) + [str(dump), str(path)], capture_output=True)
                manifest = dump / f"0000-{name}.pcm.json"
                reference = vorbis_tool.decode_pcm(binary, path, base / "random.f32")
                streams = json.loads(manifest.read_text())["streams"] if manifest.exists() else []
                if reference is None or len(streams) != 1:
                    print(f"FAIL {name}: libvorbis or the analyzer does not decode the stream")
                    failed += 1
                    continue
                values = array.array("f")
                values.frombytes((base / "random.f32").read_bytes())
                peak = max(abs(v) for v in values) or 1.0
                outcome, found = compare(manifest, (base / "random.f32").read_bytes(), reference[1]["channels"], "libvorbis",
                                         ["--max-abs", str(peak * RELATIVE_MAX), "--rms", str(peak * RELATIVE_RMS)])
                compared += 1
                if outcome is None or outcome["notComparedOnlyInAnalyzer"] + outcome["notComparedOnlyInReference"]:
                    found = found or [f"libvorbis: positions only one side has: {outcome}"]
                if found:
                    failed += 1
                    print(f"FAIL {name}: " + "; ".join(found))
                else:
                    print(f"ok   {name} (peak {peak:.3g})")
        for name in STRESS:
            path = base / (pathlib.Path(name).stem + ".ogg")
            path.write_bytes((FIXTURES / "fuzz" / name).read_bytes())
            failed += check_file(args, binary, base, path, "stress")
            compared += 1
        for seed in range(FLOOR0_FILES):
            path = base / f"random-floor0-{seed}.ogg"
            randomized(FIXTURES / "ogg" / FLOOR0, seed, path)
            failed += check_file(args, binary, base, path, "floor0")
            compared += 1
    print(f"{compared} files compared with libvorbis, {failed} failed, {len(opened)} open")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
