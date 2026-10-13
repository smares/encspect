#!/usr/bin/env python3
"""Writes the encoder-made Musepack fixtures in tests/fixtures/musepack (external-*.mpc).

SV8 comes from mpcenc 1.30.1 of Ubuntu's musepack-tools (0.1~r495) at several profiles, sample rates,
packet sizes and seek distances, mono and stereo, without encoder info and seek table, with
perceptual noise substitution, with an APEv2 tag, with ReplayGain from mpcgain and chapters from
mpcchap; one SV8 file is mpc2sv8's conversion of an SV7 one. SV7 comes from mppenc 1.16, the last SV7
encoder, built from its source release by musepack_tool.py, and from Rarewares' Win32 builds of the
earlier SV7 encoders under Wine, one fixture for each row of the analyzer's SV7 encoder table; every
SV7 build's header fields go to sv7-encoders.json, which the tests hold the table against. SV4 and SV5
come from Rarewares' Win32 builds of Buschmann's SV4 and SV5 encoders under Wine, which take only
stereo at 44.1 kHz: frames the decoders read, and streams with superframes, intensity stereo or a
constant bitrate, which libmpcdec 1.2.6 does not decode. For these, mppdec under Wine decodes each
file when it is written and the frames it decodes go into the manifest. ffmpeg writes no Musepack. The inputs
are deterministic signals written here: two tones under a slow tremolo, then noise, which gives
mid/side stereo, the bit allocation and noise substitution something to decide.

The encoders' output depends on their builds, so the files are committed and --manifests-only
recomputes the manifests from them without the encoders. Each manifest is worked out by
musepack_model.py from the file, plus the encoder and its settings, the encoder the analyzer must
name and the findings it must report. crosscheck_musepack_fixtures.py confirms them with mpcdec,
ffprobe and libmpcdec's own reading of each SV7 frame, and with libmpcdec 1.2.6's of each SV4 and SV5
frame.
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

# One fixture per row of the SV7 encoder table (MusepackEncoder.cs) from a Rarewares build, mono where
# the build takes it, with the build's default settings.
SV7_ROWS = {
    "0.90r": "mppenc 0.90f to 1.0 (Klemm)",
    "1.04": "mppenc 1.01j to 1.04",
    "1.06 alpha": "mppenc 1.05z1 to 1.06 alpha",
    "1.06": "mppenc 1.06",
    "1.1": "mppenc 1.1",
    "1.11a": "mppenc 1.11a",
    "1.11b": "mppenc 1.11b",
    "1.12": "mppenc 1.12",
    "1.13g": "mppenc 1.13a to 1.13g",
    "1.14": "mppenc 1.14",
    "1.15r": "mppenc 1.15f to 1.15r",
    "1.15u": "mppenc 1.15t to 1.15u",
    "MP+ 1.7.9c": "MP+ 1.7.8 to 1.7.9c (Buschmann)",
}
STEREO_ONLY = {"MP+ 1.7.8", "MP+ 1.7.9", "MP+ 1.7.9a", "MP+ 1.7.9c"}
SHORT = 33075
for build, detected in SV7_ROWS.items():
    channels = 2 if build in STEREO_ONLY else 1
    FIXTURES["external-" + ("mpp-" + build[4:] if build.startswith("MP+") else "mppenc-" + build.replace(" ", "-"))] = (
        build, 44100, channels, SHORT, [], {"name": detected, "confidence": "High"}, [])

# The SV4 and SV5 encoders take their switches after the file names. build_manifest adds the finding
# for a last frame that runs past the end of the data.
FIXTURES.update({
    "external-mppsv4-vbr": ("sv4", 44100, 2, SHORT, ["MS", "HQ", "-br", "var"], None, []),
    "external-mppsv4-superframes": ("sv4", 44100, 2, SHORT, ["MS", "NS", "-frm", "4", "-br", "var"], None,
                                    [("musepack.frames-not-read", "lead blocks of frames (4)")]),
    "external-mppsv4-intensity-128": ("sv4", 44100, 2, SHORT, ["IS", "-br", "128"], None,
                                      [("musepack.frames-not-read", "have a constant bitrate (128 kbps), use intensity stereo or lead blocks of frames (16)")]),
    "external-mppsv5-vbr": ("sv5", 44100, 2, SHORT, ["-ms", "1", "-br", "var"], None, []),
    "external-mppsv5-left-right-vbr": ("sv5", 44100, 2, SHORT, ["-ms", "0", "-br", "var"], None, []),
    "external-mppsv5-intensity-128": ("sv5", 44100, 2, SHORT, ["-is", "1", "-br", "128"], None,
                                      [("musepack.frames-not-read", "have a constant bitrate (128 kbps), use intensity stereo or lead blocks of frames (16)")]),
})

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
    if tool in musepack_tool.BUILDS:
        musepack_tool.encode_windows(tool, arguments, source, target)
        archive = musepack_tool.BUILDS[tool][0]
        used = {"tool": f"{tool if tool not in ('sv4', 'sv5') else 'the ' + tool.upper() + ' encoder'} ({archive}, Rarewares) under Wine",
                "arguments": arguments, "rate": rate, "channels": channels, "samples": samples}
        if tool in ("sv4", "sv5"):
            used["mppdec_frames"] = musepack_tool.mppdec_frames(target)
        return used
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


def sv7_encoders(work, mppenc):
    """The header fields every SV7 encoder at hand writes for the same stereo input at its default
    settings, with the name the analyzer must give them."""
    source = work / "encoders.wav"
    wav(source, 44100, 2, SHORT)
    rows = []
    target = work / "encoders.mpc"
    for build in ["1.16", *musepack_tool.SV7_BUILDS]:
        target.unlink(missing_ok=True)
        if build == "1.16":
            subprocess.run([str(mppenc), "--silent", str(source), str(target)], check=True, capture_output=True)
            archive = musepack_tool.MPPENC[0]
        else:
            musepack_tool.encode_windows(build, [], source, target)
            archive = musepack_tool.BUILDS[build][0]
        h = m.sv7_header(m.Words(target.read_bytes()))
        rows.append({"build": build, "archive": archive, "encoder": h["encoder"], "profile": h["profile"], "gapless": bool(h["gapless"]),
                     "last": h["last"], "max_level": h["max_level"], "fast_seek": bool(h["fast_seek"])})
    for row in rows:
        row["detected"] = next(name for build, name in SV7_GROUPS if row["build"] in build)
    (OUT / "sv7-encoders.json").write_text(json.dumps(rows, indent=1) + "\n", encoding="utf-8")


# The builds each row of the SV7 encoder table names.
SV7_GROUPS = [
    (("1.16",), "mppenc 1.16"),
    (("0.90f", "0.90f (v2)", "0.90k", "0.90m", "0.90o", "0.90r", "1.0"), "mppenc 0.90f to 1.0 (Klemm)"),
    (("1.01j", "1.02", "1.04"), "mppenc 1.01j to 1.04"),
    (("1.05z1", "1.05z2", "1.06 alpha"), "mppenc 1.05z1 to 1.06 alpha"),
    (("1.06",), "mppenc 1.06"),
    (("1.1",), "mppenc 1.1"),
    (("1.11a",), "mppenc 1.11a"),
    (("1.11b",), "mppenc 1.11b"),
    (("1.12",), "mppenc 1.12"),
    (("1.13a", "1.13g"), "mppenc 1.13a to 1.13g"),
    (("1.14",), "mppenc 1.14"),
    (("1.15f", "1.15i", "1.15r"), "mppenc 1.15f to 1.15r"),
    (("1.15t", "1.15u"), "mppenc 1.15t to 1.15u"),
    (("MP+ 1.7.8", "MP+ 1.7.9", "MP+ 1.7.9a", "MP+ 1.7.9c"), "MP+ 1.7.8 to 1.7.9c (Buschmann)"),
]


def regions(data, name):
    """The top-level nodes: the Musepack stream and, for the tagged fixture, mpcenc's APEv2 tag after it."""
    if name == "external-mpcenc-apetag":
        tag = data.rindex(b"APETAGEX", 0, len(data) - 32)
        return tag, [{"name": "Musepack SV8", "offset": 0, "length": tag}, {"name": "APE", "offset": tag, "length": len(data) - tag}]
    version = 8 if data[:4] == b"MPCK" else 7 if data[:3] == b"MP+" else m.Words(data).read(11, 10)
    return len(data), [{"name": f"Musepack SV{version}", "offset": 0, "length": len(data)}]


def build_manifest(name, encoder, detected, findings):
    data = (OUT / (name + ".mpc")).read_bytes()
    end, top = regions(data, name)
    infos = {"encoder": detected["name"], "encoder_confidence": detected["confidence"]} if detected else {"encoder": None}
    if data[:3] != b"MP+" and data[:4] != b"MPCK" and (m.old(data)["last_runs_past"] or 0) > 0:
        findings = [*findings, ("musepack.last-frame-unwritten", f"runs {m.old(data)['last_runs_past']} bits past the end of the data")]
    result = m.manifest(data, 0, end, {"regions": top, "encoder": encoder,
                                        "findings": [{"code": code, "contains": text} for code, text in findings], "crosscheck": {"valid": True}})
    result["stream_infos"][0].update(infos)
    return result


def main():
    only_manifests = "--manifests-only" in sys.argv
    mppenc = None
    if not only_manifests:
        missing = [tool for tool in ("mpcenc", "mpcgain", "mpcchap", "mpc2sv8", "gcc", "wine") if shutil.which(tool) is None]
        if missing:
            raise SystemExit(f"Missing {', '.join(missing)}; install musepack-tools, gcc and Wine, or pass --manifests-only.")
    OUT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as work:
        if not only_manifests:
            mppenc = musepack_tool.build_mppenc(pathlib.Path(work) / "mppenc")
            sv7_encoders(pathlib.Path(work), mppenc)
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
