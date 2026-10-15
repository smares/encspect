#!/usr/bin/env python3
"""Writes MP4 and raw AAC fixtures with real encoders and muxers into tests/fixtures/aac: ffmpeg 6.1 with its
own AAC encoder (M4A, ADTS with the fast coder, without its stereo, noise and TNS tools, AAC Main with
prediction and AAC LTP, fragmented MP4, and MP3 in MP4 with libmp3lame), fdkaac 1.0.0 with libfdk-aac 4.0.1
built from source (fdk_tool.py; LC, VBR, HE-AAC and HE-AAC v2 with both explicit signaling modes and implicit
signaling, in MP4 and ADTS, and ADTS with CRCs), FAAC 1.30 to 2.2 (faac_tool.py), exhale 1.2.2 for xHE-AAC (exhale_tool.py) and GPAC's MP4Box 2.2 as a muxer. Each
encodes 1.5 seconds of a 440/660 Hz tone, or for the ffmpeg encoder's tool settings ({mix}), of pink noise with a
tone, on which the encoder uses its stereo and noise tools.

Each <name>.expected.json manifest records the command and the encoder's version, and what the analyzer
must report, mostly from independent tools at the time of encoding:

- "boxes": the boxes MP4Box -diso lists, as path and size (the offset is null: MP4Box gives none).
- "units": the packets ffprobe lists with edit lists ignored (offset and size), or for xHE-AAC, which ffmpeg
  6.1 cannot read, the sample sizes MP4Box -dtsx lists (offset null).
- "streams": the codec, rate, channels, samples and encoder the settings give, with ffprobe's profile where
  ffmpeg names one, and "ffprobe_sample_rate" where ffmpeg finds SBR the configuration does not signal.
- "fields": the values MP4Box reads from the movie, track and media headers, the first edit, the audio sample
  entry and the decoder configuration (crosscheck_mp4_fixtures.mp4box_fields), and the fields the settings give.
- "findings" and "crosscheck" as in make_mp4_fixtures.py; "fdk_crc_errors" for protected ADTS,
  the access units fdk-aac's decoder rejects for their CRC.

    python3 scripts/fixtures/make_external_aac_fixtures.py --manifests-only [name ...]

rewrites the manifests from the committed files without the encoders. Running the script also checks the
hand-built ADTS CRC fixtures with fdk-aac's decoder, the one decoder at
hand that verifies ADTS CRCs, and fails where its verdicts differ from what their manifests imply.

Needs ffmpeg 6.1, faac 1.30, MP4Box (gpac 2.2), faad, git, cmake, make, autoconf, automake, libtool, meson, ninja
and a C and C++ compiler:

    apt-get install ffmpeg faac gpac faad cmake autoconf automake libtool meson ninja-build
"""

import json
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile

import exhale_tool
import faac_tool
import fdk_tool
from crosscheck_mp4_fixtures import ffprobe_packets, mp4box_boxes, mp4box_fields, mp4box_sample_sizes, with_unlisted

ROOT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures"
OUT = ROOT / "aac"
FFMPEG_VERSION = "ffmpeg version 6.1.1-3ubuntu5"
FAAC_VERSION = "FAAC 1.30"
# The 2.0 and 2.1 front ends' tool atoms carry no commit, the 2.2 front end's tool atom: its version and, from `git describe`, the abbreviated commit (faac_tool.py).
FAAC_2_2 = "FAAC 2.2.0 (6acbe23)"
FAAC_2_0 = "FAAC 2.0.0"
LAVC = "FFmpeg (Lavc60.31.102)"
FDKAAC = "fdkaac 1.0.0 (libfdk-aac 4.0.1)"
ASC = "MP4/moov/trak/mdia/minf/stbl/stsd/mp4a/esds/ES_Descriptor/DecoderConfigDescriptor/DecoderSpecificInfo/AudioSpecificConfig"
MOOV_AFTER = ["mp4.moov-after-mdat", "not optimized for streaming"]
IMPLICIT_SBR = ["aac.implicit-sbr", "35 of 35 raw data blocks hold SBR data"]
IMPLICIT_PS = ["aac.implicit-sbr", "36 of 36 raw data blocks hold SBR data, and 36 carry the parametric stereo extension"]
APPLE = "Apple AudioToolbox"
STREAM_TYPE = "MP4/moov/trak/mdia/minf/stbl/stsd/mp4a/esds/ES_Descriptor/DecoderConfigDescriptor/Stream Type"
GAPLESS = "MP4/moov/trak/Gapless/"
FAAD_ADTS_RATE = "faad -i prints twice the rate of any ADTS stream at 24 kHz or less, whatever its frames hold; the trace check reads the SBR data instead."
STATS = "ADTS/Statistics"


# The speakers of one and two channels: AAC's channel configurations 1 and 2 (ISO/IEC 14496-3 Table
# 1.19), parametric stereo's output, and MPEG audio's mono and stereo.
SPEAKERS = {1: ["front center"], 2: ["front left", "front right"], 6: ["front center", "front left", "front right", "back left", "back right", "LFE"]}


def stream(codec, rate, channels, mode, frame, vbr, encoder, profile=None, **extra):
    entry = {"codec": codec, "sample_rate": rate, "channels": channels, "channel_mode": mode, "channel_layout": SPEAKERS[channels], "frame": frame,
             "is_vbr": vbr}
    if encoder:
        entry["encoder"] = list(encoder)
    if profile:
        entry["ffprobe_profile"] = profile
    entry.update(extra)
    return entry


# name, extension, command (with {in}, {mono}, {out}, {fdkaac}, {exhale}), streams, fields, findings, crosscheck
FIXTURES = [
    ("ffmpeg-lc", ".m4a", ["ffmpeg", "-v", "error", "-y", "-i", "{in}", "-c:a", "aac", "-b:a", "128k", "{out}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, False, (LAVC, "High"), "LC")],
     {ASC + "/Sync Extension": "SBR absent"}, [MOOV_AFTER], {"faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}),
    ("ffmpeg-lc-vbr", ".m4a", ["ffmpeg", "-v", "error", "-y", "-i", "{in}", "-c:a", "aac", "-q:a", "1", "{out}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, False, (LAVC, "High"), "LC")], {}, [MOOV_AFTER], {"faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}),
    ("ffmpeg-mono-faststart", ".m4a", ["ffmpeg", "-v", "error", "-y", "-i", "{mono}", "-ar", "48000", "-c:a", "aac", "-b:a", "64k", "-movflags", "+faststart", "{out}"],
     [stream("AAC LC", 48000, 1, "Mono", 1024, False, (LAVC, "High"), "LC")], {}, [], {"faad": {"label": "LC AAC", "channels": 2, "rate": 48000}}),
    ("ffmpeg-adts", ".aac", ["ffmpeg", "-v", "error", "-y", "-i", "{in}", "-c:a", "aac", "-b:a", "96k", "{out}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, True, (LAVC, "High"), "LC")], {"ADTS/Frame/Buffer Fullness": "0x7FF (variable bitrate)"}, [],
     {"faad": "adts"}),
    ("ffmpeg-fragmented", ".mp4", ["ffmpeg", "-v", "error", "-y", "-i", "{in}", "-c:a", "aac", "-b:a", "128k", "-movflags", "frag_keyframe+empty_moov+default_base_moof",
                                   "-frag_duration", "500000", "{out}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, False, (LAVC, "High"), "LC")], {}, [],
     {"skip": {"faad": "faad2's MP4 reader reads samples from the sample tables only, which a fragmented file leaves empty.",
               "faad_trace": "faad2's MP4 reader reads samples from the sample tables only, which a fragmented file leaves empty."}}),
    ("ffmpeg-mp3", ".mp4", ["ffmpeg", "-v", "error", "-y", "-i", "{in}", "-c:a", "libmp3lame", "-b:a", "128k", "{out}"],
     [stream("MPEG-1 Layer III", 44100, 2, "Joint stereo", 1152, False, ("LAME 3.100", "Low"))], {}, [MOOV_AFTER], {"skip": {"faad": "faad2 reads AAC only."}}),
    ("fdkaac-lc", ".m4a", ["{fdkaac}", "-S", "-p", "2", "-b", "128000", "-G", "2", "-o", "{out}", "{in}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, False, (FDKAAC, "Low"), "LC")], {"MP4/moov/trak/Gapless/Encoder Delay (Edit List)": "2048 samples"},
     [MOOV_AFTER, ["mp4.duration-mismatch", "tkhd box of track 1 gives a duration of 68608"]],
     {"faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}),
    ("fdkaac-lc-vbr", ".m4a", ["{fdkaac}", "-S", "-p", "2", "-m", "4", "-o", "{out}", "{in}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, True, (FDKAAC, "Low"), "LC")], {}, [MOOV_AFTER],
     {"faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}),
    ("fdkaac-he", ".m4a", ["{fdkaac}", "-S", "-p", "5", "-b", "48000", "-o", "{out}", "{in}"],
     [stream("HE-AAC", 44100, 2, "Stereo", 2048, False, (FDKAAC, "Low"), "HE-AAC")], {ASC + "/Sync Extension": "SBR present"}, [MOOV_AFTER],
     {"faad": {"label": "HE AAC", "channels": 2, "rate": 44100}}),
    ("fdkaac-he-v2", ".m4a", ["{fdkaac}", "-S", "-p", "29", "-b", "32000", "-o", "{out}", "{in}"],
     [stream("HE-AAC v2", 44100, 2, "Parametric stereo", 2048, False, (FDKAAC, "Low"), "HE-AACv2")], {ASC + "/Sync Extension": "SBR and PS present"}, [MOOV_AFTER],
     {"faad": {"label": "HE AAC", "channels": 2, "rate": 44100}}),
    ("fdkaac-he-hierarchical", ".m4a", ["FDKAAC_SBR_SIGNALING=2", "{fdkaac}", "-S", "-p", "5", "-b", "48000", "-o", "{out}", "{in}"],
     [stream("HE-AAC", 44100, 2, "Stereo", 2048, False, (FDKAAC, "Low"), "HE-AAC")], {ASC + "/Audio Object Type": "5 (SBR)"}, [MOOV_AFTER],
     {"faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}),
    ("fdkaac-he-v2-hierarchical", ".m4a", ["FDKAAC_SBR_SIGNALING=2", "{fdkaac}", "-S", "-p", "29", "-b", "32000", "-o", "{out}", "{in}"],
     [stream("HE-AAC v2", 44100, 2, "Parametric stereo", 2048, False, (FDKAAC, "Low"), "HE-AACv2")], {ASC + "/Audio Object Type": "29 (PS)"}, [MOOV_AFTER],
     {"faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}),
    ("fdkaac-crc-mono", ".aac", ["{fdkaac}", "-S", "-p", "2", "-b", "64000", "-f", "2", "-C", "-o", "{out}", "{mono}"],
     [stream("AAC LC", 44100, 1, "Mono", 1024, False, None, "LC")], {"ADTS/Frame[1]/CRC/Check": "verified", "ADTS/Frame[40]/CRC/Check": "verified"},
     [], {"faad": "adts"}),
    ("fdkaac-he-adts", ".aac", ["{fdkaac}", "-S", "-p", "5", "-b", "48000", "-f", "2", "-o", "{out}", "{in}"],
     [stream("HE-AAC", 44100, 2, "Stereo", 2048, False, None, "HE-AAC")], {STATS + "/SBR": "35 access units (100.0%)"},
     [IMPLICIT_SBR], {"skip": {"faad": FAAD_ADTS_RATE}}),
    ("fdkaac-he-v2-adts", ".aac", ["{fdkaac}", "-S", "-p", "29", "-b", "32000", "-f", "2", "-o", "{out}", "{in}"],
     [stream("HE-AAC v2", 44100, 2, "Parametric stereo", 2048, False, None, "HE-AACv2")], {STATS + "/SBR/Parametric Stereo": "36 access units (100.0%)"},
     [IMPLICIT_PS], {"skip": {"faad": FAAD_ADTS_RATE}}),
    ("fdkaac-he-implicit", ".m4a", ["FDKAAC_SBR_SIGNALING=0", "{fdkaac}", "-S", "-p", "5", "-b", "48000", "-o", "{out}", "{in}"],
     [stream("HE-AAC", 44100, 2, "Stereo", 2048, False, (FDKAAC, "Low"), "HE-AAC")], {ASC + "/Audio Object Type": "2 (AAC LC)"}, [MOOV_AFTER, IMPLICIT_SBR],
     {"faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}),
    ("fdkaac-he-v2-implicit", ".m4a", ["FDKAAC_SBR_SIGNALING=0", "{fdkaac}", "-S", "-p", "29", "-b", "32000", "-o", "{out}", "{in}"],
     [stream("HE-AAC v2", 44100, 2, "Parametric stereo", 2048, False, (FDKAAC, "Low"), "HE-AACv2")], {ASC + "/Audio Object Type": "2 (AAC LC)"},
     [MOOV_AFTER, IMPLICIT_PS], {"faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}),
    ("faac", ".m4a", ["faac", "-o", "{out}", "{in}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, False, ("FAAC 1.30", "High"), "LC")], {}, [MOOV_AFTER, ["mp4.duration-mismatch", "mdhd box of track 1"]],
     {"faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}),
    ("faac-adts", ".aac", ["faac", "-o", "{out}", "{in}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, True, ("FAAC 1.30", "High"), "LC")], {}, [], {"faad": "adts"}),
    ("faac-1.31.1", ".m4a", ["{faac 1.31.1}", "-o", "{out}", "{in}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, False, ("FAAC 1.31.1", "High"), "LC")], {}, [MOOV_AFTER, ["mp4.duration-mismatch", "mdhd box of track 1"]],
     {"faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}),
    ("faac-1.31.1-adts", ".aac", ["{faac 1.31.1}", "-o", "{out}", "{in}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, True, ("FAAC 1.31.1", "High"), "LC")], {}, [], {"faad": "adts"}),
    ("faac-1.40", ".m4a", ["{faac 1.40}", "-o", "{out}", "{in}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, False, ("FAAC 1.40.0", "High"), "LC")], {}, [MOOV_AFTER, ["mp4.duration-mismatch", "mdhd box of track 1"]],
     {"faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}),
    ("faac-1.40-adts", ".aac", ["{faac 1.40}", "-o", "{out}", "{in}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, True, ("FAAC 1.40.0", "High"), "LC")], {}, [], {"faad": "adts"}),
    ("faac-1.50", ".m4a", ["{faac 1.50}", "-o", "{out}", "{in}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, False, ("FAAC 1.50.0", "High"), "LC")], {}, [MOOV_AFTER, ["mp4.duration-mismatch", "mdhd box of track 1"]],
     {"faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}),
    ("faac-1.50-adts", ".aac", ["{faac 1.50}", "-o", "{out}", "{in}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, True, ("FAAC 1.50.0", "High"), "LC")], {}, [], {"faad": "adts"}),
    ("faac-2.2", ".m4a", ["{faac 2.2}", "-o", "{out}", "{in}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, False, (FAAC_2_2, "Low"), "LC")], {}, [MOOV_AFTER, ["mp4.duration-mismatch", "tkhd box of track 1"]],
     {"faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}),
    ("faac-2.2-adts", ".aac", ["{faac 2.2}", "-o", "{out}", "{in}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, True, None, "LC")], {}, [], {"faad": "adts"}),
    ("faac-2.2-tags", ".m4a", ["{faac 2.2}", "--title", "Two tones", "--artist", "encspect", "--album", "Fixtures", "--year", "2026", "-o", "{out}", "{in}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, False, (FAAC_2_2, "Low"), "LC")], {}, [MOOV_AFTER, ["mp4.duration-mismatch", "tkhd box of track 1"]],
     {"faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}),
    ("faac-2.2-he", ".m4a", ["{faac 2.2}", "--object-type", "he-aac-v1", "-b", "48", "-o", "{out}", "{in}"],
     [stream("HE-AAC", 44100, 2, "Stereo", 2048, False, (FAAC_2_2, "Medium"), "HE-AAC")], {ASC + "/Sync Extension": "SBR present"},
     [MOOV_AFTER, ["mp4.duration-mismatch", "tkhd box of track 1"]], {"faad": {"label": "HE AAC", "channels": 2, "rate": 44100}}),
    ("faac-2.2-he-adts", ".aac", ["{faac 2.2}", "--object-type", "he-aac-v1", "-b", "48", "-o", "{out}", "{in}"],
     [stream("HE-AAC", 44100, 2, "Stereo", 2048, True, ("FAAC", "Medium"), "HE-AAC")], {STATS + "/SBR": "35 access units (100.0%)"},
     [IMPLICIT_SBR], {"skip": {"faad": FAAD_ADTS_RATE}}),
    ("faac-2.2-he-24k", ".m4a", ["{faac 2.2}", "--object-type", "he-aac-v1", "-b", "24", "-o", "{out}", "{in}"],
     [stream("HE-AAC", 44100, 2, "Stereo", 2048, False, (FAAC_2_2, "Medium"), "HE-AAC")], {ASC + "/Sync Extension": "SBR present"},
     [MOOV_AFTER, ["mp4.duration-mismatch", "tkhd box of track 1"]], {"faad": {"label": "HE AAC", "channels": 2, "rate": 44100}}),
    ("faac-2.2-he-24k-adts", ".aac", ["{faac 2.2}", "--object-type", "he-aac-v1", "-b", "24", "-o", "{out}", "{in}"],
     [stream("HE-AAC", 44100, 2, "Stereo", 2048, True, ("FAAC", "Medium"), "HE-AAC")], {STATS + "/SBR": "35 access units (100.0%)"},
     [IMPLICIT_SBR], {"skip": {"faad": FAAD_ADTS_RATE}}),
    ("faac-2.2-he-mono", ".m4a", ["{faac 2.2}", "--object-type", "he-aac-v1", "-b", "32", "-o", "{out}", "{mono48}"],
     [stream("HE-AAC", 48000, 1, "Mono", 2048, False, (FAAC_2_2, "Medium"), "HE-AAC")], {ASC + "/Sync Extension": "SBR present"},
     [MOOV_AFTER, ["mp4.duration-mismatch", "tkhd box of track 1"]], {"faad": {"label": "HE AAC", "channels": 2, "rate": 48000}}),
    ("faac-2.2-he-mono-adts", ".aac", ["{faac 2.2}", "--object-type", "he-aac-v1", "-b", "32", "-o", "{out}", "{mono48}"],
     [stream("HE-AAC", 48000, 1, "Mono", 2048, True, ("FAAC", "Medium"), "HE-AACv2", ffprobe_channels=2)], {STATS + "/SBR": "38 access units (100.0%)"},
     [["aac.implicit-sbr", "38 of 38 raw data blocks hold SBR data"]], {"skip": {"faad": FAAD_ADTS_RATE}}),
    # 2.0 and 2.1 count their SBR headers like 2.2 but send them again in their last access units, which are flush frames (FaacSbr).
    ("faac-2.1-he-24k-adts", ".aac", ["{faac 2.1}", "--object-type", "he-aac-v1", "-b", "24", "-o", "{out}", "{in}"],
     [stream("HE-AAC", 44100, 2, "Stereo", 2048, True, ("FAAC", "Medium"), "HE-AAC")], {STATS + "/SBR": "34 access units (100.0%)",
      STATS + "/SBR/SBR Headers": "5 headers"},
     [["aac.implicit-sbr", "34 of 34 raw data blocks hold SBR data"]], {"skip": {"faad": FAAD_ADTS_RATE}}),
    ("faac-2.0-he-24k", ".m4a", ["{faac 2.0}", "--object-type", "he-aac-v1", "-b", "24", "-o", "{out}", "{in}"],
     [stream("HE-AAC", 44100, 2, "Stereo", 2048, False, (FAAC_2_0, "Medium"), "HE-AAC")], {ASC + "/Sync Extension": "SBR present"},
     [MOOV_AFTER, ["mp4.duration-mismatch", "mdhd box of track 1"]], {"faad": {"label": "HE AAC", "channels": 2, "rate": 44100}}),
    # 2.2 writes the SBR data of each channel element after it, so a 5.1 stream is named; 2.0 and 2.1 write one payload after the whole
    # frame, typed as a channel pair element, which a 5.1 stream does not have (aac.sbr-without-channel-element), and so do not.
    ("faac-2.2-he-51-adts", ".aac", ["{faac 2.2}", "--object-type", "he-aac-v1", "-b", "16", "-o", "{out}", "{six}"],
     [stream("HE-AAC", 44100, 6, "5.1", 2048, True, ("FAAC", "Medium"), "HE-AAC")], {STATS + "/SBR": "35 access units (100.0%)"},
     [["aac.implicit-sbr", "35 of 35 raw data blocks hold SBR data"]], {"skip": {"faad": FAAD_ADTS_RATE}}),
    ("faac-2.1-he-51-adts", ".aac", ["{faac 2.1}", "--object-type", "he-aac-v1", "-b", "16", "-o", "{out}", "{six}"],
     [stream("HE-AAC", 44100, 6, "5.1", 2048, True, None, "HE-AAC")], {STATS + "/SBR": "34 access units (100.0%)"},
     [["aac.implicit-sbr", "34 of 34 raw data blocks hold SBR data"], ["aac.sbr-without-channel-element", "SBR data"]], {"skip": {"faad": FAAD_ADTS_RATE, "faad_trace": "faad2 does not read the stream: its SBR payload follows no channel element (aac.sbr-without-channel-element), and its trace holds one raw data block."}}),
    ("ffmpeg-fast", ".aac", ["ffmpeg", "-v", "error", "-y", "-i", "{mix}", "-c:a", "aac", "-b:a", "64k", "-aac_coder", "fast", "{out}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, True, (LAVC, "High"), "LC")], {}, [], {"faad": "adts"}),
    ("ffmpeg-no-tools", ".aac", ["ffmpeg", "-v", "error", "-y", "-i", "{mix}", "-c:a", "aac", "-b:a", "64k", "-aac_tns", "0", "-aac_pns", "0", "-aac_is", "0",
                                 "-aac_ms", "0", "{out}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, True, (LAVC, "High"), "LC")],
     {STATS + "/Channel Pairs/Mid/Side Masks/None (0)": "66 elements (100.0%)", STATS + "/Channel Pairs/Intensity Stereo": "0 elements (0.0%)",
      STATS + "/Noise Substitution": "0 channel streams (0.0%)", STATS + "/TNS": "0 channel streams (0.0%)"}, [], {"faad": "adts"}),
    ("ffmpeg-main", ".aac", ["ffmpeg", "-v", "error", "-y", "-i", "{mix}", "-c:a", "aac", "-b:a", "64k", "-profile:a", "aac_main", "-aac_pred", "1", "{out}"],
     [stream("AAC Main", 44100, 2, "Stereo", 1024, True, (LAVC, "High"), "Main")], {}, [], {"faad": "adts"}),
    ("ffmpeg-ltp", ".aac", ["ffmpeg", "-v", "error", "-y", "-i", "{mix}", "-c:a", "aac", "-b:a", "64k", "-profile:a", "aac_ltp", "-aac_ltp", "1", "-strict", "-2",
                            "{out}"],
     [stream("AAC LTP", 44100, 2, "Stereo", 1024, True, (LAVC, "High"), "LTP")], {}, [], {"faad": "adts"}),
    ("exhale", ".m4a", ["{exhale}", "3", "{in}", "{out}"],
     [stream("xHE-AAC (USAC)", 44100, 2, "Stereo", 1024, False, ("exhale 1.2.2", "Medium"))], {ASC + "/Audio Object Type": "42 (USAC)"},
     [["aac.usac-frames-not-analyzed", "usacIndependencyFlag"]],
     {"skip": {"ffprobe": "ffmpeg 6.1 does not implement audio object type 42 and lists no stream; MP4Box lists the samples instead.",
               "faad": "faad2 2.11 does not read USAC configurations."}}),
    ("exhale-sbr", ".m4a", ["{exhale}", "b", "{in}", "{out}"],
     [stream("xHE-AAC (USAC)", 44100, 2, "Stereo", 2048, False, ("exhale 1.2.2", "Medium"))], {ASC + "/Audio Object Type": "42 (USAC)"},
     [["aac.usac-frames-not-analyzed", "usacIndependencyFlag"]],
     {"skip": {"ffprobe": "ffmpeg 6.1 does not implement audio object type 42 and lists no stream; MP4Box lists the samples instead.",
               "faad": "faad2 2.11 does not read USAC configurations."}}),
    ("mp4box-mux", ".m4a", ["MP4Box", "-quiet", "-add", "{fixture ffmpeg-adts.aac}", "-new", "{out}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, False, (LAVC, "High"), "LC")], {},
     [["aac.encoder-claim-differs", "names \"GPAC-2.2.1"]], {"faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}),
    # Apple's AudioToolbox, through afconvert on the macOS 15.7 runners (import_macos_aac_fixtures.py, scripts/fixtures/macos): macOS only, so
    # the files are committed once and these entries only write the manifests (--manifests-only); "pcm/..." is a source file of wma_signals.py.
    ("apple-aace-m4a-cbr-mono44", ".m4a", ["afconvert", "-f", "m4af", "-d", "aace", "-s", "0", "-b", "32000", "pcm/mono44.wav", "{out}"],
     [stream("ER AAC ELD", 44100, 1, "Mono", 512, False, (APPLE, "Low"), "ELD")],
     {STREAM_TYPE: "5 (audio), reserved bit 0", GAPLESS + "Encoder Delay (iTunSMPB)": "328 samples", GAPLESS + "Samples (iTunSMPB)": "22050 samples", GAPLESS + "Padding (iTunSMPB)": "150 samples"},
     [], {"skip": {"faad": "faad2 reads AAC LD and ELD in no MP4 file here; ffmpeg decodes them.", "faad_trace": "faad2 does not decode the file."}}),
    ("apple-aacl-m4a-cbr-mono44", ".m4a", ["afconvert", "-f", "m4af", "-d", "aacl", "-s", "0", "-b", "32000", "pcm/mono44.wav", "{out}"],
     [stream("ER AAC LD", 44100, 1, "Mono", 512, False, (APPLE, "Low"), "LD")],
     {STREAM_TYPE: "5 (audio), reserved bit 0", GAPLESS + "Encoder Delay (iTunSMPB)": "584 samples", GAPLESS + "Samples (iTunSMPB)": "22050 samples", GAPLESS + "Padding (iTunSMPB)": "406 samples"},
     [], {"skip": {"faad": "faad2 reads AAC LD and ELD in no MP4 file here; ffmpeg decodes them.", "faad_trace": "faad2 does not decode the file."}}),
    ("apple-he-m4a-cbr-mono44", ".m4a", ["afconvert", "-f", "m4af", "-d", "aach", "-s", "0", "-b", "32000", "pcm/mono44h.wav", "{out}"],
     [stream("HE-AAC", 44100, 1, "Mono", 2048, False, (APPLE, "Low"), "HE-AAC", ffprobe_channels=2)],
     {STREAM_TYPE: "5 (audio), reserved bit 0", GAPLESS + "Encoder Delay (iTunSMPB)": "2112 samples", GAPLESS + "Samples (iTunSMPB)": "22050 samples", GAPLESS + "Padding (iTunSMPB)": "1438 samples", ASC + "/Sync Extension": "SBR present"},
     [], {"faad": {"label": "HE AAC", "channels": 2, "rate": 44100}}),
    ("apple-he-m4a-cbr-st44", ".m4a", ["afconvert", "-f", "m4af", "-d", "aach", "-s", "0", "-b", "64000", "pcm/st44h.wav", "{out}"],
     [stream("HE-AAC", 44100, 2, "Stereo", 2048, False, (APPLE, "Low"), "HE-AAC")],
     {STREAM_TYPE: "5 (audio), reserved bit 0", GAPLESS + "Encoder Delay (iTunSMPB)": "2112 samples", GAPLESS + "Samples (iTunSMPB)": "22050 samples", GAPLESS + "Padding (iTunSMPB)": "1438 samples", ASC + "/Sync Extension": "SBR present"},
     [], {"faad": {"label": "HE AAC", "channels": 2, "rate": 44100}}),
    ("apple-hev2-adts-cbr-st44", ".aac", ["afconvert", "-f", "adts", "-d", "aacp", "-s", "0", "-b", "32000", "pcm/st44h.wav", "{out}"],
     [stream("HE-AAC v2", 44100, 2, "Parametric stereo", 2048, False, (APPLE, "Low"), "HE-AACv2")],
     {},
     [["aac.implicit-sbr", "25 of 25 raw data blocks hold SBR data, and 25 carry the parametric stereo extension"]], {"skip": {"faad": FAAD_ADTS_RATE}}),
    ("apple-hev2-m4a-cbr-st44", ".m4a", ["afconvert", "-f", "m4af", "-d", "aacp", "-s", "0", "-b", "32000", "pcm/st44h.wav", "{out}"],
     [stream("HE-AAC v2", 44100, 2, "Parametric stereo", 2048, False, (APPLE, "Low"), "HE-AACv2")],
     {STREAM_TYPE: "5 (audio), reserved bit 0", GAPLESS + "Encoder Delay (iTunSMPB)": "2112 samples", GAPLESS + "Samples (iTunSMPB)": "22050 samples", GAPLESS + "Padding (iTunSMPB)": "1438 samples", ASC + "/Sync Extension": "SBR and PS present"},
     [], {"faad": {"label": "HE AAC", "channels": 2, "rate": 44100}}),
    ("apple-lc-adts-cbr-st44", ".aac", ["afconvert", "-f", "adts", "-d", "aac", "-s", "0", "-b", "128000", "pcm/st44.wav", "{out}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, False, (APPLE, "Low"), "LC")],
     {},
     [], {"faad": "adts"}),
    ("apple-lc-m4a-cbr-st44", ".m4a", ["afconvert", "-f", "m4af", "-d", "aac", "-s", "0", "-b", "128000", "pcm/st44.wav", "{out}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, False, (APPLE, "Low"), "LC")],
     {STREAM_TYPE: "5 (audio), reserved bit 0", GAPLESS + "Encoder Delay (iTunSMPB)": "2112 samples", GAPLESS + "Samples (iTunSMPB)": "22050 samples", GAPLESS + "Padding (iTunSMPB)": "414 samples"},
     [], {"faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}),
]


def check_versions():
    ffmpeg = subprocess.run(["ffmpeg", "-version"], capture_output=True, text=True).stdout.splitlines()[0]
    if not ffmpeg.startswith(FFMPEG_VERSION):
        sys.exit(f"{FFMPEG_VERSION} is required, found {ffmpeg}")
    faac = subprocess.run(["faac", "--help"], capture_output=True, text=True)
    if FAAC_VERSION not in faac.stdout + faac.stderr:
        sys.exit(f"{FAAC_VERSION} is required")


def write_tone(path, channels, rate=44100):
    sources = ["-f", "lavfi", "-i", f"sine=f=440:d=1.5:r={rate}"]
    if channels == 2:
        sources += ["-f", "lavfi", "-i", f"sine=f=660:d=1.5:r={rate}", "-filter_complex", "amerge"]
    subprocess.run(["ffmpeg", "-v", "error", "-y", *sources, "-ac", str(channels), "-c:a", "pcm_s16le", str(path)], check=True)


def write_surround(path):
    """Six tones, one per channel of a 5.1 layout, for 1.5 seconds."""
    sources = [arg for frequency in (440, 660, 550, 330, 880, 110) for arg in ("-f", "lavfi", "-i", f"sine=f={frequency}:d=1.5:r=44100")]
    subprocess.run(["ffmpeg", "-v", "error", "-y", *sources, "-filter_complex", "amerge=inputs=6", "-ac", "6", "-c:a", "pcm_s16le", str(path)], check=True)


def write_mix(path):
    """Pink noise with a 440 Hz tone, in both channels, the second 2 dB quieter: material on which ffmpeg's encoder uses mid/side,
    intensity stereo and noise substitution."""
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "anoisesrc=d=1.5:c=pink:r=44100:a=0.3:s=7", "-f", "lavfi", "-i", "sine=f=440:d=1.5:r=44100",
                    "-filter_complex", "[0][1]amix=inputs=2:normalize=0,asplit[a][b];[b]volume=0.8[c];[a][c]amerge", "-ac", "2", "-c:a", "pcm_s16le", str(path)],
                   check=True)


def run(command, values, library):
    env = None
    args = list(command)
    if args[0].startswith("FDKAAC_SBR_SIGNALING="):
        env = {"FDKAAC_SBR_SIGNALING": args.pop(0).split("=", 1)[1]}
    resolved = []
    for arg in args:
        match = re.fullmatch(r"\{fixture (.+)\}", arg)
        faac = re.fullmatch(r"\{faac (.+)\}", arg)
        resolved.append(str(OUT / match.group(1)) if match else str(values["faacs"][faac.group(1)]) if faac else arg.format(**values))
    environment = None
    if resolved[0] == values["fdkaac"] or env:
        import os
        environment = dict(os.environ, LD_LIBRARY_PATH=str(library), **(env or {}))
    subprocess.run(resolved, check=True, capture_output=True, env=environment)


def description(command):
    if command[0] == "afconvert":
        return f"{' '.join(command)} on the deterministic signal of wma_signals.py ({command[-2]})."
    return f"{' '.join(command)} on 1.5 s of {'pink noise with a 440 Hz tone' if '{mix}' in command else 'a 440/660 Hz tone'}."


def manifest_for(name, path, command, streams, fields, findings, check, values):
    data = path.read_bytes()
    is_mp4 = path.suffix != ".aac"
    usac = streams[0]["codec"].startswith("xHE")
    units = [[None, s] for s in mp4box_sample_sizes(path)] if usac else ffprobe_packets(path)
    boxes = [[p, None, s] for p, s in with_unlisted(path, mp4box_boxes(path))] if is_mp4 else []
    out = []
    for entry in streams:
        entry = dict(entry)
        entry["samples"] = len(units) * entry.pop("frame")
        out.append(entry)
    crosscheck = {"ffprobe": not usac, "mp4box": is_mp4, "mp4box_samples": usac}
    faad = check.get("faad")
    if faad == "adts":
        faad = {"seconds": f"{len(units) * 1024 / streams[0]['sample_rate']:.3f}", "rate": streams[0]["sample_rate"]}
    crosscheck["faad"] = faad
    if "skip" in check:
        crosscheck["skip"] = check["skip"]
    manifest = {
        "description": description(command),
        "encoder": {"command": command},
        "length": len(data),
        "format": "MP4" if is_mp4 else "AAC",
        "boxes": boxes,
        "streams": out,
        "units": units,
        "fields": {**(mp4box_fields(path) if is_mp4 else {}), **fields},
        "findings": findings,
        "crosscheck": crosscheck,
    }
    if name == "fdkaac-crc-mono" and values is not None:
        manifest["fdk_crc_errors"] = fdk_tool.crc_errors(values["checker"], values["library"], path)
    return manifest


def check_hand_built_crcs(checker, library):
    """fdk-aac's decoder must reject exactly the hand-built ADTS frames whose CRC was written wrong."""
    expected = {"crc-mono.aac": [], "crc-mono-damaged.aac": [5], "crc-blocks.aac": [], "crc-blocks-damaged.aac": [10], "crc-stereo.aac": [],
                "crc-stereo-damaged.aac": [3], "crc-short-element.aac": []}
    for name, errors in expected.items():
        found = fdk_tool.crc_errors(checker, library, ROOT / "adts" / name)
        if found != errors:
            sys.exit(f"fdk-aac rejects access units {found} of adts/{name} for their CRC, expected {errors}")
    print("fdk-aac's decoder confirms the hand-built ADTS CRCs.")


def rewrite_manifests(names):
    """Rewrites the manifests of the committed files from their entries above and the tools that read them, without the encoders; fdk-aac's CRC
    verdicts are kept from the manifest."""
    for name, extension, command, streams, fields, findings, check in FIXTURES:
        if names and name not in names:
            continue
        path = OUT / (name + extension)
        manifest_path = OUT / (name + ".expected.json")
        old = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else {}
        manifest = manifest_for(name, path, command, streams, fields, findings, check, None)
        if "fdk_crc_errors" in old:
            manifest["fdk_crc_errors"] = old["fdk_crc_errors"]
        manifest_path.write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")


def main(names):
    if names[:1] == ["--manifests-only"]:
        rewrite_manifests(names[1:])
        return
    check_versions()
    selected = [f for f in FIXTURES if (not names or f[0] in names) and f[2][0] != "afconvert"]
    commands = [" ".join(f[2]) for f in selected]
    # FAAC's releases and the other encoders are built only for the fixtures that use them.
    versions = sorted({m for c in commands for m in re.findall(r"\{faac ([^}]+)\}", c)})
    uses_fdk = any("{fdkaac}" in c for c in commands) or not names
    uses_exhale = any("{exhale}" in c for c in commands)
    fdkaac = library = checker = exhale = None
    if uses_fdk:
        fdkaac, library, checker = fdk_tool.build()
        check_hand_built_crcs(checker, library)
    if uses_exhale:
        exhale = exhale_tool.build()
    faacs = {version: faac_tool.build(version) for version in versions}
    OUT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as directory:
        temp = pathlib.Path(directory)
        stereo = temp / "tone.wav"
        mono = temp / "mono.wav"
        mono48 = temp / "mono48.wav"
        write_tone(stereo, 2)
        write_tone(mono, 1)
        write_tone(mono48, 1, 48000)
        mix = temp / "mix.wav"
        write_mix(mix)
        six = temp / "six.wav"
        write_surround(six)
        for name, extension, command, streams, fields, findings, check in selected:
            path = OUT / (name + extension)
            values = {"in": str(stereo), "mono": str(mono), "mono48": str(mono48), "six": str(six), "mix": str(mix), "out": str(path), "fdkaac": str(fdkaac),
                      "exhale": str(exhale), "faacs": faacs, "checker": checker, "library": library}
            # FAAC refuses to overwrite its output.
            path.unlink(missing_ok=True)
            run(command, values, library)
            manifest = manifest_for(name, path, command, streams, fields, findings, check, values)
            (OUT / (name + ".expected.json")).write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
            print(f"{path.name}: {len(manifest['units'])} access units")
    for tool in (fdkaac and fdkaac.parents[1], exhale and exhale.parents[1], *(binary.parent for binary in faacs.values())):
        if tool:
            shutil.rmtree(tool, ignore_errors=True)


if __name__ == "__main__":
    main(sys.argv[1:])
