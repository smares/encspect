#!/usr/bin/env python3
"""The two Python steps of make_macos_aac_fixtures.sh, which use only the standard library of the
runner's python3 (3.9 or later):

    macos_aac_support.py signals <out directory>
    macos_aac_support.py manifest <out directory> <encodes.tsv> <environment.txt>

- "signals" writes the source PCM into <out>/pcm, copies the other encoders' fixtures that the packet copy (-d 0) takes into
  <out>/foreign, and writes their description to <out>/sources.json. The
  samples are those of wma_signals.py ("tones" and "quiet"), so every encoder in the project is
  fed the same audio. Files with more than two channels or more than 16 bits are
  WAVE_FORMAT_EXTENSIBLE with a channel mask, which is how afconvert learns a 5.1 layout; two 5.1
  files are CAFs with an explicit channel layout tag (MPEG_5_1_A and MPEG_5_1_D); the stereo files with a name ending
  in "h" run 1 s, for HE-AAC's 2,048-sample frames; one stereo file carries a
  LIST/INFO chunk, to see whether afconvert passes it on as iTunes-style metadata.
- "manifest" turns the tab-separated record of every afconvert run into <out>/manifest.json, with
  the size and SHA-256 of each output and of each source.
"""

import hashlib
import json
import pathlib
import struct
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
import wma_signals  # noqa: E402

SECONDS = 0.5  # enough for 20 ADTS frames of AAC-LC at 44.1 and 48 kHz (21 and 23, with the encoder delay); HE-AAC frames hold 2,048 samples, so its sources run longer
HE_SECONDS = 1.0
SPEAKERS_5_1 = 0x3F  # front left and right, center, LFE, back left and right: WAV's 5.1 order
MPEG_5_1_A = (121 << 16) | 6  # kAudioChannelLayoutTag_MPEG_5_1_A: L R C LFE Ls Rs, the order of the WAV's channels
MPEG_5_1_D = (124 << 16) | 6  # kAudioChannelLayoutTag_MPEG_5_1_D: C L R Ls Rs LFE, AAC's own order (channel configuration 6); afinfo names the tag it reads
INFO = [("INAM", "Encspect test tone"), ("IART", "Encspect"), ("IPRD", "Encspect fixtures"), ("ICMT", "afconvert metadata test"), ("ICRD", "2026"), ("IGNR", "Test")]

# name, channels, rate, bits, kind, extras
# Fixtures of this repository that Apple's encoder did not write, for afconvert's packet copy (-d 0): name, path under tests/fixtures.
FOREIGN = [
    ("ffmpeg-adts", "aac/ffmpeg-adts.aac"),
    ("ffmpeg-m4a", "aac/ffmpeg-lc.m4a"),
    ("faac-m4a", "aac/faac-1.40.m4a"),
    ("fdkaac-m4a", "aac/fdkaac-lc.m4a"),
    ("fdkaac-he-m4a", "aac/fdkaac-he.m4a"),
    ("fdkaac-he-adts", "aac/fdkaac-he-adts.aac"),
]

SOURCES = [
    ("mono44", 1, 44100, 16, "tones", {}),
    ("mono48", 1, 48000, 16, "tones", {}),
    ("mono22", 1, 22050, 16, "tones", {}),
    ("st44", 2, 44100, 16, "tones", {}),
    ("mono44h", 1, 44100, 16, "tones", {"seconds": HE_SECONDS}),
    ("st44h", 2, 44100, 16, "tones", {"seconds": HE_SECONDS}),
    ("st48h", 2, 48000, 16, "tones", {"seconds": HE_SECONDS}),
    ("st48", 2, 48000, 16, "tones", {}),
    ("st22", 2, 22050, 16, "tones", {}),
    ("st44tag", 2, 44100, 16, "tones", {"info": INFO}),
    ("six48", 6, 48000, 16, "tones", {"mask": SPEAKERS_5_1}),
    ("six48caf", 6, 48000, 16, "tones", {"caf_layout": MPEG_5_1_A}),
    ("six48d", 6, 48000, 16, "tones", {"caf_layout": MPEG_5_1_D}),
    ("st48f32", 2, 48000, 32, "tones", {"float": True}),
    ("mono48f32", 1, 48000, 32, "tones", {"float": True}),
    ("st44q16", 2, 44100, 16, "quiet", {}),
    ("st44q24", 2, 44100, 24, "quiet", {}),
    ("six48q16", 6, 48000, 16, "quiet", {"mask": SPEAKERS_5_1}),
]


def pcm_bytes(channels, rate, bits, kind, floating=False, seconds=SECONDS):
    if floating:
        # 32-bit IEEE float, the 16-bit signal scaled to +-1
        return b"".join(struct.pack("<f", v / 32768.0) for v in wma_signals.samples(rate, channels, 16, seconds, kind))
    pack = (lambda v: struct.pack("<h", v)) if bits == 16 else (lambda v: struct.pack("<i", v)[:3])
    return b"".join(pack(v) for v in wma_signals.samples(rate, channels, bits, seconds, kind))


def chunk(tag, body):
    return tag + struct.pack("<I", len(body)) + (body + b"\0" if len(body) % 2 else body)


def wav(channels, rate, bits, data, mask=None, info=None, floating=False):
    block = channels * bits // 8
    if floating:
        fmt = struct.pack("<HHIIHH", 3, channels, rate, rate * block, block, bits)
    elif channels > 2 or bits > 16:
        # WAVE_FORMAT_EXTENSIBLE: 22 bytes of extension, the mask, and the PCM sub-format GUID.
        mask = mask if mask is not None else {1: 4, 2: 3}.get(channels, 0)
        fmt = struct.pack("<HHIIHH", 0xFFFE, channels, rate, rate * block, block, bits)
        fmt += struct.pack("<HHI", 22, bits, mask) + bytes.fromhex("0100000000001000800000aa00389b71")
    else:
        fmt = struct.pack("<HHIIHH", 1, channels, rate, rate * block, block, bits)
    body = b"WAVE" + chunk(b"fmt ", fmt)
    if info:
        body += chunk(b"LIST", b"INFO" + b"".join(chunk(key.encode(), value.encode() + b"\0") for key, value in info))
    body += chunk(b"data", data)
    return b"RIFF" + struct.pack("<I", len(body)) + body


def caf(channels, rate, bits, data, layout):
    def block(kind, body):
        return kind + struct.pack(">q", len(body)) + body
    # 'desc': little-endian linear PCM (flag 2), one frame per packet; 'chan': a layout tag, no bitmap, no descriptions.
    desc = struct.pack(">d", float(rate)) + b"lpcm" + struct.pack(">IIIII", 2, channels * bits // 8, 1, channels, bits)
    return b"caff" + struct.pack(">HH", 1, 0) + block(b"desc", desc) + block(b"chan", struct.pack(">III", layout, 0, 0)) + block(b"data", struct.pack(">I", 0) + data)


def sha256(path):
    return hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest()


def signals(out):
    pcm = out / "pcm"
    pcm.mkdir(parents=True, exist_ok=True)
    records = []
    for name, channels, rate, bits, kind, extras in SOURCES:
        seconds = extras.get("seconds", SECONDS)
        data = pcm_bytes(channels, rate, bits, kind, extras.get("float", False), seconds)
        if "caf_layout" in extras:
            path, content = pcm / f"{name}.caf", caf(channels, rate, bits, data, extras["caf_layout"])
        else:
            path, content = pcm / f"{name}.wav", wav(channels, rate, bits, data, extras.get("mask"), extras.get("info"), extras.get("float", False))
        path.write_bytes(content)
        records.append({
            "name": name, "file": f"pcm/{path.name}", "channels": channels, "rate": rate, "bits": bits, "seconds": seconds, "signal": kind,
            "generator": "wma_signals.py", "channel_mask": extras.get("mask"),
            "channel_layout_tag": extras.get("caf_layout"), "info_tags": dict(extras["info"]) if "info" in extras else None,
            "bytes": len(content), "sha256": sha256(path),
        })
    foreign = out / "foreign"
    foreign.mkdir(parents=True, exist_ok=True)
    fixtures = pathlib.Path(__file__).resolve().parents[3] / "tests" / "fixtures"
    for name, relative in FOREIGN:
        source = fixtures / relative
        target = foreign / f"{name}{source.suffix}"
        target.write_bytes(source.read_bytes())
        records.append({"name": name, "file": f"foreign/{target.name}", "foreign": True, "from": f"tests/fixtures/{relative}", "bytes": target.stat().st_size, "sha256": sha256(target)})
    (out / "sources.json").write_text(json.dumps(records, indent=1) + "\n", encoding="utf-8")
    print(f"{len(records)} source files written to {out}")


COLUMNS = ["name", "source", "file_type", "format", "strategy", "flags", "exit_status", "output", "command", "log", "required"]


def manifest(out, tsv, environment):
    sources = {s["name"]: s for s in json.loads((out / "sources.json").read_text(encoding="utf-8"))}
    encodes = []
    for line in pathlib.Path(tsv).read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        row = dict(zip(COLUMNS, line.split("\t")))
        row["exit_status"] = int(row["exit_status"])
        row["required"] = row["required"] == "1"
        output = out / row["output"]
        row["bytes"] = output.stat().st_size if output.is_file() else None
        row["sha256"] = sha256(output) if output.is_file() and row["bytes"] else None
        info = out / "info" / (row["name"] + ".afinfo.txt")
        row["afinfo"] = f"info/{info.name}" if info.is_file() else None
        row["source_sha256"] = sources[row["source"]]["sha256"] if row["source"] in sources else None
        encodes.append(row)
    record = {
        "schema": 1,
        "tool": "afconvert",
        "environment": pathlib.Path(environment).read_text(encoding="utf-8", errors="replace"),
        "sources": list(sources.values()),
        "encodes": encodes,
    }
    (out / "manifest.json").write_text(json.dumps(record, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    ok = sum(1 for e in encodes if e["exit_status"] == 0 and e["bytes"])
    print(f"manifest.json: {len(encodes)} encodes, {ok} produced a file")


def main(argv):
    if len(argv) == 3 and argv[1] == "signals":
        signals(pathlib.Path(argv[2]))
    elif len(argv) == 5 and argv[1] == "manifest":
        manifest(pathlib.Path(argv[2]), argv[3], argv[4])
    else:
        raise SystemExit(__doc__)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
