#!/usr/bin/env python3
"""Writes the encoder-made Musepack fixtures in tests/fixtures/musepack (external-*.mpc).

SV8 comes from mpcenc 1.30.1 of Ubuntu's musepack-tools (0.1~r495) at several profiles, sample rates,
packet sizes and seek distances, mono and stereo, without encoder info and seek table, with
perceptual noise substitution, with an APEv2 tag, with ReplayGain from mpcgain and chapters from
mpcchap; one SV8 file is mpc2sv8's conversion of an SV7 one. SV7 comes from mppenc 1.16, the last SV7
encoder, built from its source release by musepack_tool.py. ffmpeg writes no Musepack. The inputs
are deterministic signals written here: two tones under a slow tremolo, then noise, which gives
mid/side stereo, the bit allocation and noise substitution something to decide.

The encoders' output depends on their builds, so the files are committed and --manifests-only
recomputes the manifests from them without the encoders. Each manifest is worked out by
musepack_model.py from the file, plus the encoder and its settings, the encoder the analyzer must
name and the findings it must report. crosscheck_musepack_fixtures.py confirms them with mpcdec,
ffprobe and libmpcdec's own reading of each SV7 frame.
"""

import json
import math
import pathlib
import shutil
import struct
import subprocess
import sys
import tempfile

import musepack_model as m
import musepack_tool

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "musepack"

MPCENC = {"name": "mpcenc 1.30.1", "confidence": "High"}
MPPENC = {"name": "mppenc 1.16", "confidence": "High"}

# name: (tool, rate, channels, samples, arguments, detected encoder, expected findings)
FIXTURES = {
    "external-mpcenc-standard": ("mpcenc", 44100, 2, 66150, ["--standard"], MPCENC, []),
    "external-mpcenc-telephone-mono": ("mpcenc", 44100, 1, 66150, ["--telephone"], MPCENC, []),
    "external-mpcenc-radio-48k": ("mpcenc", 48000, 2, 72000, ["--radio"], MPCENC, []),
    "external-mpcenc-insane-32k": ("mpcenc", 32000, 2, 48000, ["--insane"], MPCENC, []),
    "external-mpcenc-braindead": ("mpcenc", 44100, 2, 66150, ["--braindead"], MPCENC, []),
    "external-mpcenc-pns": ("mpcenc", 44100, 2, 66150, ["--thumb", "--pns", "1"], MPCENC, []),
    "external-mpcenc-packets": ("mpcenc", 44100, 2, 66150, ["--thumb", "--num_frames", "1", "--seek_distance", "0"], MPCENC, []),
    "external-mpcenc-no-ei-no-st": ("mpcenc", 44100, 2, 66150, ["--thumb", "--no_ei", "--no_st"], None, []),
    "external-mpcenc-apetag": ("mpcenc", 44100, 2, 66150, ["--thumb", "--artist", "Fixture artist", "--title", "Fixture title"], MPCENC, []),
    "external-mpcenc-replaygain": ("mpcgain", 44100, 2, 66150, ["--thumb"], MPCENC, []),
    "external-mpcenc-chapters": ("mpcchap", 44100, 2, 66150, ["--thumb"], MPCENC, []),
    "external-mppenc-standard": ("mppenc", 44100, 2, 66150, ["--standard"], MPPENC, []),
    "external-mppenc-telephone-mono": ("mppenc", 44100, 1, 66150, ["--telephone"], MPPENC, []),
    "external-mppenc-braindead-48k": ("mppenc", 48000, 2, 72000, ["--braindead"], MPPENC, []),
    # 927 samples in the last frame, more than 671, so mppenc writes one frame after those it counts.
    "external-mppenc-extra-frame": ("mppenc", 44100, 2, 66591, ["--radio"], MPPENC, []),
    "external-mppenc-pns": ("mppenc", 44100, 2, 66150, ["--thumb", "--pns", "1"], MPPENC, []),
    "external-mpc2sv8": ("mpc2sv8", 44100, 2, 66150, [], {"name": "mppenc 1.16, converted to SV8 by mpc2sv8", "confidence": "High"}, []),
}

CHAPTERS = "[0]\nTitle=First part\n[33075]\nTitle=Second part\nArtist=Somebody\n"


def signal(rate, channels, samples):
    """Tones of 300 Hz and 1100 Hz (1700 Hz on the second channel) under a 3 Hz tremolo, the last third
    of the time white noise from a fixed linear congruential generator."""
    out = bytearray()
    state = 12345
    for i in range(samples):
        t = i / rate
        noise = i >= samples * 2 // 3
        if noise:
            state = (state * 1103515245 + 12345) & 0x7FFFFFFF
        for channel in range(channels):
            if noise:
                value = ((state >> (8 + channel)) & 0x3FFF) - 0x2000
            else:
                envelope = 8000 * (0.5 + 0.5 * math.sin(2 * math.pi * 3 * t))
                value = envelope * (math.sin(2 * math.pi * 300 * t) + 0.5 * math.sin(2 * math.pi * (1100 + 600 * channel) * t + 0.3))
            out += struct.pack("<h", max(-32768, min(32767, int(value))))
    return bytes(out)


def wav(path, rate, channels, samples):
    pcm = signal(rate, channels, samples)
    header = b"RIFF" + struct.pack("<I", 36 + len(pcm)) + b"WAVEfmt " + struct.pack("<IHHIIHH", 16, 1, channels, rate, rate * channels * 2,
                                                                                    channels * 2, 16)
    path.write_bytes(header + b"data" + struct.pack("<I", len(pcm)) + pcm)


def version(tool):
    banner = subprocess.run([tool], capture_output=True, text=True).stderr
    return " ".join(banner.split("\n")[0].replace("\x1b[1m", "").split()[:4])


def encode(name, tool, rate, channels, samples, arguments, work, mppenc):
    target = OUT / (name + ".mpc")
    target.unlink(missing_ok=True)
    source = work / f"in-{rate}-{channels}-{samples}.wav"
    wav(source, rate, channels, samples)
    if tool == "mpc2sv8":
        subprocess.run(["mpc2sv8", str(OUT / "external-mppenc-standard.mpc"), str(target)], check=True, capture_output=True)
        return {"tool": "mpc2sv8 (musepack-tools 0.1~r495)", "input": "external-mppenc-standard.mpc"}
    if tool == "mppenc":
        subprocess.run([str(mppenc), "--silent", *arguments, str(source), str(target)], check=True, capture_output=True)
        return {"tool": "mppenc 1.16 (musepack_tool.py)", "arguments": arguments, "rate": rate, "channels": channels, "samples": samples}
    subprocess.run(["mpcenc", "--silent", "--overwrite", *arguments, str(source), str(target)], check=True, capture_output=True)
    used = {"tool": f"{version('mpcenc')} (musepack-tools 0.1~r495)", "arguments": arguments, "rate": rate, "channels": channels, "samples": samples}
    if tool == "mpcgain":
        subprocess.run(["mpcgain", str(target)], check=True, capture_output=True)
        used["then"] = "mpcgain"
    elif tool == "mpcchap":
        chapters = work / "chapters.ini"
        chapters.write_text(CHAPTERS)
        subprocess.run(["mpcchap", str(target), str(chapters)], check=True, capture_output=True)
        used["then"] = "mpcchap with " + CHAPTERS.replace("\n", " ").strip()
    return used


def regions(data, name):
    """The top-level nodes: the Musepack stream and, for the tagged fixture, mpcenc's APEv2 tag after it."""
    if name == "external-mpcenc-apetag":
        tag = data.rindex(b"APETAGEX", 0, len(data) - 32)
        return tag, [{"name": "Musepack SV8", "offset": 0, "length": tag}, {"name": "APE", "offset": tag, "length": len(data) - tag}]
    return len(data), [{"name": f"Musepack SV{8 if data[:4] == b'MPCK' else 7}", "offset": 0, "length": len(data)}]


def build_manifest(name, encoder, detected, findings):
    data = (OUT / (name + ".mpc")).read_bytes()
    end, top = regions(data, name)
    infos = {"encoder": detected["name"], "encoder_confidence": detected["confidence"]} if detected else {"encoder": None}
    result = m.manifest(data, 0, end, {"regions": top, "encoder": encoder,
                                        "findings": [{"code": code, "contains": text} for code, text in findings], "crosscheck": {"valid": True}})
    result["stream_infos"][0].update(infos)
    return result


def main():
    only_manifests = "--manifests-only" in sys.argv
    mppenc = None
    if not only_manifests:
        missing = [tool for tool in ("mpcenc", "mpcgain", "mpcchap", "mpc2sv8", "gcc") if shutil.which(tool) is None]
        if missing:
            raise SystemExit(f"Missing {', '.join(missing)}; install musepack-tools and gcc, or pass --manifests-only.")
    OUT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as work:
        if not only_manifests:
            mppenc = musepack_tool.build_mppenc(pathlib.Path(work) / "mppenc")
        for name, (tool, rate, channels, samples, arguments, detected, findings) in FIXTURES.items():
            path = OUT / (name + ".expected.json")
            if only_manifests:
                encoder = json.loads(path.read_text(encoding="utf-8"))["encoder"]
            else:
                encoder = encode(name, tool, rate, channels, samples, arguments, pathlib.Path(work), mppenc)
            path.write_text(json.dumps(build_manifest(name, encoder, detected, findings), indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Wrote {len(FIXTURES)} {'manifests' if only_manifests else 'fixtures'} to {OUT}")


if __name__ == "__main__":
    main()
