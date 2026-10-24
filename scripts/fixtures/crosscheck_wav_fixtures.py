#!/usr/bin/env python3
"""Checks the WAV and AIFF fixtures' manifests against independent tools.

For every manifest in tests/fixtures/wav and tests/fixtures/aiff (make_external_wav_fixtures.py, make_wav_fixtures.py, make_external_aiff_fixtures.py,
make_aiff_fixtures.py), on the fixtures whose manifest lists no finding or only findings the parsers raise as information (a file the specifications
accept, with a note: a coding the analyzer has no parser for, a fact chunk it would want; a damaged file is read differently by every tool, and its
manifest rests on the model's reading of the specifications, docs/development.md):

- FFmpeg 8.0.1 (ffprobe): the codec (as FFMPEG_CODECS maps the manifest's), the sample rate, the channels and the duration; the bits per sample of
  the linear codings; for a file whose data FFmpeg routes to a codec (MPEG audio, AC-3, E-AC-3, DTS) the codec's name, sample rate and channels.
- MediaInfo 26.01 (mediainfo_tool.py): the audio stream's format (as MEDIAINFO_FORMATS maps the manifest's codec), sample rate, channels, the bit
  depth of the linear codings, the duration and the speakers of ChannelLayout, where it reports them.

Every skipped check prints its reason. Disagreements fail the run (exit status 1).

    python3 scripts/fixtures/crosscheck_wav_fixtures.py [name ...]
"""

import json
import pathlib
import re
import subprocess
import sys

import mediainfo_tool as mi
import tool_versions

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORIES = [ROOT / "tests" / "fixtures" / "wav", ROOT / "tests" / "fixtures" / "aiff"]
SOURCES = [ROOT / "src" / "Encspect.Formats.Wav", ROOT / "src" / "Encspect.Formats.Aiff"]
SKIPS = {}


def information_codes():
    """The finding codes the parsers raise only as information (never as a warning or an error): the sources are read, so a code whose severity
    changes leaves or joins the compared fixtures with the change."""
    severities = {}
    for directory in SOURCES:
        for path in directory.glob("*.cs"):
            for severity, code in re.findall(r'Severity\.(Info|Warning|Error),\s*"((?:wav|aiff)\.[a-z0-9-]+)"', path.read_text(encoding="utf-8")):
                severities.setdefault(code, set()).add(severity)
    return {code for code, found in severities.items() if found == {"Info"}}

# The manifest's codec -> the codec_name values ffprobe gives, by byte order and sample width where the manifest does not say them.
FFMPEG_CODECS = {
    "A-law": {"pcm_alaw"},
    "Mu-law": {"pcm_mulaw"},
    "Microsoft ADPCM": {"adpcm_ms"},
    "IMA ADPCM": {"adpcm_ima_wav", "adpcm_ima_qt"},
    "GSM 6.10": {"gsm_ms", "gsm"},
    "MACE 3-to-1": {"mace3"},
    "MACE 6-to-1": {"mace6"},
}

# The manifest's codec -> MediaInfo's Format. MediaInfo names every AIFF sample coding "PCM", and the A-law and mu-law WAVE tags "ADPCM"
# (MediaInfoLib's Riff_Format_Tag table); the others it names as the manifest does.
MEDIAINFO_FORMATS = {
    "PCM": {"PCM"},
    "PCM (floating-point)": {"PCM"},
    "A-law": {"ADPCM", "PCM"},
    "Mu-law": {"ADPCM", "PCM"},
    "Microsoft ADPCM": {"ADPCM"},
    "IMA ADPCM": {"ADPCM"},
    "GSM 6.10": {"GSM 6.10"},
}

# The manifest's stream "routed": the codec FFmpeg reads from the data chunk is not in the manifest, so only its presence is compared.
ROUTED_FORMATS = {"mp3", "mp2", "ac3", "eac3", "dts"}


# {fixture: {field: reason}}: a tool's own reading of a field on a fixture, with the evidence. The field must still differ from the manifest, so
# the list stays current.
EXPLAINED = {
    "wav/built-fact-missing-compressed": {"ffprobe duration": "ffprobe estimates the duration of a compressed format without a fact chunk from the average bytes per second; the analyzer states none without the samples of a fact chunk",
                                          "MediaInfo Duration": "MediaInfo estimates the duration of a compressed format without a fact chunk from the average bytes per second; the analyzer states none"},
    "wav/built-wavl-list-without-fact": {"ffprobe audio stream": "FFmpeg 6.1.1 and 8.0.1 stop with \"no 'data' tag found\": wavdec.c looks for a data chunk at the top level and does not enter a wavl list"},
    "aiff/built-aifc-ace2": {"ffprobe audio stream": "FFmpeg 6.1.1 and 8.0.1 list no ACE2 coding (aiff.c has MACE 3:1 and 6:1 only) and ffprobe reports no audio stream"},
    "aiff/built-aifc-ace8": {"ffprobe audio stream": "FFmpeg 6.1.1 and 8.0.1 list no ACE8 coding (aiff.c has MACE 3:1 and 6:1 only) and ffprobe reports no audio stream",
                            "MediaInfo Duration": "MediaInfo takes the Common Chunk's frame count of compressed sample frames for a count of sample frames"},
    "aiff/built-aifc-gsm": {"ffprobe channels": "FFmpeg's GSM decoder is mono whatever the Common Chunk states",
                           "ffprobe duration": "aiffdec.c takes the Common Chunk's count for blocks of 160 samples (33 bytes) for GSM; no source read says what that count counts, so the analyzer states no length",
                           "MediaInfo Duration": "MediaInfo takes the Common Chunk's frame count for a count of sample frames; no source read says what it counts for GSM, so the analyzer states no length"},
    "aiff/built-aifc-mac3": {"MediaInfo Duration": "MediaInfo takes the Common Chunk's frame count of compressed sample frames for a count of sample frames"},
    "aiff/built-aifc-mac3-short": {"ffprobe duration": "ffprobe's duration is the Common Chunk's count times the 6 samples of a frame (aiffdec.c, st->duration = nb_frames * block_duration), not cut to the frames the sound data holds as the analyzer cuts it",
                                   "MediaInfo Duration": "MediaInfo takes the Common Chunk's frame count of compressed sample frames for a count of sample frames, and does not cut it to the sound data"},
    "aiff/built-aifc-mac6": {"MediaInfo Duration": "MediaInfo takes the Common Chunk's frame count of compressed sample frames for a count of sample frames"},
    "aiff/built-aifc-unknown-type": {"ffprobe audio stream": "FFmpeg 6.1.1 and 8.0.1 refuse a compression type they do not know (aiffdec.c: \"unknown or unsupported codec tag\") and ffprobe reports no audio stream"},
    "wav/built-extensible-ebu-mask": {"MediaInfo ChannelLayout": "MediaInfo lists the two speakers of the mask and none of the discrete channels the mask leaves over"},
    "wav/built-leading-id3v2": {"MediaInfo audio stream": "MediaInfo does not find the RIFF header behind a leading ID3v2 tag and reports no audio stream"},
    "aiff/built-leading-id3v2": {"MediaInfo audio stream": "MediaInfo does not find the FORM header behind a leading ID3v2 tag and reports no audio stream"},
    "wav/built-pcm-24-bit-extensible-in-32": {"ffprobe codec": "FFmpeg 6.1.1 and 8.0.1 read 24 valid bits in a 32-bit block as pcm_f24le (wavdec.c, wav_read_header: a pcm_s24le coding whose block align is 4 bytes for each channel)",
                                             "ffprobe bits": "ffprobe gives that coding a width of 32 bits"},
    "wav/built-dts-format-tag-0008": {"ffprobe codec": "FFmpeg 6.1.1 and 8.0.1 name no codec for the format tag 0x0008 (riff.c maps only 0x2001 to DTS), so ffprobe gives the stream no codec_name"},
    "wav/built-extensible-mask-fewer": {"MediaInfo ChannelLayout": "MediaInfo lists the two speakers of the mask and none of the discrete channels the mask leaves over"},
    "wav/built-extensible-mask-mismatch": {"MediaInfo ChannelLayout": "MediaInfo lists all six speakers of the mask for two channels; Microsoft's rule ignores the extra, most significant bits"},
    "wav/built-extensible-samples-per-block": {"ffprobe audio stream": "FFmpeg 6.1.1 and 8.0.1 cannot open the IMA ADPCM decoder for an extensible header with a container size of 0: \"unspecified number of channels\""},
    "wav/built-rf64-table": {"ffprobe audio stream": "FFmpeg 6.1.1 and 8.0.1 stop with \"Cannot allocate memory\": the bext chunk of the fixture has the size 0xFFFFFFFF, which its ds64 table resolves, and wavdec.c reads the 32-bit size (wav_parse_bext_tag allocates the coding history by it) and takes only the data size from the ds64 chunk"},
    "wav/built-riffx-pcm-16": {"MediaInfo audio stream": "MediaInfo reports no audio stream for a big-endian RIFX file"},
    "wav/built-wavl-list": {"ffprobe audio stream": "FFmpeg 6.1.1 and 8.0.1 stop with \"no 'data' tag found\": wavdec.c looks for a data chunk at the top level and does not enter a wavl list"},
    "aiff/built-aifc-ima4": {"MediaInfo Duration": "MediaInfo takes the Common Chunk's frame count of packets for a count of sample frames"},
    "aiff/external-ima4-stereo-44100": {"MediaInfo Duration": "MediaInfo takes the Common Chunk's frame count of packets for a count of sample frames"},
}
USED = set()


def skip(name, check, reason):
    SKIPS.setdefault((check, reason), []).append(name)


def differs(name, field, found, expected, problems, detail=None):
    """Records a disagreement unless the fixture's difference in the field is explained, in which case the tool must still differ."""
    reason = EXPLAINED.get(name, {}).get(field)
    if reason is not None:
        USED.add((name, field))
        if found == expected:
            problems.append(f"{field} now agrees ({found!r}); drop it from the explained differences")
        else:
            skip(name, field, reason)
        return
    if found != expected:
        problems.append(detail or f"{field} is {found!r}, the manifest {expected!r}")


def find(manifest):
    stem = manifest.name[:-len(".expected.json")]
    return next(p for p in manifest.parent.glob(stem + ".*") if not p.name.endswith(".json"))


def ffprobe_codec_ok(stream, codec):
    name = stream.get("codec_name", "")
    if codec == "PCM":
        return name.startswith("pcm_s") or name.startswith("pcm_u")
    if codec == "PCM (floating-point)":
        return name.startswith("pcm_f")
    return name in FFMPEG_CODECS.get(codec, set())


def check_ffprobe(name, manifest, path, problems):
    probe = subprocess.run(["ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", str(path)], capture_output=True)
    try:
        document = json.loads(probe.stdout or b"{}")
    except json.JSONDecodeError:
        document = {}
    stream = next((s for s in document.get("streams", []) if s.get("codec_type") == "audio"), None)
    expected = manifest["stream"]
    differs(name, "ffprobe audio stream", stream is not None, True, problems, "ffprobe finds no audio stream")
    if stream is None:
        return
    if expected.get("carried"):
        skip(name, "ffprobe stream", "ffprobe reads the PCM carrier: wavdec.c has no probe for a stream in the samples, so the codec, rate and channels are the fmt chunk's")
        return
    differs(name, "ffprobe sample_rate", str(stream.get("sample_rate")), str(expected["sample_rate"]), problems)
    differs(name, "ffprobe channels", stream.get("channels"), expected["channels"], problems)
    if "routed" in expected:
        codec = str(stream.get("codec_name"))
        differs(name, "ffprobe codec", codec in ROUTED_FORMATS, True, problems, f"ffprobe codec_name is {codec}, expected a codec the data chunk is routed to")
        skip(name, "ffprobe duration", "a routed stream's length is the codec parser's, which its own fixtures check")
        return
    if expected["codec"] == "Other":
        skip(name, "ffprobe codec", "the analyzer names no coding for a format tag it does not list")
    else:
        differs(name, "ffprobe codec", ffprobe_codec_ok(stream, expected["codec"]), True, problems,
                f"ffprobe codec_name is {stream.get('codec_name')}, the manifest {expected['codec']}")
    bits = expected["bits"]
    if bits in (8, 16, 24, 32, 64) and expected["codec"] in ("PCM", "PCM (floating-point)"):
        differs(name, "ffprobe bits", int(stream.get("bits_per_sample") or bits), bits, problems)
    elif bits is not None and expected["codec"] in ("PCM", "PCM (floating-point)"):
        skip(name, "ffprobe bits", "the sample width is not a whole number of bytes, which FFmpeg reports as the container's width")
    duration = stream.get("duration") or document.get("format", {}).get("duration")
    if duration is None:
        skip(name, "ffprobe duration", "ffprobe reports no duration")
    else:
        differs(name, "ffprobe duration", abs(float(duration) - expected["duration"]) <= 0.0015, True, problems,
                f"ffprobe duration is {duration}, the manifest {expected['duration']}")


def check_mediainfo(name, manifest, path, problems):
    tracks = mi.read_or_problem(path, problems, full=True)
    if tracks is None:
        return
    audio = tracks["Audio"][0] if tracks["Audio"] else None
    differs(name, "MediaInfo audio stream", audio is not None, True, problems, "MediaInfo finds no audio stream")
    if audio is None:
        return
    expected = manifest["stream"]
    differs(name, "MediaInfo SamplingRate", audio.get("SamplingRate"), str(expected["sample_rate"]), problems)
    differs(name, "MediaInfo Channels", audio.get("Channels"), str(expected["channels"]), problems)
    if "routed" in expected:
        skip(name, "MediaInfo format and duration", "a routed stream's coding and length are the codec parser's, which its own fixtures check")
        return
    formats = {"PCM"} if name.startswith("aiff/") else MEDIAINFO_FORMATS.get(expected["codec"])
    if formats is None:
        skip(name, "MediaInfo format", f"{expected['codec']} has no MediaInfo name this script maps")
    else:
        differs(name, "MediaInfo Format", audio.get("Format") in formats, True, problems,
                f"MediaInfo Format is {audio.get('Format')}, expected one of {sorted(formats)} for {expected['codec']}")
    bits = expected["bits"]
    if expected["codec"] in ("PCM", "PCM (floating-point)") and bits is not None:
        if audio.get("BitDepth") is None:
            skip(name, "MediaInfo bit depth", "MediaInfo reports no bit depth")
        else:
            differs(name, "MediaInfo BitDepth", audio.get("BitDepth"), str(bits), problems)
    if audio.get("Duration") is None:
        skip(name, "MediaInfo duration", "MediaInfo reports no duration")
    else:
        differs(name, "MediaInfo Duration", abs(float(audio["Duration"]) - expected["duration"]) <= 0.0025, True, problems,
                f"MediaInfo Duration is {audio['Duration']}, the manifest {expected['duration']}")
    layout = audio.get("ChannelLayout")
    if layout is None:
        skip(name, "MediaInfo channel layout", "MediaInfo reports no channel layout")
    else:
        speakers = mi.speakers(layout)
        if speakers is None:
            skip(name, "MediaInfo channel layout", f"MediaInfo's name '{layout}' is not in the speaker table")
        else:
            differs(name, "MediaInfo ChannelLayout", speakers, mi.normalized(expected["layout"]), problems,
                    f"MediaInfo ChannelLayout {layout!r} is {speakers}, the manifest {mi.normalized(expected['layout'])}")


def main():
    tool_versions.require("ffprobe")
    mi.require()
    chosen = set(a for a in sys.argv[1:] if not a.startswith("-"))
    informational = information_codes()
    failures = 0
    checked = 0
    for directory in DIRECTORIES:
        for manifest_path in sorted(directory.glob("*.expected.json")):
            name = f"{directory.name}/{manifest_path.name[:-len('.expected.json')]}"
            if chosen and name not in chosen:
                continue
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            if manifest["stream"] is None or any(finding["code"] not in informational for finding in manifest["findings"]):
                skip(name, "ffprobe and MediaInfo", "the fixture is damaged or unusual, so the tools do not agree on what it holds")
                continue
            path = find(manifest_path)
            problems = []
            check_ffprobe(name, manifest, path, problems)
            check_mediainfo(name, manifest, path, problems)
            checked += 1
            if problems:
                failures += 1
                print(f"FAIL {name}")
                for problem in problems:
                    print(f"    {problem}")
    if not chosen:
        for name, fields in sorted(EXPLAINED.items()):
            for field in fields:
                if (name, field) not in USED:
                    failures += 1
                    print(f"FAIL {name}: the explained difference in {field} was not compared")
    for (check, reason), names in sorted(SKIPS.items()):
        print(f"skipped {check} for {len(names)} fixtures: {reason}")
    print(f"{checked} fixtures checked, {failures} with disagreements.")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
