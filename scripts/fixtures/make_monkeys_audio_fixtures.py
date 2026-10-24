#!/usr/bin/env python3
"""Writes the hand-built Monkey's Audio fixtures in tests/fixtures/monkeys-audio (built-*.ape): damaged and unusual files made from the files mac wrote
(external-*.ape, make_external_monkeys_audio_fixtures.py) by taking their frames apart and putting them together again with monkeys_audio_model.py.

Each case changes one thing and says what the analyzer must make of it: a frame CRC or a byte of the audio that is changed with the descriptor MD5 made
right again (so that only the frame CRC can tell), a descriptor MD5 that is changed, files cut inside each of their parts, seek tables that put frames
where they are not, header fields no encoder writes, a version the decoder does not read, bytes before and after the file, and files of the format before
version 3.98. The manifests are worked out by monkeys_audio_model.py; crosscheck_monkeys_audio_fixtures.py confirms them with mac, ffmpeg and MediaInfo.

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


def load(name):
    data = (OUT / f"external-{name}.ape").read_bytes()
    a = m.parse(data)
    return data, a, [m.frame_data(data, a, f) for f in range(len(a.frames))]


MONO, MONO_A, MONO_FRAMES = load("frames-normal-16-mono")
STEREO, STEREO_A, STEREO_FRAMES = load("level-normal-16-stereo")
SILENT, SILENT_A, SILENT_FRAMES = load("silent-then-tone-stereo")
CHUNKS, CHUNKS_A, CHUNKS_FRAMES = load("wav-chunks")
LOUD24, LOUD24_A, LOUD24_FRAMES = load("loud-24-stereo")
MONO_PCM = sig.canonical("music", 44100, 1, 160000, 16)
LOUD24_PCM = sig.canonical("loud-sine-noise", 44100, 2, 8000, 24) * 4
STEREO_PCM = sig.canonical("music", 44100, 2, 24000, 16)
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
# These carry the frames of a 3.99 file under an older header, which is all the parser reads of them: versions up to 3.92 are not decoded, and the files
# of 3.93 to 3.98 that are decoded are the legacy-* fixtures (make_legacy_monkeys_audio_fixtures.py).
add("old-version-3900", m.build_old(frames, 3900, 2000, 2, 1, 44100, 12544, header_data=MONO_A.header_data))
add("old-version-3850", m.build_old(frames, 3850, 2000, 0, 1, 44100, 12544, header_data=MONO_A.header_data))
add("old-version-3820-crc", m.build_old(frames, 3820, 2000, 2, 1, 44100, 12544, header_data=MONO_A.header_data))
add("old-version-3800", m.build_old(frames, 3800, 2000, 0, 1, 44100, 12544, header_data=MONO_A.header_data))

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
OLD = {  # name: whether the frames store a CRC (the flag, or version 3.93 and later)
    "old-version-3900": True,
    "old-version-3850": False,
    "old-version-3820-crc": True,
    "old-version-3800": False,
}
PCM = {"mono": MONO_PCM, "chunks": STEREO_PCM, "loud24": LOUD24_PCM}
TOTAL = {"mono": FULL, "chunks": 24000, "loud24": 32000}


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
    """A file with the old header whose frames the decoder does not read: versions before 3.93, where the frames are 3.99's under another header."""
    a = m.parse_old(data)
    has_crc = OLD[name]
    samples = (a.total_frames - 1) * a.blocks_per_frame + a.final_blocks
    frames = m.old_frame_entries(data, a)
    bits = sum((f["end"] - f["start"]) * 8 for f in a.frames)
    n = len(a.frames)
    # Without the CRC flag a frame starts with a checksum of 30 bits, Rice-coded, that is no CRC: it is declared under its own name and never compared.
    checks = [{"name": "frame CRC-32" if has_crc else "frame checksum", "covers": "decodedAudio", "coversAllAudio": has_crc, "stored": n, "checked": 0, "failed": 0}]
    findings = ["monkeys-audio.old-format"] + (["monkeys-audio.final-frame-blocks"] if a.final_blocks > a.blocks_per_frame else [])
    return {"length": len(data), "file_version": a.version, "seek_entries": a.seek_entries, "regions": [{"name": "Monkey's Audio", "offset": 0, "length": len(data)}],
            "frames": frames, "stream_infos": [m.old_stream_info(a, bits, samples)], "decoded_samples": 0, "pcm_samples": 0,
            "verification": {"default": {"isVerified": None, "decoding": "notRequested", "checksums": checks},
                             "verify": {"isVerified": None, "decoding": "notPossible", "checksums": checks}},
            "decode_findings": [{"code": "decode.not-possible", "count": 1}], "findings": [finding(c) for c in findings], "encoder": {"tool": TOOL},
            "crosscheck": {"mac_verify": None}, "header": {"flags": a.flags}}


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
