#!/usr/bin/env python3
"""Writes the encoder-made Ogg Speex fixtures in tests/fixtures/ogg (external-*.spx).

speexenc from Ubuntu's speex package (1.2.1) encodes narrowband, wideband and ultra-wideband at
several qualities, in VBR, ABR and CBR at a bitrate, stereo, with VAD and DTX, several frames per
packet, with comments and with a Skeleton stream; ffmpeg's libspeex wrapper encodes CBR, VBR and
ABR with several frames per packet, and remuxes a speexenc file with -c copy; and two of them joined
make a chained file. The inputs are deterministic signals of 1.5 s, written here: two tones under a
slow tremolo with half a second of silence in the middle, so VBR, VAD and DTX have something to do.

speexenc picks a random serial number, so the bytes depend on the run as well as on the encoder;
the files are committed, and --manifests-only recomputes the manifests from them without the
encoders. Each manifest has the same parts as a hand-built one (make_ogg_fixtures.py), worked out
by ogg_model.py from the file's pages, plus the encoder and its settings, and the encoder the
analyzer must name ("detected_encoder"). crosscheck_ogg_fixtures.py confirms them with oggz,
ogginfo, ffprobe and speexdec.
"""

import json
import math
import pathlib
import shutil
import struct
import subprocess
import sys
import tempfile

import make_ogg_fixtures as hand
import ogg_model as m

OUT = hand.OUT

SPEEXENC = "speexenc"
FFMPEG = "ffmpeg"

SPEEXENC_NAME = {"name": "speexenc 1.2.1", "confidence": "High"}
FFMPEG_NAME = {"name": "FFmpeg (Lavc60.31.102 libspeex)", "confidence": "Medium"}

# name: (tool, rate, channels, arguments, detected encoder, expected findings)
FIXTURES = {
    "external-speexenc-nb": ("speexenc", 8000, 1, ["-n"], SPEEXENC_NAME, []),
    "external-speexenc-nb-q2": ("speexenc", 8000, 1, ["-n", "--quality", "2"], SPEEXENC_NAME, []),
    "external-speexenc-nb-cbr-bitrate": ("speexenc", 8000, 1, ["-n", "--bitrate", "8000"], SPEEXENC_NAME, []),
    "external-speexenc-nb-vbr-dtx": ("speexenc", 8000, 1, ["-n", "--vbr", "--dtx"], SPEEXENC_NAME, []),
    "external-speexenc-wb": ("speexenc", 16000, 1, ["-w"], SPEEXENC_NAME, []),
    "external-speexenc-wb-q4": ("speexenc", 16000, 1, ["-w", "--quality", "4"], SPEEXENC_NAME, []),
    "external-speexenc-wb-vbr": ("speexenc", 16000, 1, ["-w", "--vbr"], SPEEXENC_NAME, []),
    "external-speexenc-wb-abr": ("speexenc", 16000, 1, ["-w", "--abr", "16000"], SPEEXENC_NAME, []),
    "external-speexenc-wb-vad-dtx": ("speexenc", 16000, 1, ["-w", "--vad", "--dtx"], SPEEXENC_NAME, []),
    "external-speexenc-wb-stereo": ("speexenc", 16000, 2, ["-w", "--stereo"], SPEEXENC_NAME, []),
    "external-speexenc-wb-nframes": ("speexenc", 16000, 1, ["-w", "--nframes", "4"], SPEEXENC_NAME, []),
    "external-speexenc-wb-comments": ("speexenc", 16000, 1, ["-w", "--title", "Fixture title", "--author", "Ünïcödé author",
                                                             "--comment", "DESCRIPTION=Comment with = sign"], SPEEXENC_NAME, []),
    "external-speexenc-wb-skeleton": ("speexenc", 16000, 1, ["-w", "--skeleton"], SPEEXENC_NAME, [("ogg.codec-not-analyzed", "holds Skeleton")]),
    "external-speexenc-uwb": ("speexenc", 32000, 1, ["-u"], SPEEXENC_NAME, []),
    "external-speexenc-uwb-vbr-q10": ("speexenc", 32000, 1, ["-u", "--vbr", "--quality", "10"], SPEEXENC_NAME, []),
    "external-ffmpeg-libspeex-wb": ("ffmpeg", 16000, 1, ["-c:a", "libspeex"], FFMPEG_NAME, []),
    "external-ffmpeg-libspeex-nb-vbr": ("ffmpeg", 8000, 1, ["-c:a", "libspeex", "-q:a", "6"], FFMPEG_NAME, []),
    "external-ffmpeg-libspeex-uwb-abr": ("ffmpeg", 32000, 1, ["-c:a", "libspeex", "-abr", "1", "-b:a", "24k"], FFMPEG_NAME, []),
    "external-ffmpeg-libspeex-frames": ("ffmpeg", 16000, 1, ["-c:a", "libspeex", "-frames_per_packet", "3", "-metadata", "title=ffmpeg title"],
                                        FFMPEG_NAME, []),
    # ffmpeg rewrites the granule positions of a stream it copies without the encoder's lookahead.
    "external-ffmpeg-remux": ("remux", 16000, 1, ["-c", "copy"], {"name": "libspeex 1.2.1", "confidence": "Low"},
                              [("speex.delay-unusual", "an encoder delay of 0 samples")]),
    # Two files joined end to end: speexenc's narrowband stream, then ffmpeg's wideband one.
    "external-chained": ("join", 0, 0, [], [SPEEXENC_NAME, FFMPEG_NAME], []),
}


def signal(rate, channels):
    """1.5 s: tones of 300 Hz and 1100 Hz (1700 Hz on the second channel) under a 3 Hz tremolo,
    silent from 0.5 s to 1 s."""
    out = bytearray()
    for i in range(int(1.5 * rate)):
        t = i / rate
        envelope = 0 if 0.5 <= t < 1.0 else 8000 * (0.5 + 0.5 * math.sin(2 * math.pi * 3 * t))
        for channel in range(channels):
            value = envelope * (math.sin(2 * math.pi * 300 * t) + 0.5 * math.sin(2 * math.pi * (1100 + 600 * channel) * t + 0.3))
            out += struct.pack("<h", max(-32768, min(32767, int(value))))
    return bytes(out)


def version(tool):
    if tool == "speexenc":
        line = subprocess.run([SPEEXENC, "--version"], capture_output=True, text=True).stdout.splitlines()[0]
        return line.split("version ")[1].split(" ")[0]
    return subprocess.run([FFMPEG, "-version"], capture_output=True, text=True).stdout.split()[2]


def encode(name, tool, rate, channels, arguments, work):
    target = OUT / (name + ".spx")
    if tool == "join":
        target.write_bytes((OUT / "external-speexenc-nb.spx").read_bytes() + (OUT / "external-ffmpeg-libspeex-wb.spx").read_bytes())
        return {"tool": "cat", "inputs": ["external-speexenc-nb.spx", "external-ffmpeg-libspeex-wb.spx"]}
    if tool == "remux":
        subprocess.run([FFMPEG, "-v", "error", "-y", "-i", str(OUT / "external-speexenc-wb.spx"), *arguments, str(target)], check=True)
        return {"tool": f"ffmpeg {version('ffmpeg')}", "arguments": arguments, "input": "external-speexenc-wb.spx"}
    raw = work / f"in-{rate}-{channels}.raw"
    raw.write_bytes(signal(rate, channels))
    if tool == "speexenc":
        subprocess.run([SPEEXENC, *arguments, "--rate", str(rate), str(raw), str(target)], check=True, capture_output=True)
        return {"tool": f"speexenc {version('speexenc')}", "arguments": arguments, "rate": rate, "channels": channels}
    subprocess.run([FFMPEG, "-v", "error", "-y", "-f", "s16le", "-ar", str(rate), "-ac", str(channels), "-i", str(raw), *arguments, str(target)],
                   check=True)
    return {"tool": f"ffmpeg {version('ffmpeg')}", "arguments": arguments, "rate": rate, "channels": channels}


def manifest(name, encoder, detected, findings):
    """The manifest of a well-formed file, worked out from its pages."""
    data = (OUT / (name + ".spx")).read_bytes()
    pages, end = m.read_pages(data)
    if end != len(data):
        raise SystemExit(f"{name}: the pages end at {end} of {len(data)} bytes")
    # A beginning-of-stream page after other pages, or after every stream of the link ended, starts
    # the next link of a chain.
    records, links, link, has_data, first, open_streams = [], {}, 1, False, {}, set()
    for p in pages:
        if p["flags"] & m.BOS and (has_data or (first and not open_streams)):
            link, has_data, open_streams = link + 1, False, set()
        has_data |= not p["flags"] & m.BOS
        if p["flags"] & m.BOS:
            open_streams.add(p["serial"])
        if p["flags"] & m.EOS:
            open_streams.discard(p["serial"])
        key = (p["serial"], link if p["flags"] & m.BOS else links.get(p["serial"], link))
        if p["flags"] & m.BOS:
            links[p["serial"]] = link
        computed = m.ogg_crc(data[p["offset"]:p["offset"] + 22] + bytes(4) + data[p["offset"] + 26:p["offset"] + p["length"]])
        record = {"name": "Page", "offset": p["offset"], "length": p["length"], "serial": p["serial"], "sequence": p["sequence"],
                  "granule": p["granule"], "flags": p["flags"], "crc_ok": computed == p["crc"]}
        records.append((key, record))
        first.setdefault(key, p)
    streams, infos = [], []
    named = list(detected) if isinstance(detected, list) else None
    for key in first:
        own = [r for k, r in records if k == key]
        start = data[first[key]["body"]:first[key]["body"] + 8]
        codec = "Speex" if start.startswith(b"Speex   ") else "Skeleton" if start.startswith(b"fishead\0") else "Unknown"
        spec = {"serial": key[0], "link": key[1], "codec": codec, "analyzed": codec == "Speex", "lost": set(), "extra_headers": 0}
        entry, info = hand.expect_stream(data, own, spec)
        name = (named.pop(0) if named else detected) if "speex" in entry else None
        if name:
            entry["speex"]["encoder"] = name
        streams.append(entry)
        if info:
            info["encoder"], info["encoder_confidence"] = name["name"], name["confidence"]
            infos.append(info)
    return {
        "length": len(data), "regions": [{"name": "Ogg", "offset": 0, "length": len(data)}], "encoder": encoder,
        "pages": [r for _, r in records], "streams": streams, "stream_infos": infos,
        "findings": [{"code": code, "contains": text} for code, text in findings], "crosscheck": {"valid": True},
    }


def main():
    only_manifests = "--manifests-only" in sys.argv
    if not only_manifests:
        missing = [tool for tool in (SPEEXENC, FFMPEG) if shutil.which(tool) is None]
        if missing:
            raise SystemExit(f"Missing {', '.join(missing)}; install speex and ffmpeg, or pass --manifests-only.")
    with tempfile.TemporaryDirectory() as work:
        for name, (tool, rate, channels, arguments, detected, findings) in FIXTURES.items():
            path = OUT / (name + ".expected.json")
            if only_manifests:
                encoder = json.loads(path.read_text(encoding="utf-8"))["encoder"]
            else:
                encoder = encode(name, tool, rate, channels, arguments, pathlib.Path(work))
            result = manifest(name, encoder, detected, findings)
            path.write_text(json.dumps(result, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote {len(FIXTURES)} {'manifests' if only_manifests else 'fixtures'} to {OUT}")


if __name__ == "__main__":
    main()
