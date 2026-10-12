#!/usr/bin/env python3
"""Writes the encoder-made Vorbis and Opus fixtures in tests/fixtures/ogg (external-*.ogg, .opus).

oggenc from Ubuntu's vorbis-tools (1.4.2, libvorbis 1.3.7) encodes at several qualities, with
managed bitrate, in mono, stereo and 5.1, and also with aoTuV's libraries of beta 5.7, beta 6.03 and
its 2020 rebuild in their place (aotuv_tool.py); Rarewares' oggenc2.88 builds on aoTuV b6.03 (2020)
and on Lancer, run under Wine, write their own vendor strings; opusenc from opus-tools (0.2, libopus 1.4) at several
bitrates, in hard CBR and constrained VBR, mono, stereo and 5.1, with speech tuning and other
frame sizes; ffmpeg encodes with libvorbis and libopus (also in the ambisonic mapping family 2)
and with its native vorbis and opus encoders. The inputs are deterministic signals written here:
two tones under a slow tremolo with a click train and a quarter second of silence, so block
switching, VBR and the encoders' speech and music decisions have something to do.

The encoders pick random serial numbers, so the bytes depend on the run; the files are committed,
and --manifests-only recomputes the manifests from them without the encoders. Each manifest has
the parts make_vorbis_opus_fixtures.py describes, worked out by vorbis_opus_model.py from the
file's pages, plus the encoder and its settings and the encoder the analyzer must name.
crosscheck_ogg_fixtures.py confirms them with oggz, ogginfo, opusinfo, ffprobe, oggdec and opusdec.
"""

import json
import math
import pathlib
import shutil
import struct
import subprocess
import sys
import tempfile

import aotuv_tool
import make_ogg_fixtures as hand
import make_vorbis_opus_fixtures as hand_vo
import ogg_model as m

OUT = hand.OUT

LIBVORBIS = {"name": "libvorbis 1.3.7", "confidence": "High"}
OPUSENC = {"name": "opusenc 0.2 (libopus 1.4)", "confidence": "High"}
FFMPEG_LIBVORBIS = {"name": "FFmpeg (Lavc60.31.102 libvorbis)", "confidence": "Medium"}
FFMPEG_VORBIS = {"name": "FFmpeg (Lavc60.31.102 vorbis)", "confidence": "Medium"}
FFMPEG_LIBOPUS = {"name": "FFmpeg (Lavc60.31.102 libopus)", "confidence": "Medium"}
AOTUV_B57 = {"name": "aoTuV b5.7 (20090301)", "confidence": "High"}
AOTUV_B603 = {"name": "aoTuV b6.03 (20110424)", "confidence": "High"}
AOTUV_2020 = {"name": "aoTuV b6.03 (20110424, 2020 build on libvorbis 1.3.7)", "confidence": "High"}
AOTUV_RAREWARES = {"name": "aoTuV b6.03 (2020, Rarewares build)", "confidence": "High"}
LANCER = {"name": "Lancer (SSE2, based on aoTuV 6.03 (2020))", "confidence": "High"}
FFMPEG_OPUS = {"name": "FFmpeg (Lavc60.31.102 opus)", "confidence": "Medium"}

# name: (tool, rate, channels, seconds, arguments, detected encoder, expected findings)
FIXTURES = {
    "external-oggenc-q0": ("oggenc", 44100, 2, 1.5, ["-q", "0"], LIBVORBIS, []),
    "external-oggenc-q5": ("oggenc", 44100, 2, 1.5, ["-q", "5"], LIBVORBIS, []),
    "external-oggenc-q10": ("oggenc", 48000, 2, 1.0, ["-q", "10"], LIBVORBIS, []),
    "external-oggenc-managed": ("oggenc", 44100, 2, 1.5, ["--managed", "-b", "96", "-m", "64", "-M", "128"], LIBVORBIS, []),
    "external-oggenc-mono": ("oggenc", 22050, 1, 1.5, ["-q", "3", "-t", "Mono title", "-a", "Ünïcödé artist"], LIBVORBIS, []),
    "external-oggenc-51": ("oggenc", 48000, 6, 1.0, ["-q", "4"], LIBVORBIS, []),
    # oggenc with aoTuV's libraries (aotuv_tool.py), and Rarewares' oggenc2.88 builds under Wine.
    "external-aotuv-b5.7-q5": ("aotuv:b5.7", 44100, 2, 1.5, ["-q", "5"], AOTUV_B57, []),
    "external-aotuv-b6.03-q5": ("aotuv:b6.03", 44100, 2, 1.5, ["-q", "5"], AOTUV_B603, []),
    "external-aotuv-b6.03-2020-q3": ("aotuv:b6.03 (2020)", 44100, 2, 1.5, ["-q", "3"], AOTUV_2020, []),
    "external-aotuv-b6.03-2020-managed": ("aotuv:b6.03 (2020)", 44100, 2, 1.5, ["--managed", "-b", "96", "-m", "64", "-M", "128"], AOTUV_2020, []),
    "external-oggenc2-aotuv-rarewares": ("wine:aotuv-rarewares", 44100, 2, 1.5, ["-q", "5"], AOTUV_RAREWARES, []),
    "external-oggenc2-lancer-sse2": ("wine:lancer-sse2", 44100, 2, 1.5, ["-q", "5"], LANCER, []),
    "external-ffmpeg-libvorbis": ("ffmpeg", 44100, 2, 1.5, ["-c:a", "libvorbis", "-q:a", "4"], FFMPEG_LIBVORBIS, []),
    "external-ffmpeg-vorbis": ("ffmpeg", 44100, 2, 1.5, ["-strict", "-2", "-c:a", "vorbis"], FFMPEG_VORBIS, []),
    "external-opusenc-96": ("opusenc", 48000, 2, 1.5, ["--bitrate", "96"], OPUSENC, []),
    "external-opusenc-24-mono": ("opusenc", 16000, 1, 1.5, ["--bitrate", "24"], OPUSENC, []),
    "external-opusenc-hard-cbr": ("opusenc", 48000, 2, 1.5, ["--bitrate", "64", "--hard-cbr"], OPUSENC, []),
    "external-opusenc-cvbr": ("opusenc", 44100, 2, 1.5, ["--bitrate", "64", "--cvbr"], OPUSENC, []),
    "external-opusenc-speech": ("opusenc", 16000, 1, 1.5, ["--bitrate", "12", "--speech", "--framesize", "60"], OPUSENC, []),
    "external-opusenc-music-5ms": ("opusenc", 48000, 2, 1.0, ["--bitrate", "128", "--music", "--framesize", "5"], OPUSENC, []),
    "external-opusenc-51": ("opusenc", 48000, 6, 1.0, ["--bitrate", "192"], OPUSENC, []),
    "external-ffmpeg-libopus": ("ffmpeg", 48000, 2, 1.5, ["-c:a", "libopus", "-b:a", "96k"], FFMPEG_LIBOPUS, []),
    "external-ffmpeg-libopus-ambisonics": ("ffmpeg", 48000, 4, 0.5, ["-c:a", "libopus", "-mapping_family", "2"], FFMPEG_LIBOPUS, []),
    "external-ffmpeg-opus": ("ffmpeg", 48000, 2, 1.5, ["-strict", "-2", "-c:a", "opus", "-b:a", "96k"], FFMPEG_OPUS, []),
}


# Checks a tool cannot make on a fixture, with the reason.
SKIPS = {
    "external-ffmpeg-libopus-ambisonics": {"opusdec": "libopusfile 0.12, which opusdec reads with, refuses channel mapping family 2 (\"Failed to open\")"},
}


def extension(name):
    return ".ogg" if FIXTURES[name][0].startswith(("oggenc", "aotuv", "wine")) or "vorbis" in name else ".opus"


def signal(rate, channels, seconds):
    """Tones of 330 Hz and 1250 Hz (shifted per channel) under a 2 Hz tremolo, with a click every
    0.25 s and silence from 0.5 s to 0.75 s."""
    out = bytearray()
    for i in range(int(seconds * rate)):
        t = i / rate
        envelope = 0 if 0.5 <= t < 0.75 else 9000 * (0.6 + 0.4 * math.sin(2 * math.pi * 2 * t))
        click = 12000 if (i % (rate // 4)) < 8 else 0
        for channel in range(channels):
            value = envelope * (math.sin(2 * math.pi * 330 * t + channel) + 0.4 * math.sin(2 * math.pi * (1250 + 150 * channel) * t)) + click
            out += struct.pack("<h", max(-32768, min(32767, int(value))))
    return bytes(out)


def wav(raw, rate, channels):
    return (b"RIFF" + struct.pack("<I", 36 + len(raw)) + b"WAVEfmt " + struct.pack("<IHHIIHH", 16, 1, channels, rate, rate * channels * 2, channels * 2, 16)
            + b"data" + struct.pack("<I", len(raw)) + raw)


def version(tool):
    if tool == "ffmpeg":
        return "ffmpeg " + subprocess.run(["ffmpeg", "-version"], capture_output=True, text=True).stdout.split()[2]
    return subprocess.run([tool, "--version"], capture_output=True, text=True).stdout.splitlines()[0]


def encode(name, tool, rate, channels, seconds, arguments, work):
    target = OUT / (name + extension(name))
    source = work / f"in-{rate}-{channels}-{seconds}.wav"
    source.write_bytes(wav(signal(rate, channels, seconds), rate, channels))
    if tool == "oggenc":
        subprocess.run(["oggenc", "-Q", *arguments, "-o", str(target), str(source)], check=True)
    elif tool.startswith("aotuv:"):
        aotuv_tool.oggenc(tool[6:], arguments, source, target)
        return {"tool": f"{version('oggenc')} with aoTuV {tool[6:]}'s libvorbis (aotuv_tool.py)", "arguments": arguments, "rate": rate,
                "channels": channels, "seconds": seconds}
    elif tool.startswith("wine:"):
        aotuv_tool.windows(tool[5:], arguments, source, target)
        return {"tool": f"{aotuv_tool.WINDOWS[tool[5:]][0]} (Rarewares) under Wine", "arguments": arguments, "rate": rate, "channels": channels,
                "seconds": seconds}
    elif tool == "opusenc":
        subprocess.run(["opusenc", "--quiet", *arguments, str(source), str(target)], check=True)
    else:
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(source), *arguments, str(target)], check=True)
    return {"tool": version(tool), "arguments": arguments, "rate": rate, "channels": channels, "seconds": seconds}


def manifest(name, encoder, detected, findings):
    """The manifest of a well-formed single-stream file, worked out from its pages."""
    data = (OUT / (name + extension(name))).read_bytes()
    pages, end = m.read_pages(data)
    if end != len(data):
        raise SystemExit(f"{name}: the pages end at {end} of {len(data)} bytes")
    serial = pages[0]["serial"]
    records = []
    for p in pages:
        computed = m.ogg_crc(data[p["offset"]:p["offset"] + 22] + bytes(4) + data[p["offset"] + 26:p["offset"] + p["length"]])
        records.append({"name": "Page", "offset": p["offset"], "length": p["length"], "serial": p["serial"], "sequence": p["sequence"],
                        "granule": p["granule"], "flags": p["flags"], "crc_ok": computed == p["crc"]})
    codec = "Vorbis" if data[pages[0]["body"]:pages[0]["body"] + 7] == b"\x01vorbis" else "Opus"
    spec = {"serial": serial, "link": 1, "codec": codec, "analyzed": True, "lost": set(), "extra_headers": 0, "encoder": detected}
    entry, info = hand.expect_stream(data, records, spec)
    model = set(entry.pop("model_findings", [])) - {"vorbis.header-extra-data"}
    unexpected = model - {code for code, _ in findings}
    if unexpected:
        raise SystemExit(f"{name}: the model predicts {sorted(unexpected)}, which the fixture does not list")
    info["encoder"], info["encoder_confidence"] = detected["name"], detected["confidence"]
    return {
        "length": len(data), "regions": [{"name": "Ogg", "offset": 0, "length": len(data)}], "encoder": encoder,
        "pages": records, "streams": [entry], "stream_infos": [info],
        "findings": [{"code": code, "contains": text} for code, text in findings],
        "crosscheck": {"valid": True, "skip": SKIPS[name]} if name in SKIPS else {"valid": True},
    }


def main():
    only_manifests = "--manifests-only" in sys.argv
    if not only_manifests:
        missing = [tool for tool in ("oggenc", "opusenc", "ffmpeg", "wine", "autoreconf") if shutil.which(tool) is None]
        if missing:
            raise SystemExit(f"Missing {', '.join(missing)}; install vorbis-tools, opus-tools, ffmpeg, wine and autoconf, or pass --manifests-only.")
    _ = hand_vo
    with tempfile.TemporaryDirectory() as work:
        for name, (tool, rate, channels, seconds, arguments, detected, findings) in FIXTURES.items():
            path = OUT / (name + ".expected.json")
            if only_manifests:
                encoder = json.loads(path.read_text(encoding="utf-8"))["encoder"]
            else:
                encoder = encode(name, tool, rate, channels, seconds, arguments, pathlib.Path(work))
            result = manifest(name, encoder, detected, findings)
            path.write_text(json.dumps(result, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote {len(FIXTURES)} {'manifests' if only_manifests else 'fixtures'} to {OUT}")


if __name__ == "__main__":
    main()
