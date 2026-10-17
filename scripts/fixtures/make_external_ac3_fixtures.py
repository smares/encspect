#!/usr/bin/env python3
"""Writes the encoder-made AC-3 and E-AC-3 fixtures into tests/fixtures/ac3 (external-*): ffmpeg 6.1's
ac3, ac3_fixed and eac3 encoders (mono, stereo, 3/2 + LFE, 44.1 and 32 kHz, Annex D's alternate bit
stream syntax, raw and in MP4) and Aften (git 2010-01-05, Ubuntu's aften package) with and without
block switching. Each encodes half a second of a deterministic signal written here: tones with
short noise bursts and, for block switching, clicks after quiet. ffmpeg's eac3 encoder writes no
dependent substreams (it encodes 7.1 input as 5.0), so the substream fixtures are hand-built
(make_ac3_fixtures.py).

The manifests hold what make_ac3_fixtures.py's do, worked out from the files (ac3_model.py) and,
where ffmpeg muxed MP4, from ffprobe's packets; for AC-3, "block_ends" holds where liba52 ends each
audio block (a52_blocks.c), so the tests hold the analyzer's bit allocation to liba52's. The encoder,
its version and the command are recorded too.

    python3 scripts/fixtures/make_external_ac3_fixtures.py --manifests-only

rewrites the manifests from the committed files without the encoders, keeping the parts only the
tools give. Needs ffmpeg 6.1, aften, liba52-0.7.4-dev and a C compiler otherwise.

The derived-* fixtures rewrite encoder-made frames where the analyzer's report puts their fields
(--analyzer path/to/encspect), for what the encoders do not write but needs real mantissas to be
checked: delta bit allocation in the coupling channel and both channels of ffmpeg's 2/0 AC-3, in block 5 and
in block 0, whose five later blocks reuse it (liba52 confirms every block's end), and spectral extension with delta bit allocation in ffmpeg's
mono E-AC-3 (the skip field insertion check confirms every block's start). Where a block's bit
allocation changes, its mantissas are cut or filled with zero bits to the length the analyzer
gives; the cross-checks then test that length against liba52 and ffmpeg.
"""

import json
import math
import pathlib
import shutil
import struct
import subprocess
import sys
import tempfile
import wave

import ac3_model as m
import crosscheck_ac3_fixtures as cc

ROOT = pathlib.Path(__file__).resolve().parent
OUT = ROOT.parents[1] / "tests" / "fixtures" / "ac3"
# Provenance: the tool versions (Ubuntu 24.04) that wrote the committed files this script produced. The cross-check jobs run Ubuntu 26.04 and
# assert its versions (tool_versions.py); the committed files and goldens stay as written, so these strings stay as they are.
FFMPEG_VERSION = "ffmpeg version 6.1.1-3ubuntu5"
AFTEN_VERSION = "aften 0.0.8+git20100105-0ubuntu4"

# name, extension, tool, input (rate, channels, signal), arguments, extra manifest findings, fields
FIXTURES = [
    ("external-ffmpeg-mono-96k", ".ac3", "ffmpeg", (48000, 1, "tones"), ["-c:a", "ac3", "-b:a", "96k"], [], {}),
    ("external-ffmpeg-stereo-192k", ".ac3", "ffmpeg", (48000, 2, "tones"), ["-c:a", "ac3", "-b:a", "192k"], [], {}),
    ("external-ffmpeg-5.1-448k", ".ac3", "ffmpeg", (48000, 6, "tones"), ["-c:a", "ac3", "-b:a", "448k"], [], {}),
    ("external-ffmpeg-stereo-44k-64k", ".ac3", "ffmpeg", (44100, 2, "tones"), ["-c:a", "ac3", "-b:a", "64k"], [], {}),
    ("external-ffmpeg-stereo-32k-320k", ".ac3", "ffmpeg", (32000, 2, "tones"), ["-c:a", "ac3", "-b:a", "320k"], [], {}),
    ("external-ffmpeg-fixed-stereo-128k", ".ac3", "ffmpeg", (48000, 2, "tones"), ["-c:a", "ac3_fixed", "-b:a", "128k"], [], {}),
    ("external-ffmpeg-annex-d-5.1", ".ac3", "ffmpeg", (48000, 6, "tones"),
     ["-c:a", "ac3", "-b:a", "384k", "-dmix_mode", "2", "-ltrt_cmixlev", "0.595", "-loro_surmixlev", "0.5", "-dsurex_mode", "2", "-ad_conv_type", "1",
      "-room_type", "2", "-mixing_level", "95", "-dialnorm", "-24"], [],
     {"AC-3/Frame/Bit Stream Information/Bit Stream ID": "6 (AC-3 with the alternate bit stream syntax of Annex D)",
      "AC-3/Frame/Bit Stream Information/Extended Information 1/Preferred Downmix": "2 (Lo/Ro downmix preferred)"}),
    ("external-ffmpeg-eac3-mono-48k", ".eac3", "ffmpeg", (48000, 1, "tones"), ["-c:a", "eac3", "-b:a", "48k"], [], {}),
    ("external-ffmpeg-eac3-stereo-96k", ".eac3", "ffmpeg", (48000, 2, "tones"), ["-c:a", "eac3", "-b:a", "96k"], [], {}),
    ("external-ffmpeg-eac3-5.1-256k", ".eac3", "ffmpeg", (48000, 6, "tones"), ["-c:a", "eac3", "-b:a", "256k"], [], {}),
    ("external-aften-stereo-192k-blocksw", ".ac3", "aften", (48000, 2, "clicks"), ["-b", "192", "-s", "1"], [], {}),
    ("external-aften-5.1-448k", ".ac3", "aften", (48000, 6, "tones"), ["-b", "448"], [], {}),
    ("external-ffmpeg-mp4-5.1-448k", ".mp4", "ffmpeg", (48000, 6, "tones"), ["-c:a", "ac3", "-b:a", "448k"], [("mp4.moov-after-mdat", "not optimized for streaming")], {}),
    ("external-ffmpeg-mp4-eac3-stereo-96k", ".mp4", "ffmpeg", (48000, 2, "tones"), ["-c:a", "eac3", "-b:a", "96k"], [("mp4.moov-after-mdat", "not optimized for streaming")], {}),
]


# name, source fixture, rewrite, description
DERIVED = [
    ("derived-ffmpeg-stereo-192k-delta", "external-ffmpeg-stereo-192k.ac3", "ac3-delta",
     "ffmpeg's 2/0 AC-3 with delta bit allocation in block 5: two segments for the coupling channel, three for each channel."),
    ("derived-ffmpeg-stereo-192k-delta-block0", "external-ffmpeg-stereo-192k.ac3", "ac3-delta-block0",
     "ffmpeg's 2/0 AC-3 with delta bit allocation in block 0, which the five blocks after it reuse: two segments for the coupling channel, three for each channel."),
    ("derived-ffmpeg-eac3-mono-48k-spx-delta", "external-ffmpeg-eac3-mono-48k.eac3", "eac3-spx-delta",
     "ffmpeg's mono E-AC-3 with spectral extension from sub-band 4 to 9 and delta bit allocation in block 4."),
]


DERIVED_FIELDS = {
    "ac3-delta": {"AC-3/Frame/Audio Block[5]/Delta Bit Allocation/Delta Bit Allocation Segments (coupling)/Offset": "31 bands",
                  "AC-3/Frame/Audio Block[5]/Delta Bit Allocation/Delta Bit Allocation Segments (left)/Offset": "3 bands"},
    "ac3-delta-block0": {"AC-3/Frame/Audio Block[0]/Delta Bit Allocation/Delta Bit Allocation Segments (coupling)/Offset": "31 bands",
                         "AC-3/Frame/Audio Block[0]/Delta Bit Allocation/Delta Bit Allocation Segments (left)/Offset": "3 bands"},
    "eac3-spx-delta": {"AC-3/Frame/Audio Block/Spectral Extension Begin Code": "2 (sub-band 4)",
                       "AC-3/Frame/Audio Block[4]/Delta Bit Allocation/Delta Bit Allocation Segments (center)/Offset": "5 bands"},
}


def segment(offset, length, ba):
    return f"{offset:05b}{length:04b}{ba:03b}"


# Deltas of +12 to +24 dB (Table 5.17) raise the masking curve over most bands, so the blocks need
# fewer mantissa bits. Coupling bands are counted from band 0 (A/52 7.2.2.6), so its segments
# start at band 31, inside the coupling range. Every segment ends before band 49: liba52 refuses
# one that reaches band 49 (parse_deltba checks j + deltlen >= 50).
CPL_SEGMENTS = "001" + segment(31, 15, 7) + segment(0, 3, 5)


def channel_segments(ch):
    return "010" + segment(3 + ch, 15, 7) + segment(0, 15, 6) + segment(0, 15, 5)


def start_bit(node, base):
    return (node["offset"] - base) * 8 + node.get("bitOffset", 0)


def blocks_of(frame):
    return [c for c in frame["children"] if c["name"] == "Audio Block"]


def first(node, name):
    return next(cc.find(node, name))


def rewrite_ac3(data, frames, lengths, number=5):
    """Block `number` of every frame gets delta bit allocation, which stays in force for the blocks after it (A/52 5.4.3.47:
    a block that sends none reuses the last, A/52 5.4.3.47 to 5.4.3.57). lengths, once known, give per frame the mantissa bits of
    each block from `number` on, or None for the unchanged length; the blocks before `number` stay as they are."""
    out = bytearray()
    for index, frame in enumerate(frames):
        base, size = frame["offset"], frame["length"]
        bits = cc.bits_of(data[base:base + size])
        blocks = blocks_of(frame)
        flag = start_bit(first(blocks[number], "HasDeltaBitAllocation"), base)
        delta = "1" + "01" * 3 + CPL_SEGMENTS + channel_segments(0) + channel_segments(1)
        body = bits[:flag] + delta
        position = flag + 1
        for at in range(number, len(blocks)):
            mantissas = first(blocks[at], "Mantissas")
            start, length = start_bit(mantissas, base), mantissas["value"]
            keep = lengths[index].get(at, length) if lengths else length
            body += bits[position:start] + bits[start:start + min(keep, length)] + "0" * max(0, keep - length)
            position = start + length
        # The next nominal bitrate (192 to 224 kbps) leaves room for the new fields.
        code = int(bits[34:40], 2) + 2
        body = body[:34] + f"{code:06b}" + body[40:]
        end = m.WORDS[int(bits[32:34], 2)][code] * 16 - 18
        if len(body) > end:
            raise SystemExit(f"Frame at {base}: the rewritten blocks take {len(body)} bits, more than {end}.")
        body += "0" * (end - len(body)) + bits[len(bits) - 18:]
        frame_bytes = m.fix_crc1(cc.bytes_of(body))
        out += m.fix_crc2(frame_bytes)
    return bytes(out)


def rewrite_eac3(data, frames, lengths):
    """Spectral extension in every block (sub-bands 4 to 9, where ffmpeg's bandwidth code 0 ends
    the channel, so no exponent changes) and delta bit allocation in block 4."""
    out = bytearray()
    for index, frame in enumerate(frames):
        base, size = frame["offset"], frame["length"]
        bits = cc.bits_of(data[base:base + size])
        edits = []  # (start, end, replacement)
        audio_frame = first(frame, "Audio Frame")
        dba = first(audio_frame, "HasDeltaBitAllocation")
        edits.append((start_bit(dba, base), start_bit(dba, base) + 1, "1"))
        blocks = blocks_of(frame)
        last_end = None
        for number, block in enumerate(blocks):
            if number == 0:
                flag = start_bit(first(block, "IsSpectralExtensionInUse"), base)
                # spxstrtf 1, spxbegf 2, spxendf 3, default band structure; coordinates: blend,
                # master, and 4 bands of exponent and mantissa.
                spx = "1" + "01" + "010" + "011" + "0" + "00000" + "00" + "000000" * 4
                edits.append((flag, flag + 1, spx))
            else:
                flag = start_bit(first(block, "HasSpectralExtensionStrategy"), base)
                edits.append((flag + 1, flag + 1, "0"))
            for node in cc.find(block, "Bandwidth Code (center)"):
                at = start_bit(node, base)
                edits.append((at, at + 6, ""))
            mantissas = first(block, "Mantissas")
            start, length = start_bit(mantissas, base), mantissas["value"]
            delta = "1" + "01" + "010" + segment(5, 4, 7) + segment(10, 6, 6) + segment(4, 3, 5) if number == 4 else "0"
            edits.append((start, start, delta))
            if number == 4 and lengths:
                keep = lengths[index]
                edits.append((start + min(keep, length), start + length, "0" * max(0, keep - length)))
            last_end = start + length
        body = ""
        previous = 0
        for at, until, text in sorted(edits, key=lambda e: (e[0], e[1])):
            body += bits[previous:at] + text
            previous = until
        # The rest up to auxdatae, then fill to whole words, encinfo and the CRC.
        body += bits[previous:last_end] + bits[last_end:len(bits) - 18] + bits[len(bits) - 18]
        body += "0" * (-(len(body) + 17) % 16)
        body += bits[len(bits) - 17]
        words = (len(body) + 16) // 16
        body = body[:21] + f"{words - 1:011b}" + body[32:]
        payload = cc.bytes_of(body)
        out += payload + m.crc16(payload[2:]).to_bytes(2, "big")
    return bytes(out)


def derive(analyzer, kind, source, target):
    """Rewrites source into target in passes: the first gives the analyzer the new fields, whose bit allocation then gives the
    changed block's mantissa length for the next. Where delta bit allocation stays in force for later blocks (ac3-delta-block0),
    each pass fixes one more block, since a block's start follows from the length of the one before; it ends when no length changes."""
    data = source.read_bytes()
    frames = cc.report_frames(analyzer, source)
    if kind == "eac3-spx-delta":
        target.write_bytes(rewrite_eac3(data, frames, None))
        lengths = [first(blocks_of(f)[4], "Mantissas")["value"] for f in cc.report_frames(analyzer, target)]
        target.write_bytes(rewrite_eac3(data, frames, lengths))
        return
    number = 5 if kind == "ac3-delta" else 0
    target.write_bytes(rewrite_ac3(data, frames, None, number))
    lengths = None
    for _ in range(8):
        report = cc.report_frames(analyzer, target)
        found = [{b: m_["value"] for b, block in enumerate(blocks_of(f)) if b >= number for m_ in list(cc.find(block, "Mantissas"))[:1]} for f in report]
        if found == lengths:
            return
        lengths = found
        target.write_bytes(rewrite_ac3(data, frames, lengths, number))
    raise SystemExit("The block lengths did not settle.")


def signal(rate, channels, kind, path):
    """Half a second of 16-bit PCM: per channel a tone with noise bursts, or quiet tones with clicks of 2 ms."""
    count = rate // 2
    frames = bytearray()
    seed = 12345
    for i in range(count):
        t = i / rate
        for ch in range(channels):
            seed = (seed * 1103515245 + 12345) & 0x7FFFFFFF
            noise = (seed / 0x7FFFFFFF) * 2 - 1
            if kind == "clicks":
                value = 0.05 * math.sin(2 * math.pi * (440 + 110 * ch) * t) + (0.9 if (i + ch * 3000) % 12000 < 96 else 0)
            else:
                burst = 0.3 if (i + ch * 1777) % 9000 < 400 else 0
                value = 0.35 * math.sin(2 * math.pi * (330 + 55 * ch) * t) + burst * noise
            frames += struct.pack("<h", int(max(-1, min(1, value)) * 32000))
    with wave.open(str(path), "wb") as w:
        w.setnchannels(channels)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(bytes(frames))


def liba52_block_ends(tool, path):
    ends = {}
    for line in subprocess.run([tool, str(path)], capture_output=True, text=True, check=True).stdout.splitlines():
        parts = line.split()
        if len(parts) == 8:
            ends[parts[0]] = [int(p) for p in parts[2:]]
    return ends


def ffprobe_packets(path):
    result = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a", "-show_entries", "packet=pos,size", "-of", "json", str(path)],
                            capture_output=True, text=True, check=True)
    return [[int(p["pos"]), int(p["size"])] for p in json.loads(result.stdout)["packets"]]


def streams_of(data):
    frames = m.walk(data)
    first = frames[0][1]
    samples = sum(h["blocks"] * 256 for _, h in frames if not h["dependent"] and h["substream"] == 0)
    codec = "E-AC-3" if first["eac3"] else "AC-3"
    vbr = len({h["length"] for _, h in frames}) > 1 if first["eac3"] else len({h["bitrate_code"] for _, h in frames}) > 1
    layout = m.speakers(first["acmod"], first["lfeon"])
    return frames, [{"codec": codec, "sample_rate": first["rate"], "channels": len(layout), "channel_mode": m.CODING_MODES[first["acmod"]],
                     "channel_layout": layout, "samples": samples, "is_vbr": vbr}]


def manifest(name, extension, tool, settings, arguments, findings, fields, data, previous, block_ends, packets):
    if extension == ".mp4":
        frames = packets if packets is not None else previous["frames"]
        sample = b""
        stream = previous["streams"] if previous else None
        if stream is None:
            raise SystemExit("MP4 manifests need the encoder run once.")
        # The channel mode is the arrangement of the channels the encoder was given; a manifest kept from an earlier run takes the current wording.
        stream = [dict(s, channel_mode=m.CODING_MODES[{1: 1, 2: 2, 6: 7}[settings[1]]]) for s in stream]
        rows = frames
    else:
        rows_with, stream = streams_of(data)
        rows = [[offset, h["length"]] for offset, h in rows_with]
    result = {
        "description": f"Encoded by {tool} from {settings[1]} channels at {settings[0]} Hz ({settings[2]}).",
        "encoder": AFTEN_VERSION if tool == "aften" else FFMPEG_VERSION,
        "command": [tool] + arguments,
        "length": len(data),
        "format": "MP4" if extension == ".mp4" else "AC-3",
        "frames": rows,
        "gaps": [],
        "streams": stream,
        "fields": fields,
        "findings": [list(f) for f in findings],
        "crosscheck": {"ffprobe": True, "decode": True, **({"liba52": True} if extension == ".ac3" else {}), **({"skip_insertion": True} if extension == ".eac3" else {})},
    }
    if block_ends is not None:
        result["block_ends"] = block_ends
    elif previous and "block_ends" in previous:
        result["block_ends"] = previous["block_ends"]
    return result


def main():
    only_manifests = "--manifests-only" in sys.argv
    OUT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as work:
        work = pathlib.Path(work)
        tool_path = None
        if not only_manifests:
            missing = [t for t in ("ffmpeg", "ffprobe", "aften", "cc") if shutil.which(t) is None]
            if missing:
                raise SystemExit(f"Missing {', '.join(missing)}; install ffmpeg, aften, liba52-0.7.4-dev and a C compiler, or pass --manifests-only.")
            tool_path = work / "a52_blocks"
            subprocess.run(["cc", "-O2", "-o", str(tool_path), str(ROOT / "a52_blocks.c"), "-la52", "-lm"], check=True)
        for name, extension, tool, settings, arguments, findings, fields in FIXTURES:
            target = OUT / (name + extension)
            manifest_path = OUT / (name + ".expected.json")
            previous = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else None
            block_ends = None
            packets = None
            if not only_manifests:
                source = work / f"{name}.wav"
                signal(*settings, source)
                if tool == "aften":
                    subprocess.run(["aften"] + arguments + [str(source), str(target)], check=True, capture_output=True)
                else:
                    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(source)] + arguments + ["-fflags", "+bitexact", "-flags:a", "+bitexact", str(target)],
                                   check=True)
                if extension == ".ac3":
                    block_ends = liba52_block_ends(tool_path, target)
                if extension == ".mp4":
                    packets = ffprobe_packets(target)
                    probe = json.loads(subprocess.run(["ffprobe", "-v", "error", "-show_streams", "-of", "json", str(target)], capture_output=True, text=True,
                                                      check=True).stdout)["streams"][0]
                    eac3 = probe["codec_name"] == "eac3"
                    acmod = {1: 1, 2: 2, 6: 7}[settings[1]]
                    lfeon = 1 if settings[1] == 6 else 0
                    layout = m.speakers(acmod, lfeon)
                    previous = {"streams": [{"codec": "E-AC-3" if eac3 else "AC-3", "sample_rate": settings[0], "channels": len(layout),
                                             "channel_mode": m.CODING_MODES[acmod], "channel_layout": layout, "samples": len(packets) * 1536, "is_vbr": False}]}
            data = target.read_bytes()
            result = manifest(name, extension, tool, settings, arguments, findings, fields, data, previous, block_ends, packets)
            manifest_path.write_text(json.dumps(result, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        analyzer = sys.argv[sys.argv.index("--analyzer") + 1] if "--analyzer" in sys.argv else None
        if not only_manifests and analyzer is None:
            raise SystemExit("The derived fixtures need --analyzer path/to/encspect, or pass --manifests-only.")
        for name, source, kind, description in DERIVED:
            extension = pathlib.Path(source).suffix
            target = OUT / (name + extension)
            manifest_path = OUT / (name + ".expected.json")
            previous = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else None
            block_ends = None
            if not only_manifests:
                derive(analyzer, kind, OUT / source, target)
                if extension == ".ac3":
                    block_ends = liba52_block_ends(tool_path, target)
            data = target.read_bytes()
            rows_with, stream = streams_of(data)
            result = {
                "description": description,
                "encoder": FFMPEG_VERSION,
                "command": ["make_external_ac3_fixtures.py", kind, source],
                "length": len(data),
                "format": "AC-3",
                "frames": [[offset, h["length"]] for offset, h in rows_with],
                "gaps": [],
                "streams": stream,
                "fields": DERIVED_FIELDS[kind],
                "findings": [],
                "crosscheck": {"ffprobe": True, "decode": True, **({"liba52": True} if extension == ".ac3" else {"skip_insertion": True})},
            }
            if kind in ("ac3-delta", "ac3-delta-block0"):
                result["crosscheck"]["decode"] = False
                result["crosscheck"]["skip"] = {"decode": "ffmpeg 6.1 counts the coupling channel's delta bit allocation bands from the coupling's first band "
                                                "(ac3.c, ff_ac3_bit_alloc_calc_mask), not from band 0 as A/52 7.2.2.6 and liba52 do, so it rejects "
                                                "segments that start at band 31."}
            if block_ends is not None:
                result["block_ends"] = block_ends
            elif previous and "block_ends" in previous:
                result["block_ends"] = previous["block_ends"]
            manifest_path.write_text(json.dumps(result, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{len(FIXTURES) + len(DERIVED)} manifests written to {OUT}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
