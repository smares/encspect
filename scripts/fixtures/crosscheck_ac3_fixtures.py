#!/usr/bin/env python3
"""Checks the AC-3 and E-AC-3 fixtures' manifests (tests/fixtures/ac3) against independent tools.

For every fixture, its manifest's "crosscheck" says what to confirm:

- "ffprobe": ffprobe (ffmpeg 6.1) must report the first stream's codec, sample rate and channels
  ("ffprobe_channels" where ffmpeg takes them from elsewhere, such as an MP4 box or the dependent
  substreams it mixes in), a channel layout that feeds the manifest's speakers (channel_layouts.py;
  ffmpeg places AC-3's only surround pair at the sides, the analyzer at the back as the WAVE channel
  mask does, so a lone pair counts as one position), and exactly the packets the manifest's frames
  make: ffmpeg's parser starts a packet at each AC-3 frame and each independent E-AC-3 frame, and
  joins dependent frames to the one before. For raw files, ffprobe's bitrate must be the first
  packet's, unless "ffprobe_bitrate" is false (ffmpeg gives none for frames it cannot decode).
- "decode": ffmpeg with CRC checking must decode each packet alone without an error, unless the
  packet holds a frame the manifest lists as one decoders reject ("damaged": cut or failing a CRC,
  or a dependent frame without its independent one), which it must reject, as it rejects the frames
  of independent substreams other than 0, which ffmpeg 6.1 does not decode. MP4 files are decoded
  whole, without an error.
- "liba52": liba52 (a52_blocks.c, built here) must end each audio block of every frame in
  "block_ends" at the manifest's bit, and must not reject those frames.
- "skip_insertion" (E-AC-3): with the analyzer's report (--analyzer), every frame is rewritten with
  skip fields of one byte of 0xA5 inserted where the report puts each block's mantissas, the
  skip field syntax switched on and the frame size and CRC updated; ffmpeg must decode the rewritten
  file to exactly the same samples as the original, which it only does when every block's mantissas
  start where the report says. As a negative control, the skip field of each block of the first
  frame is moved a bit earlier and a bit later in turn, and each move must change the samples;
  a block whose mantissas take no bits cannot show a wrong position, so a fixture of such blocks
  turns the control off ("skip_control": false) with its reason. A block's mantissas end where the
  next block's start; the last block's end is not checked this way.
- "skip": the checks that are not run on the fixture, each with its reason.

The analyzer's agreement with the manifests is checked by the tests (Ac3FixtureTests). Needs ffmpeg,
liba52-0.7.4-dev and a C compiler. Exits non-zero when anything disagrees.

    python3 scripts/fixtures/crosscheck_ac3_fixtures.py [--analyzer path/to/encspect]
"""

import json
import pathlib
import subprocess
import sys
import tempfile

import ac3_model as m
import channel_layouts

ROOT = pathlib.Path(__file__).resolve().parent
FIXTURES = ROOT.parents[1] / "tests" / "fixtures" / "ac3"
CHECKS = ("ffprobe", "ffprobe_bitrate", "decode", "liba52", "skip_insertion", "skip_control")


def ffprobe(path):
    out = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a", "-show_entries",
                          "stream=codec_name,sample_rate,channels,channel_layout,bit_rate:packet=pos,size", "-of", "json", str(path)],
                         capture_output=True, text=True, check=True).stdout
    return json.loads(out)


def packets_of(data, frames):
    out = []
    for offset, length in frames:
        h = m.header(data, offset)
        if out and h and h["eac3"] and h["dependent"]:
            out[-1][1] += length
        else:
            out.append([offset, length])
    return out


def normalized(speakers):
    speakers = list(speakers)
    if "side left" in speakers and "back left" not in speakers:
        speakers = ["back left" if s == "side left" else "back right" if s == "side right" else s for s in speakers]
    return sorted(speakers)


def check_ffprobe(path, data, manifest, problems):
    probe = ffprobe(path)
    streams = probe.get("streams", [])
    if not streams:
        problems.append("ffprobe finds no audio stream")
        return
    actual, expected = streams[0], manifest["streams"][0]
    codec = "eac3" if expected["codec"] == "E-AC-3" else "ac3"
    channels = manifest["crosscheck"].get("ffprobe_channels", expected["channels"])
    if actual.get("codec_name") != codec or int(actual.get("sample_rate", 0)) != expected["sample_rate"] or int(actual.get("channels", 0)) != channels:
        problems.append(f"ffprobe: {actual.get('codec_name')}, {actual.get('sample_rate')} Hz, {actual.get('channels')} channels; expected {codec}, "
                        f"{expected['sample_rate']} Hz, {channels}")
    known = channel_layouts.FFMPEG.get(actual.get("channel_layout"))
    if "ffprobe_channels" not in manifest["crosscheck"] and (known is None or normalized(known) != normalized(expected["channel_layout"])):
        problems.append(f"ffprobe's channel layout {actual.get('channel_layout')}, expected {expected['channel_layout']}")
    packets = [[int(p["pos"]), int(p["size"])] for p in probe.get("packets", [])]
    expected_packets = packets_of(data, manifest["frames"]) if manifest["format"] == "AC-3" else packets_of_samples(data, manifest["frames"])
    if packets != expected_packets:
        problems.append(f"ffprobe lists {len(packets)} packets, the manifest's frames make {len(expected_packets)}")
    if manifest["format"] == "AC-3" and expected_packets and manifest["crosscheck"].get("ffprobe_bitrate", True):
        offset, length = expected_packets[0]
        h = m.header(data, offset)
        rate = length * 8 * h["rate"] // (h["blocks"] * 256) if h["eac3"] else [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512,
                                                                                 576, 640][h["bitrate_code"]] * 1000 >> max(0, h["bsid"] - 8)
        if int(actual.get("bit_rate", 0)) != rate:
            problems.append(f"ffprobe's bitrate {actual.get('bit_rate')}, the first packet's {rate}")


def packets_of_samples(data, frames):
    """MP4 samples: ETSI TS 102 366 F.2 puts six blocks of every substream into one sample; the fixtures' samples start where ffmpeg's packets do."""
    return packets_of(data, frames)


def check_decode(path, data, manifest, problems, work):
    damaged = set(manifest["crosscheck"].get("damaged", []))
    if manifest["format"] != "AC-3":
        result = subprocess.run(["ffmpeg", "-v", "error", "-err_detect", "crccheck", "-i", str(path), "-f", "null", "-"], capture_output=True, text=True)
        if result.stderr.strip():
            problems.append(f"ffmpeg reports errors: {result.stderr.strip().splitlines()[0]}")
        return
    kind = "eac3" if manifest["streams"][0]["codec"] == "E-AC-3" else "ac3"
    for offset, length in packets_of(data, manifest["frames"]):
        piece = work / "packet.bin"
        piece.write_bytes(data[offset:offset + length])
        result = subprocess.run(["ffmpeg", "-v", "error", "-err_detect", "crccheck", "-f", kind, "-i", str(piece), "-f", "null", "-"],
                                capture_output=True, text=True)
        # ffmpeg 6.1 decodes independent substream 0 only (eac3dec.c), so it rejects the others.
        bad = any(f[0] in damaged for f in manifest["frames"] if offset <= f[0] < offset + length) or m.header(data, offset)["substream"] != 0
        failed = bool(result.stderr.strip())
        if failed != bad:
            problems.append(f"ffmpeg {'rejects' if failed else 'decodes'} the packet at {offset}, which the manifest calls {'damaged' if bad else 'intact'}")


def check_liba52(tool, path, manifest, problems):
    lines = subprocess.run([str(tool), str(path)], capture_output=True, text=True, check=True).stdout.splitlines()
    found = {line.split()[0]: line.split()[2:] for line in lines}
    for offset, ends in manifest.get("block_ends", {}).items():
        if found.get(offset) != [str(e) for e in ends]:
            problems.append(f"liba52 ends the blocks of the frame at {offset} at {found.get(offset)}, the manifest at {ends}")


def bits_of(data):
    return "".join(f"{b:08b}" for b in data)


def bytes_of(bits):
    return bytes(int(bits[i:i + 8], 2) for i in range(0, len(bits), 8))


def report_frames(analyzer, path):
    doc = json.loads(subprocess.run([analyzer, "--json", str(path)], capture_output=True, text=True, check=True).stdout)
    frames = []

    def walk(node):
        for child in node.get("children", []):
            if child.get("kind") == "Frame":
                frames.append(child)
            else:
                walk(child)

    walk(doc[0]["root"])
    return frames


def find(node, name):
    if node["name"] == name:
        yield node
    for child in node.get("children", []):
        yield from find(child, name)


def insert_skips(data, frames, moved=None):
    """Every frame with a one-byte skip field before each block's mantissas; moved = (frame, block,
    bits) shifts one of them."""
    rewritten = bytearray()
    for index, frame in enumerate(frames):
        start, length = frame["offset"], frame["length"]
        bits = bits_of(data[start:start + length])

        def bit(node):
            return (node["offset"] - start) * 8 + node.get("bitOffset", 0)

        flag = bit(next(find(frame, "HasSkipFields")))
        mantissas = [bit(n) for n in find(frame, "Mantissas")]
        if moved is not None and moved[0] == index:
            mantissas[moved[1]] += moved[2]
        out = bits[:flag] + "1"
        previous = flag + 1
        for position in mantissas:
            out += bits[previous:position] + "1" + f"{1:09b}" + "10100101"
            previous = position
        out += bits[previous:len(bits) - 17]
        out += "0" * (-(len(out) + 17) % 16)
        out += bits[len(bits) - 17]
        words = (len(out) + 16) // 16
        out = out[:21] + f"{words - 1:011b}" + out[32:]
        body = bytes_of(out)
        rewritten += body + m.crc16(body[2:]).to_bytes(2, "big")
    return bytes(rewritten)


def pcm(source):
    return subprocess.run(["ffmpeg", "-v", "error", "-err_detect", "crccheck", "-f", "eac3", "-i", str(source), "-f", "s32le", "-"],
                          capture_output=True).stdout


def check_skip_insertion(analyzer, path, problems, work, control):
    data = path.read_bytes()
    frames = report_frames(analyzer, path)
    target = work / "rewritten.eac3"
    target.write_bytes(insert_skips(data, frames))
    original = pcm(path)
    if original != pcm(target):
        problems.append("ffmpeg decodes the file with skip fields at the report's mantissa positions to other samples")
    if not control:
        return
    blocks = sum(1 for _ in find(frames[0], "Mantissas"))
    for block in range(blocks):
        for shift in (-1, 1):
            target.write_bytes(insert_skips(data, frames, (0, block, shift)))
            if pcm(target) == original:
                problems.append(f"moving the skip field of block {block} of the first frame by {shift:+d} bit leaves the samples unchanged")


def main():
    analyzer = sys.argv[sys.argv.index("--analyzer") + 1] if "--analyzer" in sys.argv else None
    failures = checked = 0
    with tempfile.TemporaryDirectory() as directory:
        work = pathlib.Path(directory)
        tool = work / "a52_blocks"
        subprocess.run(["cc", "-O2", "-o", str(tool), str(ROOT / "a52_blocks.c"), "-la52", "-lm"], check=True)
        for manifest_path in sorted(FIXTURES.glob("*.expected.json")):
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            stem = manifest_path.name[:-len(".expected.json")]
            path = next(p for p in FIXTURES.iterdir() if p.name.startswith(stem + ".") and not p.name.endswith(".json"))
            data = path.read_bytes()
            check = manifest["crosscheck"]
            problems = []
            for name in CHECKS:
                if check.get(name) is False and name not in check.get("skip", {}):
                    problems.append(f"the {name} check is off without a reason")
            if check.get("ffprobe"):
                check_ffprobe(path, data, manifest, problems)
            if check.get("decode"):
                check_decode(path, data, manifest, problems, work)
            if check.get("liba52"):
                check_liba52(tool, path, manifest, problems)
            if check.get("skip_insertion"):
                if analyzer is None:
                    print(f"{path.name}: the skip insertion check needs --analyzer; not run")
                else:
                    check_skip_insertion(analyzer, path, problems, work, check.get("skip_control", True))
            checked += 1
            for problem in problems:
                print(f"{path.name}: {problem}")
            failures += bool(problems)
    print(f"{checked} fixtures checked, {failures} disagree.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
