#!/usr/bin/env python3
"""Checks the WAV and AIFF fixtures' manifests against independent tools.

For every manifest in tests/fixtures/wav and tests/fixtures/aiff (make_external_wav_fixtures.py, make_wav_fixtures.py, make_external_aiff_fixtures.py,
make_aiff_fixtures.py), on the fixtures whose manifest lists no finding or only findings the parsers raise as information (a file the specifications
accept, with a note: a coding the analyzer has no parser for, a fact chunk it would want; a damaged file is read differently by every tool, and its
manifest rests on the model's reading of the specifications, docs/development.md):

- FFmpeg 8.0.1 (ffprobe, built with the wav, w64 and spdif demuxers): the codec (as FFMPEG_CODECS maps the manifest's), the sample rate, the channels
  and the duration; the bits per sample of the linear codings; for a file whose data FFmpeg routes to a codec (MPEG audio, AC-3, E-AC-3, DTS) the
  codec's name, sample rate and channels. For IEC 61937 bursts in the samples: the duration, which is the bursts times the repetition period over the
  link rate, and, where FFmpeg finds the bursts (AC-3 and DTS), the codec, sample rate and channels.
  For an AIFF file with a CHAN chunk the speakers of ffprobe's channel_layout (see ffprobe_speakers).
- MediaInfo 26.01 (mediainfo_tool.py): the audio stream's format (as MEDIAINFO_FORMATS maps the manifest's codec), sample rate, channels, the bit
  depth of the linear codings, the duration and the speakers of ChannelLayout, where it reports them. For IEC 61937 bursts: the format family and the
  duration, where MediaInfo finds the bursts (AC-3, MPEG audio and AAC in some files).

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

# FFmpeg's channel layout names (ffmpeg-utils, "Channel Layout") for the layouts the CHAN fixtures produce, as the speakers in the project's terms.
FFMPEG_SPEAKERS = {"FL": "front left", "FR": "front right", "FC": "front center", "LFE": "LFE", "BL": "back left", "BR": "back right", "BC": "back center",
                   "SL": "side left", "SR": "side right", "FLC": "front left of center", "FRC": "front right of center", "UNK": "discrete"}
FFMPEG_LAYOUTS = {
    "mono": "FC", "stereo": "FL+FR", "2.1": "FL+FR+LFE", "3.0": "FL+FR+FC", "4.0": "FL+FR+FC+BC", "quad": "FL+FR+BL+BR", "quad(side)": "FL+FR+SL+SR",
    "5.0": "FL+FR+FC+BL+BR", "5.0(side)": "FL+FR+FC+SL+SR", "5.1": "FL+FR+FC+LFE+BL+BR", "5.1(side)": "FL+FR+FC+LFE+SL+SR", "6.0": "FL+FR+FC+BC+SL+SR",
    "hexagonal": "FL+FR+FC+BL+BR+BC", "6.1": "FL+FR+FC+LFE+BC+SL+SR", "7.0": "FL+FR+FC+BL+BR+SL+SR", "7.1": "FL+FR+FC+LFE+BL+BR+SL+SR",
}


def ffprobe_speakers(layout):
    """(speakers, is_ordered) of ffprobe's channel_layout: "5.1(side)" is a named layout, whose order is FFmpeg's and not the file's, and "8 channels
    (FL+FR+...)" lists the channels in the file's order; None for a layout this table does not hold (unknown)."""
    match = re.fullmatch(r"\d+ channels \((.*)\)", layout or "")
    names = match.group(1) if match else FFMPEG_LAYOUTS.get(layout or "")
    if names is None or any(n not in FFMPEG_SPEAKERS for n in names.split("+")):
        return None
    return [FFMPEG_SPEAKERS[n] for n in names.split("+")], bool(match)


def surround_normalized(speakers):
    """The speakers with the surround pair named alike: this project (Speaker) calls the only surround pair of a layout the back pair, FFmpeg's
    5.1 and 5.1(side) differ in what the same channels are called, and the pair is the same two channels."""
    if ("back left" in speakers) != ("side left" in speakers):
        return ["surround left" if s in ("back left", "side left") else "surround right" if s in ("back right", "side right") else s for s in speakers]
    return list(speakers)


# IEC 61937 bursts: the codec_name ffprobe gives the data types its WAVE demuxer finds, by the codec the manifest routes to; the data type's name -> the
# Format MediaInfo gives when it finds them.
IEC_FFPROBE = {"ac-3": "ac3", "dts": "dts"}
IEC_MEDIAINFO_FORMATS = {"AC-3": {"AC-3"}, "E-AC-3": set(), "MPEG": {"MPEG Audio"}, "AAC": {"AAC"}}


def iec_family(name):
    """The family of a data type's name for IEC_MEDIAINFO_FORMATS."""
    if name.startswith("MPEG") and "AAC" in name:
        return "AAC"
    if name.startswith("MPEG"):
        return "MPEG"
    return name if name in IEC_MEDIAINFO_FORMATS else None


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
    "wav/built-bw64-table": {"ffprobe audio stream": "FFmpeg 8.0.1 stops with \"no 'data' tag found\": the axml chunk of the fixture has the size 0xFFFFFFFF, which its ds64 table resolves, and wavdec.c takes the 32-bit size and skips past the end of the file (as for built-rf64-table)"},
    "wav/built-w64-unknown-chunks": {"ffprobe audio stream": "FFmpeg 8.0.1 refuses a Wave64 file with a chunk of no data (the chunk 'zz  ' of size 24): \"Invalid data found when processing input\" (observed: the same file without that chunk, and with chunks of 3, 5 and 9 bytes or a LIST, is read)"},
    "wav/external-w64-adpcm-ima": {"MediaInfo Duration": "MediaInfo gives 0.706 s for 0.25 s of audio: it does not use the fact chunk of a Wave64 file (observed)"},
    "wav/built-w64-fact-ima-adpcm": {"MediaInfo Duration": "MediaInfo does not use the fact chunk of a Wave64 file (observed, as for external-w64-adpcm-ima)"},
    "wav/external-w64-pcm-u8-mono-8000": {"MediaInfo Duration": "MediaInfo's duration of a Wave64 file is 24 samples too long for 8-bit mono (0.253 s for 2,000 samples at 8,000 Hz): it counts the 24 bytes of the data chunk's header (observed)"},
    "wav/external-w64-alaw-mono-8000": {"MediaInfo Duration": "MediaInfo's duration of a Wave64 file is 24 samples too long for 8-bit mono (0.253 s for 2,000 samples at 8,000 Hz): it counts the 24 bytes of the data chunk's header (observed)"},
    "wav/built-w64-pcm-8-bit-mono-odd": {"MediaInfo Duration": "MediaInfo's duration of a Wave64 file counts the bytes of the data chunk's header too (observed, as for external-w64-pcm-u8-mono-8000)"},
    "wav/built-iec61937-ac3-after-silence": {"ffprobe duration": "ffprobe's duration is that of the whole data chunk, the silence before the first burst included; the analyzer times the bursts",
                                            "MediaInfo Duration": "MediaInfo's duration is that of the whole data chunk, the silence before the first burst included; the analyzer times the bursts"},
    "wav/built-iec61937-ac3-pause-bursts": {"ffprobe duration": "ffprobe's duration is that of the whole data chunk, the pause bursts included; the analyzer times the bursts of the stream",
                                           "MediaInfo Duration": "MediaInfo's duration is that of the whole data chunk, the pause bursts included; the analyzer times the bursts of the stream"},
    "wav/built-iec61937-ac3-silence-between": {"ffprobe duration": "ffprobe's duration is that of the whole data chunk, the silent periods included; the analyzer times the bursts",
                                              "MediaInfo Duration": "MediaInfo's duration is that of the whole data chunk, the silent periods included; the analyzer times the bursts"},
    "wav/built-iec61937-ac3-fmt-tag-0092": {"ffprobe codec": "ffprobe gives the format tag 0x0092 no codec_name (it finds the bursts, whose duration it reads)"},
    "wav/built-iec61937-in-24-bits": {"ffprobe codec": "ffprobe's WAVE demuxer looks for IEC 61937 bursts at the start of the data whatever the fmt chunk states (24 bits here) and finds AC-3; the analyzer takes only 16-bit stereo PCM for a carrier"},
    "wav/built-iec61937-in-mono": {"ffprobe codec": "ffprobe's WAVE demuxer looks for IEC 61937 bursts at the start of the data whatever the fmt chunk states (one channel here) and finds AC-3; the analyzer takes only 16-bit stereo PCM for a carrier",
                                  "ffprobe channels": "ffprobe gives the AC-3 stream its two channels, not the fmt chunk's one"},
    "wav/built-iec61937-in-rifx": {"ffprobe codec": "ffprobe's WAVE demuxer finds the bursts in the data of a RIFX file, which are little-endian samples there and no big-endian ones; the analyzer takes only little-endian PCM for a carrier",
                                  "MediaInfo Format": "MediaInfo finds the bursts in the data of a RIFX file too (observed)",
                                  "MediaInfo Duration": "MediaInfo's duration for this RIFX file is 1.248 s for 0.16 s of data (observed)"},
    "wav/built-iec61937-payload-too-long-at-start": {"ffprobe codec": "ffprobe takes the first burst alone for AC-3, whatever its length code states (the analyzer needs a chain of three bursts that fit their period)",
                                                    "MediaInfo Format": "MediaInfo takes the first burst alone for AC-3, whatever its length code states"},
    "wav/built-iec61937-sync-word-alone": {"MediaInfo Format": "MediaInfo takes the sync words and a burst-info at the start for AC-3; the analyzer needs a chain of three bursts",
                                          "MediaInfo SamplingRate": "MediaInfo reports no rate for that AC-3 stream",
                                          "MediaInfo Channels": "MediaInfo reports no channels for that AC-3 stream"},
    "wav/built-iec61937-two-bursts": {"MediaInfo Format": "MediaInfo takes two bursts at the start for an AC-3 stream; the analyzer needs a chain of three"},
    "aiff/built-chan-descriptions-rear-labels": {"ffprobe channel_layout": "ffprobe 8.0.1 gives the channel labels 33 and 34 (RearSurroundLeft and RearSurroundRight) no speaker (UNK); the Core Audio Format Specification lists them"},
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
    if "iec61937" in manifest:
        check_iec_ffprobe(name, manifest, stream, document, problems)
        return
    if name.startswith("aiff/") and any(c["id"] == "CHAN" for c in manifest["chunks"]) and expected.get("layout"):
        found = ffprobe_speakers(stream.get("channel_layout"))
        if found is None:
            skip(name, "ffprobe channel_layout", f"ffprobe gives no layout this script maps ({stream.get('channel_layout')!r})")
        else:
            speakers, is_ordered = found
            ours = surround_normalized(expected["layout"])
            theirs = surround_normalized(speakers)
            differs(name, "ffprobe channel_layout", ours == theirs if is_ordered else sorted(ours) == sorted(theirs), True, problems,
                    f"ffprobe channel_layout {stream.get('channel_layout')!r} is {theirs}, the manifest {ours}")
    if expected.get("carried"):
        skip(name, "ffprobe stream", "ffprobe reads the PCM carrier: wavdec.c has no probe for a DTS stream written straight into the samples, so the codec, rate and channels are the fmt chunk's")
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


def check_iec_ffprobe(name, manifest, stream, document, problems):
    """Bursts of IEC 61937 in the samples: the length is the bursts' (the carrier's too), and AC-3 and DTS bursts are found by the WAVE demuxer."""
    iec = manifest["iec61937"]
    expected = manifest["stream"]
    duration = stream.get("duration") or document.get("format", {}).get("duration")
    differs(name, "ffprobe duration", duration is not None and abs(float(duration) - iec["duration"]) <= 0.0015, True, problems,
            f"ffprobe duration is {duration}, the bursts' {iec['duration']}")
    codec = expected.get("routed")
    if codec in IEC_FFPROBE:
        differs(name, "ffprobe codec", stream.get("codec_name"), IEC_FFPROBE[codec], problems)
        differs(name, "ffprobe sample_rate", str(stream.get("sample_rate")), str(expected["sample_rate"]), problems)
        differs(name, "ffprobe channels", stream.get("channels"), expected["channels"], problems)
    else:
        skip(name, "ffprobe stream", "ffprobe 8.0.1 finds AC-3 and DTS bursts in a WAVE file and reads the others as the PCM carrier (observed; the layer II file of the low sampling frequency types as mp2 at the content rate)")


def check_iec_mediainfo(name, manifest, audio, problems):
    iec = manifest["iec61937"]
    expected = manifest["stream"]
    family = iec_family(iec["name"])
    found = audio.get("Format")
    if found not in {"AC-3", "MPEG Audio", "AAC"}:
        skip(name, "MediaInfo stream", "MediaInfo reports the PCM carrier for this file (observed: it finds AC-3, MPEG audio and AAC bursts in some files, DTS and E-AC-3 in none)")
        return
    differs(name, "MediaInfo Format", found in IEC_MEDIAINFO_FORMATS.get(family, set()), True, problems, f"MediaInfo Format is {found}, expected one of {sorted(IEC_MEDIAINFO_FORMATS.get(family, set()))}")
    if audio.get("Duration") is not None:
        differs(name, "MediaInfo Duration", abs(float(audio["Duration"]) - iec["duration"]) <= 0.0025, True, problems,
                f"MediaInfo Duration is {audio['Duration']}, the bursts' {iec['duration']}")
    if "routed" in expected:
        differs(name, "MediaInfo SamplingRate", audio.get("SamplingRate"), str(expected["sample_rate"]), problems)
        differs(name, "MediaInfo Channels", audio.get("Channels"), str(expected["channels"]), problems)
    else:
        skip(name, "MediaInfo rate and channels", "the analyzer states neither for bursts of a coding it has no parser for")


def check_mediainfo(name, manifest, path, problems):
    tracks = mi.read_or_problem(path, problems, full=True)
    if tracks is None:
        return
    audio = tracks["Audio"][0] if tracks["Audio"] else None
    differs(name, "MediaInfo audio stream", audio is not None, True, problems, "MediaInfo finds no audio stream")
    if audio is None:
        return
    expected = manifest["stream"]
    if "iec61937" in manifest:
        check_iec_mediainfo(name, manifest, audio, problems)
        return
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
