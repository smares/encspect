#!/usr/bin/env python3
"""Writes the hand-built Monkey's Audio fixtures in tests/fixtures/monkeys-audio (built-*.ape): damaged and unusual files made from the files mac wrote
(external-*.ape, make_external_monkeys_audio_fixtures.py) by taking their frames apart and putting them together again with monkeys_audio_model.py.

Each case changes one thing and says what the analyzer must make of it: a frame CRC or a byte of the audio that is changed with the descriptor MD5 made
right again (so that only the frame CRC can tell), a descriptor MD5 that is changed, files cut inside each of their parts, seek tables that put frames
where they are not, header fields no encoder writes, a version the decoder does not read, bytes before and after the file, files of the format before
version 3.98 with the frames of a 3.99 file under their header (which their decoders read as damaged), and damaged copies of the legacy-* files of versions 3.80
to 3.92 (a bit of a residual flipped, a CRC or a checksum changed, the last frame cut, a start bit outside its word; their manifests name monkeys_audio_old.py as the
tool). The manifests are worked out by monkeys_audio_model.py; crosscheck_monkeys_audio_fixtures.py confirms them with mac, ffmpeg and MediaInfo.

The files are reproducible from the committed external ones, so CI runs this script and fails if anything differs.
"""

import hashlib
import json
import pathlib
import struct
import sys

import monkeys_audio_model as m
import monkeys_audio_signal as sig

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "monkeys-audio"

TOOL = "monkeys_audio_model.py, from the frames of a file mac wrote"
TOOL_OLD = "monkeys_audio_old.py, from the frames of a file it wrote"


def load(name):
    data = (OUT / f"external-{name}.ape").read_bytes()
    a = m.parse(data)
    return data, a, [m.frame_data(data, a, f) for f in range(len(a.frames))]


MONO, MONO_A, MONO_FRAMES = load("frames-normal-16-mono")
STEREO, STEREO_A, STEREO_FRAMES = load("level-normal-16-stereo")
SILENT, SILENT_A, SILENT_FRAMES = load("silent-then-tone-stereo")
CHUNKS, CHUNKS_A, CHUNKS_FRAMES = load("wav-chunks")
LOUD24, LOUD24_A, LOUD24_FRAMES = load("loud-24-stereo")
MASKED, MASKED_A, MASKED_FRAMES = load("mask-5.1-sides")
MONO_PCM = sig.canonical("music", 44100, 1, 160000, 16)
LOUD24_PCM = sig.canonical("loud-sine-noise", 44100, 2, 8000, 24) * 4
STEREO_PCM = sig.canonical("music", 44100, 2, 24000, 16)
MASKED_PCM = sig.canonical("music", 48000, 6, 8000, 16)
SILENT_PCM = sig.canonical("silent-then-tone", 44100, 2, 160000, 16)
MONO_BLOCKS = [73728, 73728, 12544]


def rebuild(data, a, frames, **overrides):
    """The file again from its frames, with fields overridden."""
    fields = {"level": a.level, "bits": a.bits, "channels": a.channels, "rate": a.rate, "blocks_per_frame": a.blocks_per_frame, "final_blocks": a.final_blocks,
              "header_data": a.header_data, "terminating": a.terminating}
    fields.update(overrides)
    return m.build(frames, **fields)


def flip(frame, index, mask=1):
    return frame[:index] + bytes([frame[index] ^ mask]) + frame[index + 1:]


def pcm_of(pcm, blocks_per_frame_bytes, upto):
    return {"md5": hashlib.md5(pcm[:upto * blocks_per_frame_bytes]).hexdigest()}


def decoded(checked, failed=0, samples=160000, findings=None, status="decoded", pcm_samples=None):
    return {"status": status, "checked": checked, "failed": failed, "samples": samples, "pcm_samples": samples if pcm_samples is None else pcm_samples,
            "findings": findings or []}


def crc_mismatch(count=1):
    return [{"code": "monkeys-audio.frame-crc-mismatch", "count": count}]


def finding(code, contains=""):
    return {"code": code, "contains": contains}


CASES = {}


def add(name, data, **expect):
    CASES[name] = (data, expect)


def tweak(data, offset, value):
    return data[:offset] + bytes([value]) + data[offset + 1:]


def set32(data, offset, value):
    return data[:offset] + struct.pack("<I", value) + data[offset + 4:]


def set16(data, offset, value):
    return data[:offset] + struct.pack("<H", value) + data[offset + 2:]


# --- the audio changed, the structure right
frames = list(MONO_FRAMES)
crc_changed = list(frames)
crc_changed[1] = flip(frames[1], 3)
add("frame-crc-changed", rebuild(MONO, MONO_A, crc_changed))

bit_flipped = list(frames)
bit_flipped[1] = flip(frames[1], 20000, 0x10)
add("residual-bit-flipped", rebuild(MONO, MONO_A, bit_flipped))

changed = bytearray(MONO)
changed[40] ^= 1
add("md5-changed", bytes(changed))

flipped = bytearray(MONO)
flipped[MONO_A.data_start + 30000] ^= 0x04
add("audio-byte-flipped", bytes(flipped))

# --- cut inside each part
add("cut-mid-frame", MONO[:MONO_A.data_start + 60000])
add("cut-after-first-frame", MONO[:MONO_A.data_start + 27292])
add("cut-in-seek-table", MONO[:MONO_A.seek_at + 6])
add("cut-in-header", MONO[:MONO_A.header_at + 10])
add("cut-in-descriptor", MONO[:30])
add("cut-in-terminating-data", CHUNKS[:CHUNKS_A.data_end + 10])

# --- the seek table
add("seek-entries-decreasing", rebuild(MONO, MONO_A, frames, seek=[MONO_A.seek[0], MONO_A.seek[2], MONO_A.seek[1]]))
beyond = list(MONO_A.seek)
beyond[2] = 0x70000000
add("seek-entry-beyond-data", rebuild(MONO, MONO_A, frames, seek=beyond))
add("seek-table-short", rebuild(MONO, MONO_A, frames[:2], total_frames=3))
add("seek-table-long", rebuild(MONO, MONO_A, frames, seek=[v + 8 for v in [*MONO_A.seek, MONO_A.seek[2], MONO_A.seek[2]]]))
first_off = list(MONO_A.seek)
first_off[0] += 4  # data stays where it is; only the first entry lies
add("seek-first-entry-off", rebuild(MONO, MONO_A, frames, seek=first_off))
add("seek-table-bytes-odd", rebuild(MONO, MONO_A, frames, seek_pad=1))

# --- the descriptor
add("descriptor-bytes-larger", rebuild(MONO, MONO_A, frames, descriptor_bytes=60))
add("descriptor-bytes-smaller", rebuild(MONO, MONO_A, frames, descriptor_bytes=40))
add("header-bytes-larger", rebuild(MONO, MONO_A, frames, header_bytes=32))
add("header-data-too-large", rebuild(MONO, MONO_A, frames, header_data_bytes=9 << 20))
add("frame-data-bytes-too-large", rebuild(MONO, MONO_A, frames, frame_data_bytes=len(MONO) * 2))
add("frame-data-bytes-too-small", rebuild(MONO, MONO_A, frames, frame_data_bytes=len(MONO_FRAMES[0]) + len(MONO_FRAMES[1])))
add("terminating-bytes-too-large", rebuild(CHUNKS, CHUNKS_A, CHUNKS_FRAMES, terminating_bytes=10 << 20))
add("program-version-zero", rebuild(MONO, MONO_A, frames, program=0))
add("version-4110", rebuild(MONO, MONO_A, frames, version=4110))
add("version-4000", rebuild(MONO, MONO_A, frames, version=4000))
add("signature-float", rebuild(MONO, MONO_A, frames, signature=b"MACF"))
add("wav-header-created", rebuild(MONO, MONO_A, frames, header_data=b"", flags=32))
# The header data ends 10 bytes into the fmt chunk, before its channel mask: the layout is then the default one, which has none for six channels.
add("wav-header-cut-in-fmt", rebuild(MASKED, MASKED_A, MASKED_FRAMES, header_data=MASKED_A.header_data[:30]))

# --- the header
add("channels-zero", rebuild(MONO, MONO_A, frames, channels=0))
add("channels-33", rebuild(MONO, MONO_A, frames, channels=33))
add("bits-12", rebuild(MONO, MONO_A, frames, bits=12))
add("sample-rate-zero", rebuild(MONO, MONO_A, frames, rate=0))
add("blocks-per-frame-zero", rebuild(MONO, MONO_A, frames, blocks_per_frame=0))
add("final-frame-blocks-too-large", rebuild(MONO, MONO_A, frames, final_blocks=73729))
add("total-frames-zero", rebuild(MONO, MONO_A, frames, total_frames=0))
add("compression-level-unknown", rebuild(MONO, MONO_A, frames, level=1234))

# --- around the file
add("junk-before-descriptor", rebuild(MONO, MONO_A, frames, junk=b"\x55" * 100))
add("trailing-bytes", MONO + b"\xab" * 20)
add("trailing-bytes-after-terminating", CHUNKS + b"\xab" * 20)

# --- frames that are too short
short_start = list(frames)
short_start[2] = struct.pack(">I", 0x80000001)
add("frame-too-short-for-special-codes", rebuild(MONO, MONO_A, short_start))
tiny = list(frames)
tiny[2] = b"\x01\x02\x03"
add("frame-too-short-for-crc", rebuild(MONO, MONO_A, tiny))

# --- the format before version 3.98
# The first four carry the frames of a 3.99 file under an older header. The decoders of those versions read them as the damaged files they are: the frames do not
# give the CRC or the checksum they store, or no value at all, and the last frame has more samples than a frame of those versions can.
add("old-version-3900", m.build_old(frames, 3900, 2000, 2, 1, 44100, 12544, header_data=MONO_A.header_data))
add("old-version-3850", m.build_old(frames, 3850, 2000, 0, 1, 44100, 12544, header_data=MONO_A.header_data))
add("old-version-3820-crc", m.build_old(frames, 3820, 2000, 2, 1, 44100, 12544, header_data=MONO_A.header_data))
add("old-version-3800", m.build_old(frames, 3800, 2000, 0, 1, 44100, 12544, header_data=MONO_A.header_data))


def legacy(name):
    """A committed legacy-* fixture of a version before 3.93 (make_legacy_monkeys_audio_fixtures.py), what the model reads of it, and the file offset of the byte
    of the frames' stream at an index."""
    data = (OUT / f"legacy-{name}.ape").read_bytes()
    a = m.parse_old(data)
    return data, a, lambda index: m.stream_offset(a.data_start, index)


def flip_at(data, offset, mask=1):
    return data[:offset] + bytes([data[offset] ^ mask]) + data[offset + 1:]


# Damaged copies of the files of versions 3.80 to 3.92: a bit of the coded values flipped, the CRC or the checksum of a frame changed, the last frame cut. mac -V
# rejects each of them; the analyzer decodes the frames it can and reports the ones that differ.
D3860, A3860, AT3860 = legacy("3860-normal-16-stereo-frames")
add("old-3860-residual-bit-flipped", flip_at(D3860, AT3860(A3860.frames[1]["start"] + 3000), 0x10))
add("old-3860-crc-changed", flip_at(D3860, AT3860(A3860.frames[1]["start"] + 3)))
add("old-3860-last-frame-cut", D3860[:AT3860(A3860.frames[2]["start"] + 700)])
D3850, A3850, AT3850 = legacy("3850-normal-16-stereo-silent-then-tone-checksum")
add("old-3850-checksum-changed", flip_at(D3850, AT3850(A3850.frames[1]["start"] + 3), 0x02))
D3830, A3830, AT3830 = legacy("3830-high-16-stereo-silent-then-tone")
add("old-3830-crc-changed", flip_at(D3830, AT3830(A3830.frames[1]["start"] + 3)))
D3800, A3800, AT3800 = legacy("3800-normal-16-stereo-frames")
add("old-3800-residual-bit-flipped", flip_at(D3800, AT3800(A3800.frames[1]["start"] + 2000), 0x20))
# The table of start bits puts the second frame 32 bits further into its word than any encoder does (a bit of 32 or more is a bit of a later word): the
# analyzer reads the frame from there, so its CRC and values are not its own, and says so with monkeys-audio.frame-start-bit. mac -V passes the file: the SDK seeks
# only to the first frame or after a gap and reads the others where the frame before ended, so it never looks at the table (docs/formats/monkeys-audio.md, "Decoding").
BITS3800 = A3800.seek_at + 4 * A3800.seek_entries
add("old-3800-start-bit-outside-word", D3800[:BITS3800 + 1] + bytes([A3800.start_bits[1] + 32]) + D3800[BITS3800 + 2:])
D3910, A3910, AT3910 = legacy("3910-high-24-stereo")
add("old-3910-residual-bit-flipped", flip_at(D3910, AT3910(A3910.frames[0]["start"] + 2000), 0x08))
add("old-3910-last-frame-cut", D3910[:AT3910(A3910.frames[0]["start"] + 1800)])

# --- a damaged frame of 24-bit audio among good ones: the frames after it must verify. The audio is loud enough that the first stage of the predictor
# overflows 32 bits, so a decoder that stayed in the interim mode after the damaged frame would fail the frames that follow it.
loud_frames = [LOUD24_FRAMES[0]] * 4
damaged_24 = list(loud_frames)
damaged_24[1] = flip(loud_frames[1], 3)
add("24-bit-frame-crc-changed", rebuild(LOUD24, LOUD24_A, damaged_24, blocks_per_frame=8000, final_blocks=8000))


# What each case must give: the findings of the analysis (codes), what decoding does (status, frames whose CRC was compared, frames whose CRC differs,
# samples written, the findings it adds), and the figures of the verification the rule of docs/design/decoders.md makes of them. The defaults are those of
# an intact file.
FULL = 160000
DEFAULT = {"findings": [], "decode": ("decoded", 3, 0, FULL, [])}
EXPECT = {
    "frame-crc-changed": {"mac": False, "decode": ("decoded", 3, 1, FULL, ["monkeys-audio.frame-crc-mismatch"])},
    "residual-bit-flipped": {"decode": ("decoded", 2, 0, 86272, ["decode.frame-invalid"]), "written": 123136, "prefix": False},
    "md5-changed": {"findings": ["monkeys-audio.md5-mismatch"]},
    "audio-byte-flipped": {"mac": False, "findings": ["monkeys-audio.md5-mismatch"], "decode": ("decoded", 2, 0, 86272, ["decode.frame-invalid"]), "written": 94464, "prefix": False},
    "cut-mid-frame": {"findings": ["monkeys-audio.data-cut", "monkeys-audio.md5-not-compared", "monkeys-audio.frame-too-short", "monkeys-audio.frame-cut"],
                      "decode": ("decoded", 1, 0, 73728, ["decode.frame-invalid"]), "written": 114688, "complete": False, "crcs": 2, "listed": 2},
    "cut-after-first-frame": {"findings": ["monkeys-audio.data-cut", "monkeys-audio.md5-not-compared", "monkeys-audio.frame-cut", "monkeys-audio.frame-too-short"],
                              "decode": ("decoded", 1, 0, 73728, []), "complete": False, "crcs": 1, "listed": 2},
    "cut-in-seek-table": {"findings": ["monkeys-audio.data-cut", "monkeys-audio.md5-not-compared", "monkeys-audio.seek-table-cut", "monkeys-audio.frame-cut",
                                       "monkeys-audio.frame-too-short"], "decode": ("decoded", 0, 0, 0, []), "complete": False, "crcs": 0, "listed": 1},
    "cut-in-header": {"findings": ["monkeys-audio.header-cut"], "bare": True},
    "cut-in-descriptor": {"findings": ["monkeys-audio.header-cut"], "bare": True},
    "cut-in-terminating-data": {"findings": ["monkeys-audio.data-cut", "monkeys-audio.md5-not-compared"], "decode": ("decoded", 1, 0, 24000, []), "pcm": "chunks", "complete": True},
    "seek-entries-decreasing": {"findings": ["monkeys-audio.frame-misplaced"], "decode": ("decoded", 1, 0, 73728, []), "complete": False, "crcs": 1, "listed": 1, "rated": 1},
    "seek-entry-beyond-data": {"findings": ["monkeys-audio.frame-misplaced"], "decode": ("decoded", 1, 0, 73728, []), "complete": False, "crcs": 1, "listed": 1, "rated": 1},
    "seek-table-short": {"findings": ["monkeys-audio.seek-table-size"], "decode": ("decoded", 2, 0, 147456, []), "complete": False},
    "seek-table-long": {"findings": ["monkeys-audio.seek-table-size"]},
    "seek-first-entry-off": {"findings": ["monkeys-audio.seek-table-start"], "decode": ("decoded", 2, 0, 86272, ["decode.frame-invalid"])},
    "seek-table-bytes-odd": {"findings": ["monkeys-audio.seek-table-size"]},
    "descriptor-bytes-larger": {"findings": ["monkeys-audio.descriptor-size"]},
    "descriptor-bytes-smaller": {"findings": ["monkeys-audio.descriptor-size"]},
    "header-bytes-larger": {"findings": ["monkeys-audio.descriptor-size"]},
    "header-data-too-large": {"findings": ["monkeys-audio.data-cut", "monkeys-audio.header-data-size", "monkeys-audio.md5-not-compared", "monkeys-audio.seek-table-start",
                                           "monkeys-audio.frame-misplaced"], "decode": ("decoded", 0, 0, 0, []), "complete": False, "crcs": 0, "listed": 0, "rated": 0},
    "frame-data-bytes-too-large": {"findings": ["monkeys-audio.data-cut", "monkeys-audio.md5-not-compared", "monkeys-audio.frame-cut"], "complete": False},
    "frame-data-bytes-too-small": {"findings": ["monkeys-audio.md5-mismatch", "monkeys-audio.frame-cut", "monkeys-audio.frame-too-short", "monkeys-audio.trailing-data"],
                                   "decode": ("decoded", 2, 0, 147456, []), "complete": False, "crcs": 2, "listed": 2},
    "terminating-bytes-too-large": {"findings": ["monkeys-audio.data-cut", "monkeys-audio.terminating-data-size", "monkeys-audio.md5-not-compared"],
                                    "decode": ("decoded", 1, 0, 24000, []), "pcm": "chunks", "complete": True},
    "program-version-zero": {},
    "version-4110": {},
    "version-4000": {"findings": ["monkeys-audio.version"], "decode": ("notPossible", 0, 0, 0, ["decode.not-possible"])},
    "signature-float": {},
    "wav-header-created": {},
    "wav-header-cut-in-fmt": {"pcm": "masked", "decode": ("decoded", 1, 0, 8000, [])},
    "channels-zero": {"findings": ["monkeys-audio.channels"], "decode": ("notPossible", 0, 0, 0, ["decode.not-possible"])},
    "channels-33": {"findings": ["monkeys-audio.channels"], "decode": ("notPossible", 0, 0, 0, ["decode.not-possible"])},
    "bits-12": {"findings": ["monkeys-audio.bits-per-sample"], "decode": ("notPossible", 0, 0, 0, ["decode.not-possible"])},
    "sample-rate-zero": {"findings": ["monkeys-audio.sample-rate"]},
    "blocks-per-frame-zero": {"findings": ["monkeys-audio.blocks-per-frame", "monkeys-audio.final-frame-blocks"],
                              "decode": ("notPossible", 0, 0, 0, ["decode.not-possible"])},
    "final-frame-blocks-too-large": {"findings": ["monkeys-audio.final-frame-blocks"], "decode": ("decoded", 2, 0, 147456, ["decode.frame-invalid"]), "written": 159744},
    "total-frames-zero": {"findings": ["monkeys-audio.no-frames", "monkeys-audio.seek-table-size"], "decode": ("decoded", 0, 0, 0, []), "complete": False, "crcs": 0},
    "compression-level-unknown": {"findings": ["monkeys-audio.compression-level"], "decode": ("notPossible", 0, 0, 0, ["decode.not-possible"])},
    "junk-before-descriptor": {"findings": ["monkeys-audio.junk-before-descriptor"]},
    "trailing-bytes": {"findings": ["monkeys-audio.trailing-data"]},
    "trailing-bytes-after-terminating": {"findings": ["monkeys-audio.trailing-data"], "pcm": "chunks", "decode": ("decoded", 1, 0, 24000, [])},
    "frame-too-short-for-special-codes": {"findings": ["monkeys-audio.frame-too-short"], "decode": ("decoded", 2, 0, 147456, []), "complete": False},
    "frame-too-short-for-crc": {"findings": ["monkeys-audio.frame-too-short"], "decode": ("decoded", 2, 0, 147456, []), "complete": False, "crcs": 2},
    "24-bit-frame-crc-changed": {"mac": False, "decode": ("decoded", 4, 1, 32000, ["monkeys-audio.frame-crc-mismatch"]), "pcm": "loud24", "prefix": True},
}
# The files with the old header: whether the frames store a CRC (the flag), what decoding does (status, frames whose CRC or checksum was compared, those that
# differ, samples written, the findings it adds with their counts) and whether mac -V accepts the file (None: not compared). The decoders read the frames that are
# not 3.99's as the damaged ones they are.
OLD = {
    "old-version-3900": (True, ("decoded", 0, 0, 0, [("decode.frame-invalid", 3)]), False, []),
    "old-version-3850": (False, ("decoded", 2, 2, 18432, [("monkeys-audio.frame-checksum-mismatch", 2), ("decode.frame-invalid", 1)]), False, []),
    "old-version-3820-crc": (True, ("decoded", 2, 2, 18432, [("monkeys-audio.frame-crc-mismatch", 2), ("decode.frame-invalid", 1)]), False, []),
    "old-version-3800": (False, ("decoded", 2, 2, 18432, [("monkeys-audio.frame-checksum-mismatch", 2), ("decode.frame-invalid", 1)]), False, []),
    "old-3860-residual-bit-flipped": (True, ("decoded", 3, 1, 20000, [("monkeys-audio.frame-crc-mismatch", 1)]), False, []),
    "old-3860-crc-changed": (True, ("decoded", 3, 1, 20000, [("monkeys-audio.frame-crc-mismatch", 1)]), False, []),
    "old-3860-last-frame-cut": (True, ("decoded", 2, 0, 18432, [("decode.frame-invalid", 1)]), False, ["monkeys-audio.frame-cut"]),
    "old-3850-checksum-changed": (False, ("decoded", 2, 1, 12216, [("monkeys-audio.frame-checksum-mismatch", 1)]), False, []),
    "old-3830-crc-changed": (True, ("decoded", 2, 1, 12216, [("monkeys-audio.frame-crc-mismatch", 1)]), False, []),
    "old-3800-residual-bit-flipped": (True, ("decoded", 2, 0, 10784, [("decode.frame-invalid", 1)]), False, []),
    "old-3800-start-bit-outside-word": (True, ("decoded", 2, 0, 10784, [("decode.frame-invalid", 1)]), None, ["monkeys-audio.frame-start-bit"]),
    "old-3910-residual-bit-flipped": (True, ("decoded", 0, 0, 0, [("decode.frame-invalid", 1)]), False, []),
    "old-3910-last-frame-cut": (True, ("decoded", 0, 0, 0, [("decode.frame-invalid", 1)]), False, ["monkeys-audio.frame-cut"]),
}
PCM = {"mono": MONO_PCM, "chunks": STEREO_PCM, "loud24": LOUD24_PCM, "masked": MASKED_PCM}
TOTAL = {"mono": FULL, "chunks": 24000, "loud24": 32000, "masked": 8000}


def prefix_md5(kind, samples):
    pcm = PCM[kind]
    width = len(pcm) // TOTAL[kind]
    return {"md5": hashlib.md5(pcm[:samples * width]).hexdigest()}


def manifest_of(name, data):
    case = {**DEFAULT, **EXPECT.get(name, {})}
    kind = case.get("pcm", "mono")
    if case.get("bare"):
        return {"length": len(data), "regions": [], "frames": [], "stream_infos": [],
                "decoded_samples": 0, "pcm_samples": 0, "decode_findings": [], "findings": [finding(c) for c in case["findings"]], "encoder": {"tool": TOOL},
                "crosscheck": {"mac_verify": None}}
    a = m.parse(data)
    status, checked, failed, samples, found = case["decode"]
    decode = {"status": status, "checked": checked, "failed": failed, "samples": samples, "pcm_samples": samples,
              "findings": [{"code": c, "count": 1} for c in found]}
    # The audio is a prefix of the signal whenever decoding stopped at a frame boundary after whole frames.
    total = TOTAL[kind]
    written = case.get("written", samples)
    decode["pcm_samples"] = written
    pcm = prefix_md5(kind, written) if status == "decoded" and case.get("prefix", written in (73728, 114688, 147456, 159744, total)) else None
    regions = m.regions_of(data, a)
    result = m.manifest(data, a, regions, {"tool": TOOL}, [finding(c) for c in case["findings"]], pcm, decode, frames_complete=case.get("complete"),
                        stored_crcs=case.get("crcs"), frame_limit=case.get("listed", 64), placed=case.get("listed"), rated=case.get("rated"))
    verified = result["verification"]["verify"]["isVerified"]
    result["crosscheck"] = {"mac_verify": case.get("mac", True if verified is True else None)}
    return result


def old_manifest(name, data):
    """A file with the old header whose frames are damaged: the structure is the model's, what decoding does is the table's."""
    a = m.parse_old(data)
    has_crc, (status, checked, failed, written, found), mac, extra = OLD[name]
    samples = (a.total_frames - 1) * a.blocks_per_frame + a.final_blocks
    frames = m.old_frame_entries(data, a)
    bits = sum((f["end"] - f["start"]) * 8 for f in a.frames)
    n = len(a.frames)
    complete = "monkeys-audio.frame-cut" not in extra  # a frame cut by the end of the data leaves the stream's audio not all covered
    base = {"name": "frame CRC-32" if has_crc else "frame checksum", "covers": "decodedAudio", "coversAllAudio": complete, "stored": n}
    default = [{**base, "checked": 0, "failed": 0}]
    verify = [{**base, "checked": checked, "failed": failed}]
    findings = ["monkeys-audio.old-format"] + (["monkeys-audio.final-frame-blocks"] if a.final_blocks > a.blocks_per_frame else []) + extra
    return {"length": len(data), "file_version": a.version, "seek_entries": a.seek_entries, "regions": [{"name": "Monkey's Audio", "offset": 0, "length": len(data)}],
            "frames": frames, "stream_infos": [m.old_stream_info(a, bits, samples)], "decoded_samples": written, "pcm_samples": written,
            "verification": {"default": {"isVerified": m.verdict(default), "decoding": "notRequested", "checksums": default},
                             "verify": {"isVerified": m.verdict(verify), "decoding": status, "checksums": verify}},
            "decode_findings": [{"code": code, "count": count} for code, count in found], "findings": [*[finding(c) for c in findings], *m.wav_header(a.header_data, a.channels)[1]], "encoder": {"tool": TOOL if name.startswith("old-version-") else TOOL_OLD},
            "crosscheck": {"mac_verify": mac}, "header": {"flags": a.flags}}


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    for old in [*OUT.glob("built-*.ape"), *OUT.glob("built-*.expected.json")]:
        old.unlink()
    for name, (data, _expect) in CASES.items():
        (OUT / f"built-{name}.ape").write_bytes(data)
        manifest = old_manifest(name, data) if name in OLD else manifest_of(name, data)
        (OUT / f"built-{name}.expected.json").write_text(m.dump(manifest), encoding="utf-8")
    print(f"Wrote {len(CASES)} fixtures to {OUT}")


if __name__ == "__main__":
    main()
