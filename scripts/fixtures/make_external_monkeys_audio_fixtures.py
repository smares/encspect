#!/usr/bin/env python3
"""Writes the encoder-made Monkey's Audio fixtures in tests/fixtures/monkeys-audio (external-*.ape).

They come from mac, the console tool of the Monkey's Audio SDK 13.44, built from the pinned source (monkeys_audio_tool.py): every compression level
(fast to insane), 8, 16, 24 and 32-bit integer and 32-bit floating point audio, 1 to 9 channels with the channel masks of the stored WAV header, several sample
rates, frames of every size the levels use (a file with a full frame of the highest level holds 1,179,648 samples), digital silence and identical channels (which the encoder stores as special
frames), a silent channel, WAV headers with chunks before and after the audio, an AIFF, a BW64 and a Wave64 source, and an APEv2 and an ID3v1 tag, and the seven-channel file once more as SDK 13.42 wrote it. The inputs are
deterministic signals written by monkeys_audio_signal.py.

The encoder's output depends on its version, so the files are committed and --manifests-only recomputes the manifests from them without the encoder.
Each manifest is worked out by monkeys_audio_model.py from the file, with the audio that the decoder must give back (hashed from the signal that went into
the encoder, not from any decoder) and the findings the analyzer must report. crosscheck_monkeys_audio_fixtures.py confirms them with mac, ffmpeg and
MediaInfo.
"""

import hashlib
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile

import monkeys_audio_model as m
import monkeys_audio_signal as sig
import monkeys_audio_tool

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "monkeys-audio"

SHORT = 24000
MASKED = 8000
LIST_BEFORE = sig.chunk(b"LIST", b"INFOISFT" + (14).to_bytes(4, "little") + b"Lavf60.16.100\0" + b"\0")
LIST_AFTER = sig.chunk(b"LIST", b"INFOIART" + (6).to_bytes(4, "little") + b"Name\0\0")
TAGS = "Artist=Fixture artist|Title=Fixture title|Album=Fixture album"

# name: (input, mac arguments). An input is (kind, signal, rate, channels, bits, samples, options): kind is wav, bw64, w64, float, aiff or caf.
FIXTURES = {
    "level-fast-16-stereo": (("wav", "music", 44100, 2, 16, SHORT), ["-c1000"]),
    "level-normal-16-stereo": (("wav", "music", 44100, 2, 16, SHORT), ["-c2000"]),
    "level-high-16-stereo": (("wav", "music", 44100, 2, 16, SHORT), ["-c3000"]),
    "level-extra-high-16-stereo": (("wav", "music", 44100, 2, 16, SHORT), ["-c4000"]),
    "level-insane-16-stereo": (("wav", "music", 44100, 2, 16, SHORT), ["-c5000"]),
    "frames-fast-16-mono": (("wav", "music", 44100, 1, 16, 160000), ["-c1000"]),
    "frames-normal-16-mono": (("wav", "music", 44100, 1, 16, 160000), ["-c2000"]),
    "frames-high-16-mono": (("wav", "music", 44100, 1, 16, 160000), ["-c3000"]),
    "frames-extra-high-16-mono": (("wav", "quiet", 44100, 1, 16, 700000), ["-c4000"]),
    "frames-insane-16-mono": (("wav", "quiet", 44100, 1, 16, 1300000), ["-c5000"]),
    "fast-24-stereo": (("wav", "music", 44100, 2, 24, SHORT), ["-c1000"]),
    "normal-8-stereo": (("wav", "music", 44100, 2, 8, SHORT), ["-c2000"]),
    "normal-8-mono": (("wav", "music", 44100, 1, 8, SHORT), ["-c2000"]),
    "normal-16-mono": (("wav", "music", 44100, 1, 16, SHORT), ["-c2000"]),
    "normal-24-stereo": (("wav", "music", 44100, 2, 24, SHORT), ["-c2000"]),
    "loud-24-stereo": (("wav", "loud-sine-noise", 44100, 2, 24, 8000), ["-c2000"]),
    "full-frame-insane-24-mono": (("wav", "quiet", 44100, 1, 24, 1179648), ["-c5000"]),
    "normal-32-stereo": (("wav", "music", 44100, 2, 32, SHORT), ["-c2000"]),
    "normal-float-stereo": (("float", "music", 44100, 2, 32, SHORT), ["-c2000"]),
    "extra-high-32-stereo": (("wav", "music", 44100, 2, 32, SHORT), ["-c4000"]),
    "insane-24-stereo": (("wav", "music", 44100, 2, 24, SHORT), ["-c5000"]),
    "insane-32-stereo": (("wav", "music", 44100, 2, 32, SHORT), ["-c5000"]),
    "insane-8-mono": (("wav", "music", 44100, 1, 8, SHORT), ["-c5000"]),
    "normal-16-3ch": (("wav", "music", 48000, 3, 16, SHORT), ["-c2000"]),
    "normal-16-4ch": (("wav", "music", 48000, 4, 16, SHORT, 0x33), ["-c2000"]),
    "normal-16-5.1": (("wav", "music", 48000, 6, 16, SHORT, 0x3F), ["-c2000"]),
    "high-16-5.1": (("wav", "music", 48000, 6, 16, SHORT, 0x3F), ["-c3000"]),
    "normal-16-7ch": (("wav", "music", 48000, 7, 16, 12000, 0x13F), ["-c2000"]),
    # The same file from SDK 13.42, whose encoder never stored the seventh channel (13.44 does): the file fails its frame CRC-32, as 13.44's mac -V says.
    "normal-16-7ch-sdk-1342": (("wav", "music", 48000, 7, 16, 12000, 0x13F), ["-c2000"]),
    # SDK 13.44 codes the seventh channel of seven-channel files at every depth; the 24-bit branch of Prepare.cpp has its own loop over the remaining channels.
    "normal-24-7ch": (("wav", "music", 48000, 7, 24, 6000, 0x13F), ["-c2000"]),
    # Files of 8 bits and more than two channels keep every channel as a plain sample (Prepare.cpp pairs none of them).
    "normal-8-6ch": (("wav", "music", 48000, 6, 8, MASKED, 0x3F), ["-c2000"]),
    "normal-24-7.1": (("wav", "music", 48000, 8, 24, 12000, 0x63F), ["-c2000"]),
    "normal-16-9ch": (("wav", "music", 48000, 9, 16, 12000, 0), ["-c2000"]),
    # The channel mask of the stored WAV header sets the layout: 5.1 with the surround pair at the sides, a mask with fewer bits than channels, one with
    # more, mask 0, and a six-channel header of format tag 1, which has no mask. (The masks 0x33, 0x3F and 0x63F are in normal-16-4ch, normal-16-5.1 and
    # normal-24-7.1, 0x13F in normal-16-7ch and mask 0 with nine channels in normal-16-9ch.)
    "mask-5.1-sides": (("wav", "music", 48000, 6, 16, MASKED, 0x60F), ["-c2000"]),
    "mask-fewer-bits-than-channels": (("wav", "music", 48000, 6, 16, MASKED, 0x7), ["-c2000"]),
    "mask-more-bits-than-channels": (("wav", "music", 48000, 4, 16, MASKED, 0x3F), ["-c2000"]),
    "mask-zero-6ch": (("wav", "music", 48000, 6, 16, MASKED, 0), ["-c2000"]),
    "plain-format-6ch": (("wav", "music", 48000, 6, 16, MASKED, "plain"), ["-c2000"]),
    # The other WAVE-like headers the SDK stores (WAVInputSource.cpp): BW64 with its ds64 chunk (the data chunk's size is 0xFFFFFFFF, the ds64 chunk has it) and
    # Sony Wave64 (APE_FORMAT_FLAG_W64), both with an extensible format and a mask.
    "bw64-5.1-sides": (("bw64", "music", 48000, 6, 16, MASKED, 0x60F), ["-c2000"]),
    "w64-5.1-back": (("w64", "music", 48000, 6, 16, MASKED, 0x3F), ["-c2000"]),
    "rate-8000-mono-8": (("wav", "music", 8000, 1, 8, 8000), ["-c2000"]),
    "rate-12345-stereo": (("wav", "music", 12345, 2, 16, 12000), ["-c2000"]),
    "rate-96000-mono-24": (("wav", "music", 96000, 1, 24, SHORT), ["-c2000"]),
    "rate-192000-mono-24": (("wav", "music", 192000, 1, 24, SHORT), ["-c2000"]),
    "silent-stereo": (("wav", "silent", 44100, 2, 16, 160000), ["-c2000"]),
    "silent-mono": (("wav", "silent", 44100, 1, 16, 160000), ["-c2000"]),
    "silent-8-stereo": (("wav", "silent", 44100, 2, 8, 160000), ["-c2000"]),
    "silent-24-stereo": (("wav", "silent", 44100, 2, 24, 160000), ["-c2000"]),
    "silent-then-tone-stereo": (("wav", "silent-then-tone", 44100, 2, 16, 160000), ["-c2000"]),
    "silent-then-tone-insane": (("wav", "silent-then-tone", 44100, 2, 16, 160000), ["-c5000"]),
    "pseudo-stereo": (("wav", "pseudo", 44100, 2, 16, SHORT), ["-c2000"]),
    "pseudo-stereo-insane": (("wav", "pseudo", 44100, 2, 16, SHORT), ["-c5000"]),
    "quiet-insane": (("wav", "quiet", 44100, 2, 16, SHORT), ["-c5000"]),
    "left-silent": (("wav", "left-silent", 44100, 2, 16, SHORT), ["-c2000"]),
    "loud-noise": (("wav", "loud", 44100, 2, 16, SHORT), ["-c2000"]),
    "wav-chunks": (("wav", "music", 44100, 2, 16, SHORT, None, LIST_BEFORE, LIST_AFTER), ["-c2000"]),
    "from-aiff": (("aiff", "music", 44100, 2, 16, SHORT), ["-c2000"]),
    "apev2-tag": (("wav", "music", 44100, 2, 16, SHORT), ["-c2000", "-t", TAGS]),
    "id3v1-tag": (("wav", "music", 44100, 2, 16, SHORT), ["-c2000", "-t", TAGS, "TAGONLY-L"]),
}

TOOL = f"mac, the console tool of the Monkey's Audio SDK {monkeys_audio_tool.VERSION}, built from {monkeys_audio_tool.NAME}"
TOOL_1342 = f"mac, the console tool of the Monkey's Audio SDK {monkeys_audio_tool.VERSION_1342}, built from {monkeys_audio_tool.NAME_1342}"
# Fixtures written by SDK 13.42, which (as every SDK before 13.44) did not code the seventh channel of a file of seven channels.
FROM_1342 = {"normal-16-7ch-sdk-1342"}


def make_input(spec, work):
    kind, signal, rate, channels, bits, count, *rest = spec
    if kind == "wav":
        mask = rest[0] if rest else None
        before, after = (rest[1], rest[2]) if len(rest) > 2 else (b"", b"")
        return work / "in.wav", sig.wav(signal, rate, channels, count, bits, mask, False, before, after)
    if kind in ("bw64", "w64"):
        return work / ("in.wav" if kind == "bw64" else "in.w64"), (sig.bw64 if kind == "bw64" else sig.w64)(signal, rate, channels, count, bits, rest[0])
    if kind == "float":
        return work / "in.wav", sig.wav(signal, rate, channels, count, 32, None, True)
    if kind == "aiff":
        return work / "in.aif", sig.aiff(signal, rate, channels, count, bits)
    return work / "in.caf", sig.caf(signal, rate, channels, count, bits, rest[0])


def pcm_of(spec):
    kind, signal, rate, channels, bits, count, *rest = spec
    return sig.canonical(signal, rate, channels, count, bits, kind == "float")


def describe(spec):
    kind, signal, rate, channels, bits, count, *rest = spec
    return {"kind": kind, "signal": signal, "rate": rate, "channels": channels, "bits": bits, "samples": count}


def encode(name, spec, arguments, work):
    mac = MAC_1342 if name in FROM_1342 else MAC
    for old in OUT.glob(f"external-{name}.*"):
        old.unlink()
    path, data = make_input(spec, work)
    path.write_bytes(data)
    target = work / "out.ape"
    target.unlink(missing_ok=True)
    plain = [a for a in arguments if a != "TAGONLY-L"]
    code, text = monkeys_audio_tool.run(mac, path, target, *plain)
    if code != 0 or not target.exists():
        raise SystemExit(f"mac failed for {name}: {text.strip().splitlines()[-1] if text.strip() else code}")
    if "TAGONLY-L" in arguments:
        # -L turns the file's APEv2 tag into an ID3v1 tag.
        code, text = monkeys_audio_tool.run(MAC, target, "-L")
        if code != 0:
            raise SystemExit(f"mac -L failed for {name}: {text}")
    shutil.copy(target, OUT / f"external-{name}.ape")
    return {"tool": TOOL_1342 if name in FROM_1342 else TOOL, "arguments": arguments, "input": describe(spec)}


def decode_outcome(a, data, spec, old=False):
    """What decoding the file does: version 3.99 files decode every frame and the CRC of each holds, except in the seven-channel case of SDK 13.42 (old),
    like every SDK before 13.44, which did not code the seventh channel (13.44's tool reports error 1009 for such a file)."""
    kind, signal, rate, channels, bits, count, *rest = spec
    frames = len(a.frames)
    failed = 1 if old and channels == 7 else 0
    return {"status": "decoded", "checked": frames, "failed": failed, "samples": count,
            "findings": [{"code": "monkeys-audio.frame-crc-mismatch", "count": 1}] if failed else []}


def build_manifest(path, encoder, spec, findings):
    old = path.name.removeprefix("external-").removesuffix(".ape") in FROM_1342
    data = path.read_bytes()
    a = m.parse(data)
    tag = None
    kind = None
    if data[-32:-24] == b"APETAGEX":
        # The tag mac writes has a footer and no header; the footer's size counts the items and the footer.
        tag = len(data) - m.u32(data, len(data) - 32 + 12)
        kind = "APE"
    elif data[-128:-125] == b"TAG":
        tag = len(data) - 128
        kind = "ID3v1"
    regions = [{"name": "Monkey's Audio", "offset": a.junk, "length": a.data_end - a.junk}]
    if a.terminating_bytes:
        regions.append({"name": "WAV Terminating Data", "offset": a.data_end, "length": a.terminating_bytes})
    if tag is not None:
        regions.append({"name": kind, "offset": tag, "length": len(data) - tag})
    channels = spec[3]
    pcm = None if old else {"md5": hashlib.md5(pcm_of(spec)).hexdigest()}
    result = m.manifest(data, a, regions, encoder, findings, pcm, decode_outcome(a, data, spec, old), stored_crcs=len(a.frames))
    result["crosscheck"] = {"mac_verify": not old, "samples": spec[5]}
    return result


def manifest_path(path):
    return path.with_name(path.name.rsplit(".", 1)[0] + ".expected.json")


def findings_of(name):
    if name == "apev2-tag":
        return [{"code": "ape.footer-flags", "contains": "contains-no-footer"}]
    return []


def main():
    global MAC, MAC_1342
    only_manifests = "--manifests-only" in sys.argv
    chosen = [a for a in sys.argv[1:] if not a.startswith("--")]
    OUT.mkdir(parents=True, exist_ok=True)
    MAC = MAC_1342 = None
    if not only_manifests:
        MAC = monkeys_audio_tool.build_mac(pathlib.Path(tempfile.gettempdir()) / "monkeys-audio-sdk")
        if any(name in FROM_1342 for name in FIXTURES if not chosen or name in chosen):
            MAC_1342 = monkeys_audio_tool.build_mac_1342(pathlib.Path(tempfile.gettempdir()) / "monkeys-audio-sdk-1342")
    count = 0
    with tempfile.TemporaryDirectory() as work:
        for name, (spec, arguments) in FIXTURES.items():
            path = OUT / f"external-{name}.ape"
            if only_manifests or (chosen and name not in chosen):
                if not path.exists() or not manifest_path(path).exists():
                    raise SystemExit(f"No fixture or manifest for {name}; run the script without --manifests-only.")
                encoder = json.loads(manifest_path(path).read_text(encoding="utf-8"))["encoder"]
            else:
                encoder = encode(name, spec, arguments, pathlib.Path(work))
            manifest_path(path).write_text(m.dump(build_manifest(path, encoder, spec, findings_of(name))), encoding="utf-8")
            count += 1
    print(f"Wrote {count} {'manifests' if only_manifests else 'fixtures'} to {OUT}")


if __name__ == "__main__":
    main()
