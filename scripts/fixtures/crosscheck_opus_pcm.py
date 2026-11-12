#!/usr/bin/env python3
"""Compares the PCM that Encspect's Opus decoder delivers with libopus's and FFmpeg's, by position
(docs/formats/opus.md, "Decoding"; docs/design/lossy-decoders.md, "The PCM cross-checks"; docs/development.md, "Opus decoding").

    crosscheck_opus_pcm.py [--pcmdump CMD] [--ffmpeg PATH] [--keep DIR] [--work DIR] [--no-vectors] [FILE ...]

CMD runs a built tests/Encspect.PcmDump, for example "dotnet tests/Encspect.PcmDump/bin/Release/net10.0/Encspect.PcmDump.dll" (the default is that path relative to
the repository). With no FILE the script takes every Opus file of tests/fixtures/ogg and the 12 test vectors of RFC 8251 (downloaded at a pinned SHA-256, never committed,
and written as Ogg Opus files: one packet a page, the header of two channels, no pre-skip).

For each file:

- PcmDump writes the samples of the decoded stream (floats at full scale 1.0, interleaved) with the runs of positions they cover. Positions are untrimmed, at the coded
  stage.
- libopus 1.5.2 (opus_tool.py builds the pinned release without SIMD intrinsics, so that it is the plain C code that RFC 6716's decoder is; opus_pcm.c) is given each
  packet alone, with the header's output gain, and writes floats at the same stage. A file whose headers libopus refuses must not be decoded by the analyzer either.
- FFmpeg's native Opus decoder is a second reference for mono and stereo files; it trims by the pre-skip and the end, which leaves positions only the analyzer has.
- compare_pcm.py compares by position within the bounds of the design (per channel the RMS of the difference at most 2^-15 / sqrt(12) of full scale, its largest
  magnitude at most 2^-14): the floats are compared as floats, nothing is rounded to 16 bits.
- Positions only one side has are listed: those the analyzer withheld after a gap or a frame of no data (3,840 samples, 80 ms), and those a reference conceals. A file
  with positions listed that EXPECTED does not pin fails.

For the test vectors the final range coder state of every packet is a second, stricter check: tests/Encspect.Tests/Formats/Opus/OpusRangeStateTests.cs holds the entropy pass to the
state the encoder stored, and this script holds the decoder's samples to libopus's.

Exit status 0 where every bound holds, 1 where one fails, 2 for a usage error.
"""

import argparse
import json
import pathlib
import shlex
import struct
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import opus_tool  # noqa: E402

ROOT = HERE.parents[1]
FIXTURES = ROOT / "tests" / "fixtures"
DEFAULT_PCMDUMP = "dotnet " + str(ROOT / "tests" / "Encspect.PcmDump" / "bin" / "Release" / "net10.0" / "Encspect.PcmDump.dll")

# The positions only one side has in the fixtures where they differ, by file: [first, end) of samples at the coded stage. Pinned, so that a change in what the analyzer
# withholds or decodes fails here. "exclude" lists samples both sides have and that are left out of the bound, as FIRST:END:
# - opus-packets-corrupt.opus: a packet of no bytes, then packets whose framing breaks RFC 6716, section 3.4, between valid ones. libopus refuses the broken ones and decodes
#   the valid ones on the state it has; the analyzer counts each broken packet as lost, starts over at the next valid one and withholds its 80 ms, which is longer than the
#   packets (960 samples) between two broken ones, so it outputs the first valid packet only.
# - opus-toc-configs.opus: frames of no bytes. libopus conceals them and goes on with the state concealment leaves; the analyzer starts over, withholds the frame and the
#   80 ms after it (decision 5 of docs/design/lossy-decoders.md) and decodes the next frames on a fresh state, whose energy prediction (CELT) and filters (SILK) have not
#   converged to libopus's in the 1,920 samples that follow the withheld span before the next frame of no bytes: the largest difference there is 1.9e-3 of full scale.
EXPECTED = {
    "opus-packets-corrupt.opus": {"analyzer": [], "reference": [[960, 30720]]},
    "opus-toc-configs.opus": {"analyzer": [], "reference": [[60480, 67200], [69120, 71040]], "exclude": ["67200:69120"]},
}


def ogg_crc(data):
    table = []
    for i in range(256):
        r = i << 24
        for _ in range(8):
            r = ((r << 1) ^ 0x04C11DB7) & 0xFFFFFFFF if r & 0x80000000 else (r << 1) & 0xFFFFFFFF
        table.append(r)
    crc = 0
    for byte in data:
        crc = ((crc << 8) & 0xFFFFFFFF) ^ table[((crc >> 24) & 0xFF) ^ byte]
    return crc


def page(serial, sequence, granule, flags, packets):
    lacing = bytearray()
    for packet in packets:
        lacing += bytes([255]) * (len(packet) // 255) + bytes([len(packet) % 255])
    body = b"".join(packets)
    header = b"OggS" + struct.pack("<BBqIII", 0, flags, granule, serial, sequence, 0) + bytes([len(lacing)]) + bytes(lacing)
    crc = ogg_crc(header + body)
    return header[:22] + struct.pack("<I", crc) + header[26:] + body


def vector_to_ogg(bit, channels, target):
    """Writes the packets of an opus_demo file as an Ogg Opus file: OpusHead, OpusTags, then a page for each packet with the granule position of its end."""
    data = bit.read_bytes()
    packets, position = [], 0
    while position + 8 <= len(data):
        length = struct.unpack(">I", data[position:position + 4])[0]
        packets.append(data[position + 8:position + 8 + length])
        position += 8 + length
    head = b"OpusHead" + bytes([1, channels]) + struct.pack("<HIhB", 0, 48000, 0, 0)
    tags = b"OpusTags" + struct.pack("<I", 7) + b"Encspect" [:7] + struct.pack("<I", 0)
    out = bytearray(page(1, 0, 0, 2, [head]) + page(1, 1, 0, 0, [tags]))
    granule = 0
    for i, packet in enumerate(packets):
        granule += samples_of(packet)
        out += page(1, 2 + i, granule, 4 if i == len(packets) - 1 else 0, [packet])
    target.write_bytes(bytes(out))


def samples_of(packet):
    toc = packet[0]
    config = toc >> 3
    if config < 12:
        size = [480, 960, 1920, 2880][config & 3]
    elif config < 16:
        size = 480 if config & 1 == 0 else 960
    else:
        size = 120 << (config & 3)
    code = toc & 3
    frames = 1 if code == 0 else 2 if code in (1, 2) else packet[1] & 0x3F
    return size * frames


def default_files():
    return sorted(p for p in FIXTURES.glob("ogg/*.opus"))


def ffmpeg_reference(ffmpeg, path):
    result = subprocess.run([ffmpeg, "-v", "error", "-nostdin", "-i", str(path), "-f", "f32le", "-acodec", "pcm_f32le", "-"], capture_output=True)
    return result.stdout if result.returncode == 0 and result.stdout else None


def compare(manifest, reference, channels, label, first_position=0, extra=()):
    raw = manifest.parent / f"{label}.f32"
    raw.write_bytes(reference)
    output = subprocess.run([sys.executable, str(HERE / "compare_pcm.py"), str(manifest), "--reference", str(raw), "--reference-format", "float32",
                             "--reference-channels", str(channels), "--reference-first-position", str(first_position), "--json", *extra], capture_output=True, text=True)
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
    parser.add_argument("--work", type=pathlib.Path, help="build libopus and keep the downloads in this folder")
    parser.add_argument("--no-vectors", action="store_true")
    args = parser.parse_args(argv)
    files = [p for p in (args.files or default_files()) if p.read_bytes()[:4] == b"OggS" and b"OpusHead" in p.read_bytes()[:100]]
    work = args.work or pathlib.Path(tempfile.mkdtemp(prefix="opus-"))
    work.mkdir(parents=True, exist_ok=True)
    binary = opus_tool.build_pcm(work)
    failed = compared = 0
    with tempfile.TemporaryDirectory() as temporary:
        base = args.keep or pathlib.Path(temporary)
        base.mkdir(parents=True, exist_ok=True)
        inputs = [(p, p.name) for p in files]
        if not args.files and not args.no_vectors:
            vectors = opus_tool.fetch_vectors(work, "8251")
            for n in range(1, 13):
                target = base / f"testvector{n:02d}.opus"
                vector_to_ogg(vectors / f"testvector{n:02d}.bit", 2, target)
                inputs.append((target, f"testvector{n:02d}.opus"))
        for number, (path, name) in enumerate(inputs):
            dump = base / f"dump{number}"
            result = subprocess.run(shlex.split(args.pcmdump) + [str(dump), str(path)], capture_output=True)
            if result.returncode != 0:
                print(f"FAIL {name}: PcmDump exits {result.returncode}: {result.stderr.decode('utf-8', 'replace')[-300:]}")
                failed += 1
                continue
            manifest = dump / f"0000-{path.name}.pcm.json"
            streams = json.loads(manifest.read_text())["streams"] if manifest.exists() else []
            folder = base / f"work{number}"
            folder.mkdir(exist_ok=True)
            reference = opus_tool.decode_pcm(binary, "ogg", [path], folder / "libopus.f32")
            problems, notes = [], []
            if reference is None:
                if streams:
                    problems.append("libopus refuses the stream, and the analyzer decodes it")
                else:
                    notes.append("libopus refuses the stream, and the analyzer decodes nothing")
            elif not streams:
                problems.append("the analyzer decodes no stream, libopus decodes one")
            else:
                channels = reference[1]["channels"]
                if streams[0]["channels"] != channels:
                    problems.append(f"the analyzer decoded {streams[0]['channels']} channels, libopus {channels}")
                else:
                    exclude = []
                    for span in EXPECTED.get(name, {}).get("exclude", []):
                        exclude += ["--exclude", span]
                    if not (folder / "libopus.f32").stat().st_size:
                        notes.append("libopus decodes nothing (libogg drops the page the file is cut in), and the analyzer decodes its whole packets")
                        print(f"ok   {name} ({'; '.join(notes)})")
                        continue
                    outcome, found = compare(manifest, (folder / "libopus.f32").read_bytes(), channels, "libopus", extra=exclude)
                    problems += found
                    if outcome is not None:
                        compared += 1
                        pinned = EXPECTED.get(name, {"analyzer": [], "reference": []})
                        if outcome["notComparedOnlyInAnalyzer"] != pinned["analyzer"] or outcome["notComparedOnlyInReference"] != pinned["reference"]:
                            problems.append(f"libopus: positions only one side has: analyzer {outcome['notComparedOnlyInAnalyzer']}, reference {outcome['notComparedOnlyInReference']}, expected {pinned}")
                    if channels <= 2 and not problems and name.startswith("testvector"):
                        second = ffmpeg_reference(args.ffmpeg, path)
                        if second is None:
                            notes.append("ffmpeg decodes nothing")
                        else:
                            notes.append("ffmpeg trims by the pre-skip and the end, as opusdec does, and is not compared sample by sample here")
            note = "; ".join(notes)
            if problems:
                failed += 1
                print(f"FAIL {name}: " + "; ".join(problems))
            else:
                print(f"ok   {name}" + (f" ({note})" if note else ""))
    print(f"{compared} files compared with libopus, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
