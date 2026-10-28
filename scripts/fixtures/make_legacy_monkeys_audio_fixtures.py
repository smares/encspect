#!/usr/bin/env python3
"""Writes the Monkey's Audio fixtures of versions 3.80 to 3.98 in tests/fixtures/monkeys-audio (legacy-*.ape).

The SDK 13.42 decodes these versions and cannot write them, and the public files of them are few (the FFmpeg sample archive has one of 3.97 that this
repository may not hold; docs/formats/monkeys-audio.md, "Decoding"). monkeys_audio_legacy.py (3.93 to 3.98) and monkeys_audio_old.py (3.80 to 3.92) are
encoders written as the inverse of the SDK's decoders, and every file they write is held to the SDK, not to the encoder: crosscheck_monkeys_audio_fixtures.py
runs `mac -V`, which decodes each frame with the SDK's own decoder of the version and compares its CRC or checksum, and `mac -d`, whose samples must have the MD5
of the signal that went in. FFmpeg decodes the files of 3.93 and later too.

The cases cover what the versions change: the bit coding of 3.80 to 3.85, of 3.86 to 3.88, of 3.89 and the range coder of 3.90 to 3.92, the frames of 3.80 that
follow each other bit by bit, the CRC of 32 bits of 3.80 to 3.82 and of 31 bits with special codes from 3.83, the checksum of a file without the CRC flag, the
8-bit samples that are unsigned up to 3.83, the four predictors of 3.80 to 3.92 (and the filters of the extra high level before and from 3.83), the predictor of
3.93 and 3.94 (levels fast to extra high), the predictors of 3.95 to 3.97 with the old delta (every level, insane included), 3.98 with its descriptor and the new
delta, 8, 16 and 24 bits, mono and stereo, frames flagged silent and pseudo stereo, loud noise and spikes (the escapes of the codings), several frames, and the
old header's optional peak level.

The files are committed, so --manifests-only recomputes the manifests without running the encoder (which takes minutes at the highest levels).
"""

import hashlib
import json
import pathlib
import sys

import monkeys_audio_legacy as legacy
import monkeys_audio_model as m
import monkeys_audio_old as old
import monkeys_audio_signal as sig

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "monkeys-audio"
RATE = 44100
TOOL = "monkeys_audio_legacy.py, an encoder of the coding of versions 3.93 to 3.98 that mac -V verifies"
TOOL_OLD = "monkeys_audio_old.py, an encoder of the coding of versions 3.80 to 3.92 that mac -V verifies"
FRAME_95 = 73728 * 4
FRAME_90 = 73728
FRAME_FAST = 9216

# name: (version, level, channels, bits, signal, samples, peak level[, options]). A signal is a kind of monkeys_audio_signal.py or "spikes", or (silent samples,
# kind) for silence followed by that kind. The options: "crc": False for a file without the CRC flag (the frames then start with a checksum); "padded": False for a
# file of 3.81 or later that ends where the coded data of its last frame does, without the four zero bytes the encoder script appends (the SDK's bit array reads zeros
# past the end of the file, which the range coder of 3.90 to 3.92 does for its last two bytes); "forced": the values of every array of the Rice coding of 3.89 whose
# overflow is exactly 16 or 32 (monkeys_audio_old.encode_array_rice).
NO_CRC = {"crc": False}
UNPADDED = {"padded": False}
FIXTURES = {
    # Version 3.80: frames that follow each other bit by bit, a table of the bit each starts at, the CRC in 32 bits, the Rice coding up to 3.85, the extra high
    # predictor with 128 taps.
    "legacy-3800-normal-16-stereo-frames": (3800, 2000, 2, 16, "music", 20000, None),
    "legacy-3800-fast-8-mono": (3800, 1000, 1, 8, "music", 12000, None),
    "legacy-3800-extra-high-16-stereo-frames": (3800, 4000, 2, 16, "music", 76000, None),
    "legacy-3800-high-24-stereo": (3800, 3000, 2, 24, "music", 3000, None),
    "legacy-3800-normal-16-stereo-checksum": (3800, 2000, 2, 16, "music", 10000, None, NO_CRC),
    # 3.81 and 3.82: frames on byte boundaries, the CRC still in 32 bits, so no special codes: a silent frame is coded like any other.
    "legacy-3810-high-16-mono": (3810, 3000, 1, 16, "music", 12000, None),
    "legacy-3820-normal-16-stereo-silence-coded": (3820, 2000, 2, 16, (FRAME_FAST, "music"), 4000, None),
    "legacy-3820-extra-high-8-stereo": (3820, 4000, 2, 8, "music", 3000, None),
    # 3.83: special codes, the extra high predictor with 256 taps and the filter of 8 taps, the 8-bit samples still unsigned.
    "legacy-3830-extra-high-16-stereo": (3830, 4000, 2, 16, "music", 3000, None),
    "legacy-3830-normal-8-stereo-silent-then-tone": (3830, 2000, 2, 8, (FRAME_FAST, "music"), 3000, None),
    "legacy-3830-high-16-stereo-silent-then-tone": (3830, 3000, 2, 16, (FRAME_FAST, "music"), 3000, None),
    # 3.84 and later: signed 8-bit samples.
    "legacy-3840-normal-8-stereo": (3840, 2000, 2, 8, "music", 12000, None),
    "legacy-3840-high-16-pseudo-stereo": (3840, 3000, 2, 16, "pseudo", 12000, None),
    "legacy-3840-fast-16-mono-silent-then-tone": (3840, 1000, 1, 16, (FRAME_FAST, "music"), 3000, None),
    "legacy-3850-normal-16-stereo-silent-then-tone-checksum": (3850, 2000, 2, 16, (FRAME_FAST, "music"), 3000, None, NO_CRC),
    "legacy-3840-extra-high-16-stereo-frames": (3840, 4000, 2, 16, "music", 76000, None),
    "legacy-3850-extra-high-24-stereo": (3850, 4000, 2, 24, "music", 3000, None),
    # 3.86 to 3.88: the Rice coding with one running sum; 3.89 caps the overflow.
    "legacy-3860-normal-16-stereo-frames": (3860, 2000, 2, 16, "music", 20000, None),
    "legacy-3880-high-24-mono-peak-level": (3880, 3000, 1, 24, "music", 12000, 777777),
    "legacy-3880-fast-16-stereo-spikes": (3880, 1000, 2, 16, "small-spikes", 10000, None),
    "legacy-3880-extra-high-16-stereo": (3880, 4000, 2, 16, "music", 3000, None),
    "legacy-3890-normal-16-stereo-spikes": (3890, 2000, 2, 16, "spikes", 10000, None),
    # An overflow of exactly 16 and 32, which the cap of 3.89 reads as a raised parameter and an overflow of 0 (the encoder never writes one: a hand-made coding of values
    # that would fit the parameter they had).
    "legacy-3890-normal-16-mono-overflow-exactly-16": (3890, 2000, 1, 16, "music", 12000, None, {"forced": {100: 1, 700: 2, 3000: 1, 5000: 1}}),
    "legacy-3890-extra-high-16-stereo-checksum": (3890, 4000, 2, 16, "music", 3000, None, NO_CRC),
    "legacy-3890-high-8-mono-checksum": (3890, 3000, 1, 8, "music", 12000, None, NO_CRC),
    # 3.90 to 3.92: the range coder, an array at a time.
    "legacy-3900-normal-16-stereo": (3900, 2000, 2, 16, "music", 4000, None),
    "legacy-3900-fast-16-mono-spikes": (3900, 1000, 1, 16, "spikes", 10000, None),
    # Files that end where the coded data of the last frame does: the range coder reads two bytes past it, which the SDK's bit array gives as zeros.
    "legacy-3900-fast-16-mono-spikes-unpadded": (3900, 1000, 1, 16, "spikes", 10000, None, UNPADDED),
    "legacy-3910-fast-8-mono-unpadded": (3910, 1000, 1, 8, "music", 3000, None, UNPADDED),
    "legacy-3920-normal-16-pseudo-stereo-unpadded": (3920, 2000, 2, 16, "pseudo", 3000, None, UNPADDED),
    "legacy-3910-high-24-stereo": (3910, 3000, 2, 24, "music", 3000, None),
    "legacy-3910-normal-24-stereo-spikes": (3910, 2000, 2, 24, "spikes", 6000, None),
    "legacy-3910-fast-8-mono": (3910, 1000, 1, 8, "music", 3000, None),
    "legacy-3910-normal-16-stereo-frames": (3910, 2000, 2, 16, "music", 76000, None),
    "legacy-3920-extra-high-16-stereo-frames": (3920, 4000, 2, 16, "music", 76000, None),
    "legacy-3920-extra-high-16-stereo-silent-then-tone": (3920, 4000, 2, 16, (FRAME_90, "music"), 3000, None),
    "legacy-3920-normal-16-pseudo-stereo": (3920, 2000, 2, 16, "pseudo", 3000, None),
    # A file of 3.90 to 3.92 without the CRC flag: the SDK reads no checksum from it and decodes silence, so the analyzer does not decode it.
    "legacy-3900-normal-16-stereo-checksum-not-decoded": (3900, 2000, 2, 16, "music", 3000, None, NO_CRC),
    "legacy-3930-normal-16-stereo": (3930, 2000, 2, 16, "music", 4000, None),
    "legacy-3930-loud-noise-16-stereo": (3930, 2000, 2, 16, "loud", 2500, None),
    "legacy-3930-silent-then-tone-16-stereo": (3930, 1000, 2, 16, (73728, "music"), 3000, None),
    "legacy-3940-extra-high-16-stereo": (3940, 4000, 2, 16, "music", 3000, None),
    "legacy-3940-fast-16-mono-frames": (3940, 1000, 1, 16, "quiet", 150000, None),
    "legacy-3940-normal-16-pseudo-stereo": (3940, 2000, 2, 16, "pseudo", 3000, None),
    "legacy-3950-high-16-stereo": (3950, 3000, 2, 16, "music", 3000, None),
    "legacy-3950-normal-16-silent-then-tone-stereo": (3950, 2000, 2, 16, (FRAME_95, "music"), 4000, None),
    "legacy-3960-insane-16-stereo": (3960, 5000, 2, 16, "music", 2500, None),
    "legacy-3960-normal-16-pseudo-stereo": (3960, 2000, 2, 16, "pseudo", 3000, None),
    "legacy-3970-normal-16-stereo-peak-level": (3970, 2000, 2, 16, "music", 4000, 23456),
    "legacy-3970-normal-8-stereo": (3970, 2000, 2, 8, "music", 3000, None),
    "legacy-3970-high-24-stereo": (3970, 3000, 2, 24, "music", 3000, None),
    "legacy-3970-normal-16-mono": (3970, 2000, 1, 16, "music", 4000, None),
    "legacy-3970-normal-16-silent-then-tone-mono": (3970, 2000, 1, 16, (FRAME_95, "music"), 4000, None),
    "legacy-3980-normal-16-stereo": (3980, 2000, 2, 16, "music", 4000, None),
    "legacy-3980-extra-high-8-mono": (3980, 4000, 1, 8, "music", 3000, None),
}


def kind_samples(kind, channels, count, bits):
    if kind not in ("spikes", "small-spikes"):
        return list(sig.samples(kind, RATE, channels, count, bits))
    # Silence with a loud sample now and then, which the predictors answer with a residual far above the size the coding has adapted to (the overflow of the
    # Rice coding, the escape of the range coder). The small ones, a tenth of full scale, are for the Rice coding without the cap of 3.89: its code for a value is
    # as many zero bits as the value over 2^k, and the SDK's decoder of 3.86 to 3.88 reads past the end of its bit array for a run of more than 131,072 of them
    # (it refills the array only between values), which the spikes of three quarters of full scale make.
    scale = 1 << (bits - 1)
    height = scale * 3 // 4 if kind == "spikes" else scale // 10
    return [[height * (1 if (i // 3000) % 2 == 0 else -1) if i % 3000 == 1500 + 50 * c else 0 for c in range(channels)] for i in range(count)]


def samples_of(spec):
    _version, _level, channels, bits, signal, count, _peak = spec[:7]
    if isinstance(signal, tuple):
        silent, kind = signal
        return [[0] * channels for _ in range(silent)] + kind_samples(kind, channels, count, bits)
    return kind_samples(signal, channels, count, bits)


def options_of(spec):
    return spec[7] if len(spec) > 7 else {}


def wav_pcm(samples, bits):
    """The audio as the WAV file holds it (8 bits unsigned), which the frame CRC covers."""
    out = bytearray()
    for frame in samples:
        for value in frame:
            out += bytes([value + 128]) if bits == 8 else value.to_bytes(bits // 8, "little", signed=True)
    return bytes(out)


def canonical_pcm(samples, bits):
    out = bytearray()
    for frame in samples:
        for value in frame:
            out += value.to_bytes(bits // 8, "little", signed=True)
    return bytes(out)


def encode(name):
    spec = FIXTURES[name]
    version, level, channels, bits, _signal, _count, peak = spec[:7]
    samples = samples_of(spec)
    if version < 3930:
        data, frames, counts = old.encode(samples, version, level, channels, bits, RATE, wav_pcm(samples, bits), flags=32, peak=peak, has_crc=options_of(spec).get("crc", True),
                                    padded=options_of(spec).get("padded", True), forced=options_of(spec).get("forced"))
    else:
        data, frames, counts = legacy.encode(samples, version, level, channels, bits, RATE, wav_pcm(samples, bits), flags=32, peak=peak)
    return data, canonical_pcm(samples, bits)


def old_manifest(data, name, pcm):
    a = m.parse_old(data)
    samples = (a.total_frames - 1) * a.blocks_per_frame + a.final_blocks
    frames = m.old_frame_entries(data, a)
    bits = sum((f["end"] - f["start"]) * 8 for f in a.frames)
    n = len(a.frames)
    is_decoded = not (a.version >= 3900 and not a.has_crc)
    checksum = "frame CRC-32" if a.has_crc else "frame checksum"

    # The SDK's bit array of 3.90 to 3.92 reads no checksum where the CRC flag is absent, so the frames store none it can read.
    reads = a.has_crc or a.version < 3900

    def tally(checked):
        return [{"name": checksum, "covers": "decodedAudio", "coversAllAudio": reads, "stored": n if reads else 0, "checked": checked, "failed": 0}]

    result = {
        "length": len(data),
        "file_version": a.version,
        "seek_entries": a.seek_entries,
        "frames": frames,
        "stream_infos": [m.old_stream_info(a, bits, samples)],
        "decoded_samples": samples if is_decoded else 0,
        "pcm_samples": samples if is_decoded else 0,
        "verification": {
            "default": {"isVerified": None, "decoding": "notRequested", "checksums": tally(0)},
            "verify": {"isVerified": True, "decoding": "decoded", "checksums": tally(n)} if is_decoded
            else {"isVerified": None, "decoding": "notPossible", "checksums": tally(0)},
        },
        "decode_findings": [] if is_decoded else [{"code": "decode.not-possible", "count": 1}],
        "regions": [{"name": "Monkey's Audio", "offset": 0, "length": len(data)}],
        "encoder": {"tool": TOOL_OLD if a.version < 3930 else TOOL},
        "findings": [{"code": "monkeys-audio.old-format", "contains": ""}],
        **({"decoded_pcm_md5": hashlib.md5(pcm).hexdigest()} if is_decoded else {}),
        "crosscheck": {"mac_verify": True if is_decoded else None, "samples": samples},
        "header": {"flags": a.flags},
    }
    return result


def new_manifest(data, pcm):
    a = m.parse(data)
    samples = (a.total_frames - 1) * a.blocks_per_frame + a.final_blocks
    decode = {"status": "decoded", "checked": len(a.frames), "failed": 0, "samples": samples, "pcm_samples": samples, "findings": []}
    result = m.manifest(data, a, m.regions_of(data, a), {"tool": TOOL}, [], {"md5": hashlib.md5(pcm).hexdigest()}, decode, stored_crcs=len(a.frames))
    result["crosscheck"] = {"mac_verify": True, "samples": samples}
    return result


def manifest_path(path):
    return path.with_name(path.name[:-len(".ape")] + ".expected.json")


def main():
    only_manifests = "--manifests-only" in sys.argv
    chosen = [a for a in sys.argv[1:] if not a.startswith("--")]
    OUT.mkdir(parents=True, exist_ok=True)
    count = 0
    for name, spec in FIXTURES.items():
        if chosen and name not in chosen:
            continue
        path = OUT / f"{name}.ape"
        samples = samples_of(spec)
        pcm = canonical_pcm(samples, spec[3])
        if not only_manifests:
            data, _ = encode(name)
            path.write_bytes(data)
        elif not path.exists():
            raise SystemExit(f"No fixture for {name}; run the script without --manifests-only.")
        data = path.read_bytes()
        manifest = new_manifest(data, pcm) if spec[0] >= 3980 else old_manifest(data, name, pcm)
        manifest_path(path).write_text(m.dump(manifest), encoding="utf-8")
        count += 1
    print(f"Wrote {count} {'manifests' if only_manifests else 'fixtures'} to {OUT}")


if __name__ == "__main__":
    main()
