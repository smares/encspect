#!/usr/bin/env python3
"""Writes MP4 and raw AAC fixtures with real encoders and muxers into tests/fixtures/aac: ffmpeg 6.1 with its
own AAC encoder (M4A, ADTS, fragmented MP4, and MP3 in MP4 with libmp3lame), fdkaac 1.0.0 with libfdk-aac
4.0.1 built from source (fdk_tool.py; LC, VBR, HE-AAC and HE-AAC v2 with both explicit signaling modes, and
ADTS with CRCs), FAAC 1.30, exhale 1.2.2 for xHE-AAC (exhale_tool.py) and GPAC's MP4Box 2.2 as a muxer. Each
encodes 1.5 seconds of a 440/660 Hz tone.

Each <name>.expected.json manifest records the command and the encoder's version, and what the analyzer
must report, mostly from independent tools at the time of encoding:

- "boxes": the boxes MP4Box -diso lists, as path and size (the offset is null: MP4Box gives none).
- "units": the packets ffprobe lists with edit lists ignored (offset and size), or for xHE-AAC, which ffmpeg
  6.1 cannot read, the sample sizes MP4Box -dtsx lists (offset null).
- "streams": the codec, rate, channels, samples and encoder the settings give, with ffprobe's profile where
  ffmpeg names one, and "ffprobe_sample_rate" where ffmpeg finds SBR the configuration does not signal.
- "fields", "findings" and "crosscheck" as in make_mp4_fixtures.py; "fdk_crc_errors" for protected ADTS,
  the access units fdk-aac's decoder rejects for their CRC.

    python3 scripts/fixtures/make_external_aac_fixtures.py --manifests-only [name ...]

rewrites the manifests from the committed files without the encoders. Running the script also checks the
hand-built ADTS CRC fixtures with fdk-aac's decoder, the one decoder at
hand that verifies ADTS CRCs, and fails where its verdicts differ from what their manifests imply.

Needs ffmpeg 6.1, faac 1.30, MP4Box (gpac 2.2), faad, git, cmake, make and a C and C++ compiler:

    apt-get install ffmpeg faac gpac faad cmake
"""

import json
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile

import exhale_tool
import fdk_tool
from crosscheck_mp4_fixtures import faad_info, ffprobe_packets, ffprobe_streams, mp4box_boxes, mp4box_sample_sizes

ROOT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures"
OUT = ROOT / "aac"
FFMPEG_VERSION = "ffmpeg version 6.1.1-3ubuntu5"
FAAC_VERSION = "FAAC 1.30"
LAVC = "FFmpeg (Lavc60.31.102)"
FDKAAC = "fdkaac 1.0.0 (libfdk-aac 4.0.1)"
ASC = "MP4/moov/trak/mdia/minf/stbl/stsd/mp4a/esds/ES_Descriptor/DecoderConfigDescriptor/DecoderSpecificInfo/AudioSpecificConfig"
IMPLICIT = ["aac.implicit-signaling-not-checked", "signaled implicitly"]
MOOV_AFTER = ["mp4.moov-after-mdat", "not optimized for streaming"]
LC_IMPLICIT = "AAC LC (no SBR signaled explicitly)"


def stream(codec, rate, channels, mode, frame, vbr, encoder, profile=None, **extra):
    entry = {"codec": codec, "sample_rate": rate, "channels": channels, "channel_mode": mode, "frame": frame, "is_vbr": vbr}
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
     [stream(LC_IMPLICIT, 44100, 2, "Stereo", 1024, True, (LAVC, "High"), "LC")], {"ADTS/Frame/Buffer Fullness": "0x7FF (variable bitrate)"}, [IMPLICIT],
     {"faad": "adts"}),
    ("ffmpeg-fragmented", ".mp4", ["ffmpeg", "-v", "error", "-y", "-i", "{in}", "-c:a", "aac", "-b:a", "128k", "-movflags", "frag_keyframe+empty_moov+default_base_moof",
                                   "-frag_duration", "500000", "{out}"],
     [stream("AAC LC", 44100, 2, "Stereo", 1024, False, (LAVC, "High"), "LC")], {}, [],
     {"skip": {"faad": "faad2's MP4 reader reads samples from the sample tables only, which a fragmented file leaves empty."}}),
    ("ffmpeg-mp3", ".mp4", ["ffmpeg", "-v", "error", "-y", "-i", "{in}", "-c:a", "libmp3lame", "-b:a", "128k", "{out}"],
     [stream("MPEG-1 Layer III", 44100, 2, "Joint stereo", 1152, False, ("LAME 3.100", "Low"))], {}, [MOOV_AFTER], {"skip": {"faad": "faad2 reads AAC only."}}),
    ("fdkaac-lc", ".m4a", ["{fdkaac}", "-S", "-p", "2", "-b", "128000", "-G", "2", "-o", "{out}", "{in}"],
     [stream(LC_IMPLICIT, 44100, 2, "Stereo", 1024, False, (FDKAAC, "Low"), "LC")], {"MP4/moov/trak/Gapless/Encoder Delay (Edit List)": "2048 samples"},
     [MOOV_AFTER, IMPLICIT, ["mp4.duration-mismatch", "tkhd box of track 1 gives a duration of 68608"]],
     {"faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}),
    ("fdkaac-lc-vbr", ".m4a", ["{fdkaac}", "-S", "-p", "2", "-m", "4", "-o", "{out}", "{in}"],
     [stream(LC_IMPLICIT, 44100, 2, "Stereo", 1024, True, (FDKAAC, "Low"), "LC")], {}, [MOOV_AFTER, IMPLICIT],
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
     [stream(LC_IMPLICIT, 44100, 1, "Mono", 1024, False, None, "LC")], {"ADTS/Frame[1]/CRC/Check": "verified"},
     [IMPLICIT, ["aac.crc-not-verified", "covers a channel element whose end is not known"]], {"faad": "adts"}),
    ("fdkaac-he-adts", ".aac", ["{fdkaac}", "-S", "-p", "5", "-b", "48000", "-f", "2", "-o", "{out}", "{in}"],
     [stream(LC_IMPLICIT, 22050, 2, "Stereo", 1024, False, None, "HE-AAC", ffprobe_sample_rate=44100)], {}, [IMPLICIT],
     {"skip": {"faad": "faad2 finds the SBR data that ADTS can only signal implicitly, which the analyzer does not check yet (slice B); "
                       "ffprobe reports the doubled rate for the same reason."}}),
    ("faac", ".m4a", ["faac", "-o", "{out}", "{in}"],
     [stream(LC_IMPLICIT, 44100, 2, "Stereo", 1024, False, ("FAAC 1.30", "Low"), "LC")], {}, [MOOV_AFTER, IMPLICIT, ["mp4.duration-mismatch", "mdhd box of track 1"]],
     {"faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}),
    ("faac-adts", ".aac", ["faac", "-o", "{out}", "{in}"],
     [stream(LC_IMPLICIT, 44100, 2, "Stereo", 1024, True, None, "LC")], {}, [IMPLICIT], {"faad": "adts"}),
    ("exhale", ".m4a", ["{exhale}", "3", "{in}", "{out}"],
     [stream("xHE-AAC (USAC)", 44100, 2, "Stereo", 1024, False, ("exhale 1.2.2", "Low"))], {ASC + "/Audio Object Type": "42 (USAC)"},
     [["aac.usac-frames-not-analyzed", "usacIndependencyFlag"]],
     {"skip": {"ffprobe": "ffmpeg 6.1 does not implement audio object type 42 and lists no stream; MP4Box lists the samples instead.",
               "faad": "faad2 2.11 does not read USAC configurations."}}),
    ("exhale-sbr", ".m4a", ["{exhale}", "b", "{in}", "{out}"],
     [stream("xHE-AAC (USAC)", 44100, 2, "Stereo", 2048, False, ("exhale 1.2.2", "Low"))], {ASC + "/Audio Object Type": "42 (USAC)"},
     [["aac.usac-frames-not-analyzed", "usacIndependencyFlag"]],
     {"skip": {"ffprobe": "ffmpeg 6.1 does not implement audio object type 42 and lists no stream; MP4Box lists the samples instead.",
               "faad": "faad2 2.11 does not read USAC configurations."}}),
    ("mp4box-mux", ".m4a", ["MP4Box", "-quiet", "-add", "{fixture ffmpeg-adts.aac}", "-new", "{out}"],
     [stream(LC_IMPLICIT, 44100, 2, "Stereo", 1024, False, (LAVC, "High"), "LC")], {},
     [IMPLICIT, ["aac.encoder-claim-differs", "names \"GPAC-2.2.1"]], {"faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}),
]


def check_versions():
    ffmpeg = subprocess.run(["ffmpeg", "-version"], capture_output=True, text=True).stdout.splitlines()[0]
    if not ffmpeg.startswith(FFMPEG_VERSION):
        sys.exit(f"{FFMPEG_VERSION} is required, found {ffmpeg}")
    faac = subprocess.run(["faac", "--help"], capture_output=True, text=True)
    if FAAC_VERSION not in faac.stdout + faac.stderr:
        sys.exit(f"{FAAC_VERSION} is required")


def write_tone(path, channels):
    sources = ["-f", "lavfi", "-i", "sine=f=440:d=1.5:r=44100"]
    if channels == 2:
        sources += ["-f", "lavfi", "-i", "sine=f=660:d=1.5:r=44100", "-filter_complex", "amerge"]
    subprocess.run(["ffmpeg", "-v", "error", "-y", *sources, "-ac", str(channels), "-c:a", "pcm_s16le", str(path)], check=True)


def run(command, values, library):
    env = None
    args = list(command)
    if args[0].startswith("FDKAAC_SBR_SIGNALING="):
        env = {"FDKAAC_SBR_SIGNALING": args.pop(0).split("=", 1)[1]}
    resolved = []
    for arg in args:
        match = re.fullmatch(r"\{fixture (.+)\}", arg)
        resolved.append(str(OUT / match.group(1)) if match else arg.format(**values))
    environment = None
    if resolved[0] == values["fdkaac"] or env:
        import os
        environment = dict(os.environ, LD_LIBRARY_PATH=str(library), **(env or {}))
    subprocess.run(resolved, check=True, capture_output=True, env=environment)


def manifest_for(name, path, command, streams, fields, findings, check, values):
    data = path.read_bytes()
    is_mp4 = path.suffix != ".aac"
    usac = streams[0]["codec"].startswith("xHE")
    units = [[None, s] for s in mp4box_sample_sizes(path)] if usac else ffprobe_packets(path)
    boxes = [[p, None, s] for p, s in mp4box_boxes(path)] if is_mp4 else []
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
        "description": f"{' '.join(command)} on 1.5 s of a 440/660 Hz tone.",
        "encoder": {"command": command},
        "length": len(data),
        "format": "MP4" if is_mp4 else "AAC",
        "boxes": boxes,
        "streams": out,
        "units": units,
        "fields": fields,
        "findings": findings,
        "crosscheck": crosscheck,
    }
    if name == "fdkaac-crc-mono" and values is not None:
        manifest["fdk_crc_errors"] = fdk_tool.crc_errors(values["checker"], values["library"], path)
    return manifest


def check_hand_built_crcs(checker, library):
    """fdk-aac's decoder must reject exactly the hand-built ADTS frames whose CRC was written wrong."""
    expected = {"crc-mono.aac": [], "crc-mono-damaged.aac": [5], "crc-blocks.aac": [], "crc-blocks-damaged.aac": [10]}
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
        old = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest = manifest_for(name, path, command, streams, fields, findings, check, None)
        if "fdk_crc_errors" in old:
            manifest["fdk_crc_errors"] = old["fdk_crc_errors"]
        manifest_path.write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")


def main(names):
    if names[:1] == ["--manifests-only"]:
        rewrite_manifests(names[1:])
        return
    check_versions()
    fdkaac, library, checker = fdk_tool.build()
    exhale = exhale_tool.build()
    check_hand_built_crcs(checker, library)
    OUT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as directory:
        temp = pathlib.Path(directory)
        stereo = temp / "tone.wav"
        mono = temp / "mono.wav"
        write_tone(stereo, 2)
        write_tone(mono, 1)
        for name, extension, command, streams, fields, findings, check in FIXTURES:
            if names and name not in names:
                continue
            path = OUT / (name + extension)
            values = {"in": str(stereo), "mono": str(mono), "out": str(path), "fdkaac": str(fdkaac), "exhale": str(exhale),
                      "checker": checker, "library": library}
            run(command, values, library)
            manifest = manifest_for(name, path, command, streams, fields, findings, check, values)
            (OUT / (name + ".expected.json")).write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
            print(f"{path.name}: {len(manifest['units'])} access units")
    shutil.rmtree(fdkaac.parents[1], ignore_errors=True)
    shutil.rmtree(exhale.parents[1], ignore_errors=True)


if __name__ == "__main__":
    main(sys.argv[1:])
