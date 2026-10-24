#!/usr/bin/env python3
"""Writes the Monkey's Audio fixtures of versions 3.93 to 3.98 in tests/fixtures/monkeys-audio (legacy-*.ape).

The SDK 13.42 decodes these versions and cannot write them, and the public files of them are few (the FFmpeg sample archive has one of 3.97 that this
repository may not hold; docs/formats/monkeys-audio.md, "Decoding"). monkeys_audio_legacy.py is an encoder written as the inverse of the SDK's decoder, and
every file it writes is held to the SDK, not to the encoder: crosscheck_monkeys_audio_fixtures.py runs `mac -V`, which decodes each frame with the SDK's own
decoder of the version and compares its CRC, and `mac -d`, whose samples must have the MD5 of the signal that went in. FFmpeg decodes them too.

The cases cover what the versions change: the predictor of 3.93 and 3.94 (levels fast to extra high), the predictors of 3.95 to 3.97 with the old delta
(every level, insane included), 3.98 with its descriptor and the new delta, 8, 16 and 24 bits, mono and stereo, frames flagged silent and pseudo stereo,
loud noise (the escape of the range coder), several frames, and the old header's optional peak level.

The files are committed, so --manifests-only recomputes the manifests without running the encoder (which takes minutes at the highest levels).
"""

import hashlib
import json
import pathlib
import sys

import monkeys_audio_legacy as legacy
import monkeys_audio_model as m
import monkeys_audio_signal as sig

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "monkeys-audio"
RATE = 44100
TOOL = "monkeys_audio_legacy.py, an encoder of the coding of versions 3.93 to 3.98 that mac -V verifies"
FRAME_95 = 73728 * 4

# name: (version, level, channels, bits, signal, samples, peak level). A signal is a kind of monkeys_audio_signal.py, or (silent samples, kind) for silence
# followed by that kind.
FIXTURES = {
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


def samples_of(spec):
    _version, _level, channels, bits, signal, count, _peak = spec
    if isinstance(signal, tuple):
        silent, kind = signal
        return [[0] * channels for _ in range(silent)] + list(sig.samples(kind, RATE, channels, count, bits))
    return list(sig.samples(signal, RATE, channels, count, bits))


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
    version, level, channels, bits, _signal, _count, peak = spec
    samples = samples_of(spec)
    data, frames, counts = legacy.encode(samples, version, level, channels, bits, RATE, wav_pcm(samples, bits), flags=32, peak=peak)
    return data, canonical_pcm(samples, bits)


def old_manifest(data, name, pcm):
    a = m.parse_old(data)
    samples = (a.total_frames - 1) * a.blocks_per_frame + a.final_blocks
    frames = m.old_frame_entries(data, a)
    bits = sum((f["end"] - f["start"]) * 8 for f in a.frames)
    n = len(a.frames)

    def tally(checked):
        return [{"name": "frame CRC-32", "covers": "decodedAudio", "coversAllAudio": True, "stored": n, "checked": checked, "failed": 0}]

    return {
        "length": len(data),
        "file_version": a.version,
        "seek_entries": a.seek_entries,
        "frames": frames,
        "stream_infos": [m.old_stream_info(a, bits, samples)],
        "decoded_samples": samples,
        "pcm_samples": samples,
        "verification": {
            "default": {"isVerified": None, "decoding": "notRequested", "checksums": tally(0)},
            "verify": {"isVerified": True, "decoding": "decoded", "checksums": tally(n)},
        },
        "decode_findings": [],
        "regions": [{"name": "Monkey's Audio", "offset": 0, "length": len(data)}],
        "encoder": {"tool": TOOL},
        "findings": [{"code": "monkeys-audio.old-format", "contains": ""}],
        "decoded_pcm_md5": hashlib.md5(pcm).hexdigest(),
        "crosscheck": {"mac_verify": True, "samples": samples},
        "header": {"flags": a.flags},
    }


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
