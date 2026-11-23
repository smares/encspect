#!/usr/bin/env python3
"""Writes the encoder-made Matroska and WebM fixtures in tests/fixtures/matroska (external-*).

FFmpeg 6.1 (libavformat's matroska and webm muxers, matroskaenc.c) and mkvmerge from MKVToolNix (Ubuntu 24.04's mkvtoolnix package, v82.0)
mux the audio of the project's other fixtures, copied unchanged, into Matroska and WebM: AAC (LC, HE-AAC and HE-AAC v2), MPEG audio Layers II
and III, AC-3, E-AC-3, DTS, Vorbis, Opus (also with the ambisonics mapping), WavPack; and FFmpeg's own encoders write FLAC, PCM, ALAC, TTA and Dolby
TrueHD, which the analyzer reports from the TrackEntry only. The muxers' options give a live stream (unknown sizes), no CRC-32 elements, many small
Clusters, a track with video, two audio tracks, tags, chapters and an attachment, a different TimestampScale, no Cues, zlib compression of the frames
and WebM mode.

The muxers write random UIDs, so the files are committed and --manifests-only recomputes the manifests from them without the tools. Each
manifest has the parts of a hand-built one (make_matroska_fixtures.py), worked out by matroska_model.py from the file's bytes, plus the tool
and its arguments ("encoder") and the streams and findings the analyzer must report. crosscheck_matroska_fixtures.py confirms them with ffprobe,
mkvinfo and MediaInfo.
"""

import json
import math
import pathlib
import shutil
import struct
import subprocess
import sys
import tempfile
import wave

import matroska_model as m
import old_mkvmerge_tool

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "matroska"
FIXTURES_DIR = ROOT / "tests" / "fixtures"

FFMPEG = "ffmpeg"
MKVMERGE = "mkvmerge"

# name: (tool, inputs, arguments, expected streams as (codec, sample rate, channels), expected findings as (code, contains)).
# Inputs are paths under tests/fixtures (or the names of the files written below); the arguments go before the inputs for ffmpeg and
# between the output and the inputs for mkvmerge, which takes its options per file where they apply.
NOT_READ = ("matroska.codec-not-analyzed", "frames are not analyzed yet")
LAME_VS_MKVMERGE = ("mpeg-audio.writer-claim-differs", "Lavc60.31")
VORBIS_VENDOR = ("vorbis.writer-claim-differs", "Lavf60.16.100")
NOT_READ_ACM = ("matroska.codec-not-analyzed", "A_MS/ACM, format tag")

FIXTURES = {
    "external-ffmpeg-aac-lc": ("ffmpeg", ["aac/faac-1.50.m4a"], ["-c", "copy"], [("AAC", 44100, 2)], [], "mka"),
    "external-ffmpeg-aac-he": ("ffmpeg", ["aac/faac-2.2-he.m4a"], ["-c", "copy"], [("AAC", 44100, 2)], [], "mka"),
    "external-ffmpeg-aac-hev2": ("ffmpeg", ["aac/apple-hev2-m4a-cbr-st44.m4a"], ["-c", "copy"], [("AAC", 44100, 2)], [], "mka"),
    "external-ffmpeg-mp3": ("ffmpeg", ["mpeg-audio/external-ffmpeg-cbr.mp3"], ["-c", "copy"], [("MPEG-1 Layer III", 44100, 2)], [], "mka"),
    "external-ffmpeg-mp2": ("ffmpeg", ["mpeg-audio/external-ffmpeg-mp2.mp2"], ["-c", "copy"], [("MPEG-1 Layer II", 48000, 2)], [], "mka"),
    "external-ffmpeg-ac3-51": ("ffmpeg", ["ac3/external-ffmpeg-5.1-448k.ac3"], ["-c", "copy"], [("AC-3", 48000, 6)], [], "mka"),
    "external-ffmpeg-eac3-51": ("ffmpeg", ["ac3/external-ffmpeg-eac3-5.1-256k.eac3"], ["-c", "copy"], [("E-AC-3", 48000, 6)], [], "mka"),
    "external-ffmpeg-dts-51": ("ffmpeg", ["dts/external-ffmpeg-5.1-48k-768k.dts"], ["-c", "copy"], [("DTS", 48000, 6)], [], "mka"),
    "external-ffmpeg-vorbis": ("ffmpeg", ["ogg/external-ffmpeg-libvorbis.ogg"], ["-c", "copy"], [("Vorbis", 44100, 2)], [], "mka"),
    "external-ffmpeg-vorbis-webm": ("ffmpeg", ["ogg/external-ffmpeg-libvorbis.ogg"], ["-c", "copy"], [("Vorbis", 44100, 2)], [], "webm"),
    "external-ffmpeg-opus": ("ffmpeg", ["ogg/external-ffmpeg-libopus.opus"], ["-c", "copy"], [("Opus", 48000, 2)], [], "mka"),
    "external-ffmpeg-opus-webm": ("ffmpeg", ["ogg/external-ffmpeg-libopus.opus"], ["-c", "copy"], [("Opus", 48000, 2)], [], "webm"),
    "external-ffmpeg-opus-ambisonics": ("ffmpeg", ["ogg/external-ffmpeg-libopus-ambisonics.opus"], ["-c", "copy"], [("Opus", 48000, 4)], [], "mka"),
    "external-ffmpeg-wavpack": ("ffmpeg", ["wavpack/external-ffmpeg-apev2-tag.wv"], ["-c", "copy"], [("WavPack lossless", 44100, 2)], [], "mka"),
    "external-ffmpeg-flac": ("ffmpeg", ["sine"], ["-c:a", "flac"], [("FLAC", 44100, 1)], [], "mka"),
    "external-ffmpeg-wavpack-51": ("ffmpeg", ["wavpack/external-ffmpeg-51-16.wv"], ["-c", "copy"], [("WavPack lossless", 48000, 6)], [], "mka"),
    "external-mkvmerge-wavpack-51": ("mkvmerge", ["wavpack/external-ffmpeg-51-16.wv"], [], [("WavPack lossless", 48000, 6)], [], "mka"),
    "external-ffmpeg-flac-6ch": ("ffmpeg", ["flac/external-16-6ch.flac"], ["-c", "copy"], [("FLAC", 48000, 6)], [], "mka"),
    "external-mkvmerge-flac-6ch": ("mkvmerge", ["flac/external-16-6ch.flac"], [], [("FLAC", 48000, 6)], [], "mka"),
    "external-ffmpeg-mlp-51": ("ffmpeg", ["truehd/external-mlp-51-48k-24.mlp"], ["-c", "copy"], [("MLP", 48000, 6)], [], "mka"),
    "external-mkvmerge-mlp-51": ("mkvmerge", ["truehd/external-mlp-51-48k-24.mlp"], [], [("MLP", 48000, 6)], [], "mka"),
    "external-mkvmerge-truehd-50": ("mkvmerge", ["truehd/external-truehd-50-48k-24.thd"], [], [("Dolby TrueHD", 48000, 5)], [], "mka"),
    "external-mkvmerge-zlib-flac": ("mkvmerge", ["flac/external-16-6ch.flac"], ["--compression", "0:zlib"], [("FLAC", 48000, 6)], [], "mka"),
    "external-mkvmerge-zlib-wavpack": ("mkvmerge", ["wavpack/external-ffmpeg-51-16.wv"], ["--compression", "0:zlib"], [("WavPack", 48000, 6)], [("matroska.track-encoding-not-read", "zlib compression")], "mka"),
    # Old releases of mkvmerge, run under Wine (old_mkvmerge_tool.py), which strip the headers of MP3 and DTS (4.5.0 to 5.9.0) and of AC-3 (5.4.0 to 5.9.0) by default.
    "external-mkvmerge450-mp3-header-stripped": ("mkvmerge-4.5.0", ["mpeg-audio/external-ffmpeg-cbr.mp3"], [], [("MPEG-1 Layer III", 44100, 2)], [LAME_VS_MKVMERGE], "mka"),
    "external-mkvmerge450-dts-header-stripped": ("mkvmerge-4.5.0", ["dts/external-ffmpeg-5.1-48k-768k.dts"], [], [("DTS", 48000, 6)], [], "mka"),
    "external-mkvmerge540-ac3-header-stripped": ("mkvmerge-5.4.0", ["ac3/external-ffmpeg-stereo-192k.ac3"], [], [("AC-3", 48000, 2)], [], "mka"),
    "external-mkvmerge590-mp3-header-stripped": ("mkvmerge-5.9.0", ["mpeg-audio/external-ffmpeg-cbr.mp3"], [], [("MPEG-1 Layer III", 44100, 2)], [LAME_VS_MKVMERGE], "mka"),
    "external-mkvmerge590-dts-header-stripped": ("mkvmerge-5.9.0", ["dts/external-ffmpeg-5.1-48k-768k.dts"], [], [("DTS", 48000, 6)], [], "mka"),
    "external-mkvmerge590-ac3-header-stripped": ("mkvmerge-5.9.0", ["ac3/external-ffmpeg-stereo-192k.ac3"], [], [("AC-3", 48000, 2)], [], "mka"),
    "external-ffmpeg-pcm": ("ffmpeg", ["sine"], ["-c:a", "pcm_s16le"], [("PCM", 44100, 1)], [], "mka"),
    "external-ffmpeg-pcm-s16be": ("ffmpeg", ["sine"], ["-c:a", "pcm_s16be"], [("PCM", 44100, 1)], [], "mka"),
    "external-ffmpeg-pcm-s24le-stereo": ("ffmpeg", ["sine"], ["-ac", "2", "-t", "0.2", "-c:a", "pcm_s24le"], [("PCM", 44100, 2)], [], "mka"),
    "external-ffmpeg-pcm-s32le": ("ffmpeg", ["sine"], ["-t", "0.25", "-c:a", "pcm_s32le"], [("PCM", 44100, 1)], [], "mka"),
    "external-ffmpeg-pcm-f32le": ("ffmpeg", ["sine"], ["-t", "0.25", "-c:a", "pcm_f32le"], [("PCM (floating-point)", 44100, 1)], [], "mka"),
    "external-ffmpeg-pcm-f64le": ("ffmpeg", ["sine"], ["-t", "0.15", "-c:a", "pcm_f64le"], [("PCM (floating-point)", 44100, 1)], [], "mka"),
    "external-ffmpeg-pcm-u8": ("ffmpeg", ["sine"], ["-c:a", "pcm_u8"], [("PCM", 44100, 1)], [], "mka"),
    "external-ffmpeg-pcm-51": ("ffmpeg", ["sine"], ["-ar", "48000", "-ac", "6", "-t", "0.1", "-c:a", "pcm_s16le"], [("PCM", 48000, 6)], [], "mka"),
    "external-mkvmerge-pcm": ("mkvmerge", ["sine.wav"], [], [("PCM", 44100, 1)], [], "mka"),
    "external-ffmpeg-alac": ("ffmpeg", ["sine"], ["-c:a", "alac"], [("Apple Lossless", 44100, 1)], [], "mka"),
    # The sine ends in near silence: codes of a bit leave the last frame's count to the Segment Duration (docs/formats/tta.md, "TTA in Matroska").
    "external-ffmpeg-tta": ("ffmpeg", ["sine"], ["-c:a", "tta"], [("TTA", 44100, 1)], [("tta.last-frame-samples-ambiguous", "44099 to 44100")], "mka"),
    # A stream that ends one sample into its third frame, copied without the header: the decoder takes the last frame's samples from the segment's Duration.
    "external-ffmpeg-tta-last-frame-one-sample": ("ffmpeg", ["tta/external-libtta-16-stereo-32000-last-frame-one-sample.tta"], ["-c", "copy"], [("TTA", 32000, 2)], [], "mka"),
    "external-ffmpeg-truehd": ("ffmpeg", ["sine"], ["-c:a", "truehd", "-strict", "-2"], [("Dolby TrueHD", 44100, 1)], [], "mka"),
    "external-ffmpeg-live": ("ffmpeg", ["ogg/external-ffmpeg-libopus.opus"], ["-c", "copy", "-live", "1"], [("Opus", 48000, 2)],
                             [("matroska.segment-size-unknown", "unknown")], "mkv"),
    "external-ffmpeg-no-crc": ("ffmpeg", ["ogg/external-ffmpeg-libopus.opus"], ["-c", "copy", "-write_crc32", "0"], [("Opus", 48000, 2)], [], "mka"),
    "external-ffmpeg-small-clusters": ("ffmpeg", ["ogg/external-ffmpeg-libopus.opus"], ["-c", "copy", "-cluster_size_limit", "2000", "-cluster_time_limit", "100"],
                                       [("Opus", 48000, 2)], [], "mka"),
    "external-ffmpeg-video": ("ffmpeg", ["testsrc", "ogg/external-ffmpeg-libopus.opus"], ["-c:v", "mpeg4", "-c:a", "copy", "-shortest"], [("Opus", 48000, 2)],
                              [("matroska.track-not-analyzed", "video track")], "mkv"),
    "external-ffmpeg-two-audio": ("ffmpeg", ["aac/faac-1.50.m4a", "ogg/external-ffmpeg-libvorbis.ogg"], ["-map", "0:a", "-map", "1:a", "-c", "copy"],
                                  [("AAC", 44100, 2), ("Vorbis", 44100, 2)], [], "mka"),
    "external-ffmpeg-metadata": ("ffmpeg", ["ogg/external-ffmpeg-libopus.opus", "chapters.ffmetadata", "cover.jpg"],
                                 ["-map", "0:a", "-map_metadata", "1", "-map_chapters", "1", "-attach", "cover.jpg", "-metadata:s:t", "mimetype=image/jpeg",
                                  "-c", "copy", "-metadata", "title=A title", "-metadata", "artist=An artist"], [("Opus", 48000, 2)], [], "mka"),
    # An application that muxes through libavformat and sets encoding_tool: matroskaenc.c writes the muxer's version as the MuxingApp and the tool as the WritingApp.
    "external-ffmpeg-encoding-tool": ("ffmpeg", ["ogg/external-ffmpeg-libopus.opus"], ["-c", "copy", "-metadata", "encoding_tool=HandBrake 1.7.2 2024011600"],
                                      [("Opus", 48000, 2)], [], "mka"),
    # The LAME tag ffmpeg's muxer wrote names ffmpeg; the Segment's WritingApp names mkvmerge.
    "external-mkvmerge-mp3": ("mkvmerge", ["mpeg-audio/external-ffmpeg-cbr.mp3"], [], [("MPEG-1 Layer III", 44100, 2)], [("mpeg-audio.writer-claim-differs", "points to \"FFmpeg\"")], "mka"),
    "external-mkvmerge-aac": ("mkvmerge", ["aac/apple-lc-adts-cbr-st44.aac"], [], [("AAC", 44100, 2)], [], "mka"),
    "external-mkvmerge-aac-he": ("mkvmerge", ["aac/faac-2.2-he-adts.aac"], [], [("AAC", 44100, 2)], [], "mka"),
    "external-mkvmerge-ac3": ("mkvmerge", ["ac3/external-ffmpeg-5.1-448k.ac3"], [], [("AC-3", 48000, 6)], [], "mka"),
    "external-mkvmerge-eac3": ("mkvmerge", ["ac3/external-ffmpeg-eac3-5.1-256k.eac3"], [], [("E-AC-3", 48000, 6)], [], "mka"),
    "external-mkvmerge-dts": ("mkvmerge", ["dts/external-ffmpeg-5.1-48k-768k.dts"], [], [("DTS", 48000, 6)], [], "mka"),
    "external-mkvmerge-vorbis": ("mkvmerge", ["ogg/external-ffmpeg-libvorbis.ogg"], [], [("Vorbis", 44100, 2)], [VORBIS_VENDOR], "mka"),
    # The Vorbis comment of an ffmpeg-made Ogg file names ffmpeg; the claims of the other tracks must not weigh it, whatever the order.
    "external-mkvmerge-vorbis-pcm": ("mkvmerge", ["ogg/external-ffmpeg-libvorbis.ogg", "sine.wav"], [], [("Vorbis", 44100, 2), ("PCM", 44100, 1)],
                                     [VORBIS_VENDOR], "mka"),
    "external-mkvmerge-pcm-vorbis": ("mkvmerge", ["sine.wav", "ogg/external-ffmpeg-libvorbis.ogg"], [], [("PCM", 44100, 1), ("Vorbis", 44100, 2)],
                                     [VORBIS_VENDOR], "mka"),
    "external-mkvmerge-vorbis-aac": ("mkvmerge", ["ogg/external-ffmpeg-libvorbis.ogg", "aac/apple-lc-adts-cbr-st44.aac"], [], [("Vorbis", 44100, 2), ("AAC", 44100, 2)],
                                     [VORBIS_VENDOR], "mka"),
    "external-mkvmerge-opus": ("mkvmerge", ["ogg/external-ffmpeg-libopus.opus"], [], [("Opus", 48000, 2)], [], "mka"),
    "external-mkvmerge-opus-webm": ("mkvmerge", ["ogg/external-ffmpeg-libopus.opus"], ["--webm"], [("Opus", 48000, 2)],
                                    [("matroska.webm-timestamp-scale", "20832")], "webm"),
    "external-mkvmerge-tta": ("mkvmerge", ["tta/external-16-stereo.tta"], [], [("TTA", 44100, 2)], [], "mka"),
    # A mono track, whose Channels element mkvmerge leaves out (the default is 1).
    "external-mkvmerge-tta-mono": ("mkvmerge", ["tta/external-libtta-16-mono-24000-last-frame-one-sample.tta"], [], [("TTA", 24000, 1)], [], "mka"),
    "external-mkvmerge-tta-last-frame-one-sample": ("mkvmerge", ["tta/external-libtta-16-stereo-32000-last-frame-one-sample.tta"], [], [("TTA", 32000, 2)], [], "mka"),
    # WavPack blocks of lossyWAV's output (no marker) in Matroska: the codec route counts the zero bits of each packet's blocks (docs/formats/wavpack.md).
    "external-mkvmerge-wavpack-lossywav": ("mkvmerge", ["wavpack/external-lossywav-floor-44k-merge-blocks.wv"], [], [("WavPack lossless", 44100, 2)], [], "mka"),
    "external-mkvmerge-wavpack-silence": ("mkvmerge", ["wavpack/external-silence-16-in-24-merge-blocks.wv"], [], [("WavPack lossless", 44100, 2)], [], "mka"),
    "external-mkvmerge-wavpack": ("mkvmerge", ["wavpack/external-ffmpeg-apev2-tag.wv"], [], [("WavPack lossless", 44100, 2)], [], "mka"),
    "external-mkvmerge-zlib": ("mkvmerge", ["ac3/external-ffmpeg-5.1-448k.ac3"], ["--compression", "0:zlib"], [("AC-3", 48000, 6)],
                               [("matroska.track-encoding-not-read", "zlib compression")], "mka"),
    "external-mkvmerge-no-cues": ("mkvmerge", ["ogg/external-ffmpeg-libopus.opus"], ["--no-cues", "--cluster-length", "100ms"], [("Opus", 48000, 2)], [], "mka"),
    "external-mkvmerge-timestamp-scale": ("mkvmerge", ["ac3/external-ffmpeg-5.1-448k.ac3"], ["--timestamp-scale", "100000"], [("AC-3", 48000, 6)], [], "mka"),
    "external-mkvmerge-two-tracks": ("mkvmerge", ["ac3/external-ffmpeg-5.1-448k.ac3", "ogg/external-ffmpeg-libopus.opus"],
                                     ["--track-name", "0:Surround", "--language", "0:eng"], [("AC-3", 48000, 6), ("Opus", 48000, 2)], [], "mka"),
    "external-mkvmerge-metadata": ("mkvmerge", ["ogg/external-ffmpeg-libopus.opus"],
                                   ["--title", "A title", "--track-name", "0:A track", "--language", "0:fra", "--chapters", "chapters.txt", "--global-tags", "tags.xml",
                                    "--attach-file", "cover.jpg", "--attachment-mime-type", "image/jpeg"], [("Opus", 48000, 2)],
                                   [], "mka"),
    # FFmpeg's muxer writes A_MS/ACM, a WAVEFORMATEX in the CodecPrivate, for the codings that have no Matroska ID of their own, copied here from the committed WAV and
    # ASF files: Windows Media Audio, ADPCM, GSM 6.10, A-law and mu-law (mkvmerge 82.0 refuses the ADPCM and WMA tracks of an AVI file, and the releases of 4.5.0 to 5.9.0
    # could not be run under Wine in the session that looked: docs/development.md). MPEG audio, AC-3, DTS and PCM get their own IDs from it, so those routes are hand-built
    # (make_matroska_fixtures.py, acm-*).
    "external-ffmpeg-acm-wma1": ("ffmpeg", ["asf/external-ffmpeg-wmav1-stereo-44k-128k.wma"], ["-c", "copy"], [("Windows Media Audio 1", 44100, 2)], [], "mka"),
    "external-ffmpeg-acm-wma2": ("ffmpeg", ["asf/external-ffmpeg-wmav2-stereo-44k-128k.wma"], ["-c", "copy"], [("Windows Media Audio 2", 44100, 2)], [], "mka"),
    "external-ffmpeg-acm-wma-pro": ("ffmpeg", ["asf/external-windows-wma10pro-cbr-stereo-44k-128.wma"], ["-c", "copy"], [("Windows Media Audio Professional", 44100, 2)], [], "mka"),
    "external-ffmpeg-acm-wma-lossless": ("ffmpeg", ["asf/external-windows-wma92lossless-stereo-44k-16.wma"], ["-c", "copy"], [("Windows Media Audio Lossless", 44100, 2)], [], "mka"),
    "external-ffmpeg-acm-wma-voice": ("ffmpeg", ["asf/external-windows-wmavoice-cbr-mono-16k-16.wma"], ["-c", "copy"], [("Windows Media Audio Voice", 16000, 1)], [], "mka"),
    "external-ffmpeg-acm-adpcm-ms": ("ffmpeg", ["wav/external-adpcm-ms.wav"], ["-c", "copy"], [("Microsoft ADPCM", 44100, 2)], [NOT_READ_ACM], "mka"),
    "external-ffmpeg-acm-adpcm-ima": ("ffmpeg", ["wav/external-adpcm-ima.wav"], ["-c", "copy"], [("IMA ADPCM", 44100, 2)], [NOT_READ_ACM], "mka"),
    "external-ffmpeg-acm-gsm": ("ffmpeg", ["wav/external-gsm-ms.wav"], ["-c", "copy"], [("GSM 6.10", 8000, 1)], [NOT_READ_ACM], "mka"),
    "external-ffmpeg-acm-alaw": ("ffmpeg", ["wav/external-alaw-mono-8000.wav"], ["-c", "copy"], [("A-law", 8000, 1)], [], "mka"),
    "external-ffmpeg-acm-mulaw": ("ffmpeg", ["wav/external-mulaw-mono-8000.wav"], ["-c", "copy"], [("Mu-law", 8000, 1)], [], "mka"),
}


def signal_wav(path):
    """One second of a 440 Hz sine, 16 bit mono at 44.1 kHz."""
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(44100)
        w.writeframes(b"".join(struct.pack("<h", int(12000 * math.sin(2 * math.pi * 440 * i / 44100))) for i in range(44100)))


def helper_files(work):
    work = pathlib.Path(work)
    signal_wav(work / "sine.wav")
    (work / "cover.jpg").write_bytes(bytes([0xFF, 0xD8, 0xFF, 0xE0]) + bytes(range(256)) * 2 + bytes([0xFF, 0xD9]))
    (work / "chapters.ffmetadata").write_text(
        ";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=700\ntitle=First\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=700\nEND=1400\ntitle=Second\n", encoding="utf-8")
    (work / "chapters.txt").write_text("CHAPTER01=00:00:00.000\nCHAPTER01NAME=First\nCHAPTER02=00:00:00.700\nCHAPTER02NAME=Second\n", encoding="utf-8")
    (work / "tags.xml").write_text(
        '<?xml version="1.0" encoding="UTF-8"?>\n<Tags><Tag><Targets/><Simple><Name>ARTIST</Name><String>An artist</String></Simple>'
        '<Simple><Name>TITLE</Name><String>A title</String></Simple></Tag></Tags>\n', encoding="utf-8")


def path_of(name, work):
    if name == "sine":
        return str(pathlib.Path(work) / "sine.wav")
    if name == "testsrc":
        return "testsrc"
    candidate = FIXTURES_DIR / name
    return str(candidate if candidate.exists() else pathlib.Path(work) / name)


def version(tool):
    if tool.startswith("mkvmerge-"):
        # An old release run under Wine; its banner names it ("mkvmerge v5.9.0 ('On The Loose') built on ...").
        return old_mkvmerge_tool.banner(tool.removeprefix("mkvmerge-")).split(" built on")[0] + " (Wine)"
    if tool == "ffmpeg":
        return subprocess.run([FFMPEG, "-version"], capture_output=True, text=True).stdout.split()[2]
    return subprocess.run([MKVMERGE, "--version"], capture_output=True, text=True).stdout.split("\n")[0].split(" (")[0]


def encode(name, tool, inputs, arguments, extension, work):
    target = OUT / f"{name}.{extension}"
    if tool == "ffmpeg":
        command = [FFMPEG, "-v", "error", "-y", "-bitexact"]
        mapped = False
        for item in inputs:
            if item == "testsrc":
                command += ["-f", "lavfi", "-i", "testsrc=duration=1:size=64x48:rate=10"]
            elif item.endswith(".ffmetadata"):
                command += ["-f", "ffmetadata", "-i", path_of(item, work)]
            elif item == "cover.jpg":
                continue
            else:
                command += ["-i", path_of(item, work)]
        command += [a if a != "cover.jpg" else path_of("cover.jpg", work) for a in arguments]
        command += [str(target)]
        subprocess.run(command, check=True, cwd=work)
        return {"tool": f"ffmpeg {version('ffmpeg')}", "arguments": arguments, "inputs": inputs}
    if tool.startswith("mkvmerge-"):
        old_mkvmerge_tool.mux(tool.removeprefix("mkvmerge-"), target, [path_of(item, work) for item in inputs], arguments)
        return {"tool": version(tool), "arguments": list(arguments), "inputs": inputs}
    command = [MKVMERGE, "-o", str(target)]
    arguments = list(arguments)
    resolved = [path_of(a, work) if a in ("chapters.txt", "tags.xml", "cover.jpg") else a for a in arguments]
    command += resolved
    for item in inputs:
        command.append(path_of(item, work))
    subprocess.run(command, check=True, capture_output=True, cwd=work)
    return {"tool": f"mkvmerge {version('mkvmerge')}", "arguments": arguments, "inputs": inputs}


# The PCM layout the analyzer's decoder outputs for a coding, as the ffmpeg codec that writes it, its bits and its sample format: the container's value as stored, byte
# order made little-endian, unsigned 8 bits as signed (docs/design/spectral-analysis.md, "WAV, Wave64 and AIFF: a PCM decoder").
CANONICAL = {"pcm_s16le": ("pcm_s16le", 16, "integer"), "pcm_s16be": ("pcm_s16le", 16, "integer"), "pcm_s24le": ("pcm_s24le", 24, "integer"),
             "pcm_s32le": ("pcm_s32le", 32, "integer"), "pcm_f32le": ("pcm_f32le", 32, "float"), "pcm_f64le": ("pcm_f64le", 64, "float"), "pcm_u8": ("pcm_s8", 8, "integer")}


def decoded(path, coded, channels):
    """What ffmpeg decodes the file's PCM track to: its samples and the MD5 of the canonical layout (ffmpeg -c:a pcm_<format> -f md5), which the analyzer's decoder must give back."""
    codec, bits, kind = CANONICAL[coded]
    command = [FFMPEG, "-v", "error", "-i", str(path), "-map", "0:a:0", "-c:a", codec]
    raw = subprocess.run(command + ["-f", "data", "-"], capture_output=True, check=True).stdout
    result = subprocess.run(command + ["-f", "md5", "-"], capture_output=True, text=True, check=True)
    return {"samples": len(raw) // (bits // 8 * channels), "bits": bits, "format": kind, "md5": result.stdout.strip().removeprefix("MD5=")}


def coded_as(arguments):
    return arguments[arguments.index("-c:a") + 1] if "-c:a" in arguments else "pcm_s16le"


def manifest(name, encoder, streams, findings, extension, pcm=None):
    data = (OUT / f"{name}.{extension}").read_bytes()
    result = {"description": f"{encoder['tool']} {' '.join(encoder['arguments'])}".strip()}
    result.update(m.manifest(data))
    result["encoder"] = encoder
    result["streams"] = [{"codec": c, "sample_rate": r, "channels": n} for c, r, n in streams]
    result["findings"] = [{"code": code, "contains": contains} for code, contains in findings]
    if pcm is not None:
        result["pcm"] = pcm
    result["crosscheck"] = {"valid": True}
    return result


def main():
    only_manifests = "--manifests-only" in sys.argv
    if not only_manifests:
        missing = [tool for tool in (FFMPEG, MKVMERGE) if shutil.which(tool) is None]
        if missing:
            raise SystemExit(f"Missing {', '.join(missing)}; install ffmpeg and mkvtoolnix, or pass --manifests-only.")
    OUT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as work:
        if not only_manifests:
            helper_files(work)
        # Names on the command line write only those fixtures (the encoders' output is not byte for byte the same twice).
        wanted = [a for a in sys.argv[1:] if not a.startswith("--")]
        unknown = [a for a in wanted if a not in FIXTURES]
        if unknown:
            raise SystemExit(f"Unknown fixture names: {', '.join(unknown)}")
        for name, (tool, inputs, arguments, streams, findings, extension) in FIXTURES.items():
            if wanted and name not in wanted:
                continue
            path = OUT / f"{name}.expected.json"
            pcm = None
            if only_manifests:
                previous = json.loads(path.read_text(encoding="utf-8"))
                encoder = previous["encoder"]
                pcm = previous.get("pcm")
                if pcm is None and len(streams) == 1 and streams[0][0].startswith("PCM") and shutil.which(FFMPEG):
                    pcm = decoded(OUT / f"{name}.{extension}", coded_as(arguments), streams[0][2])
            else:
                encoder = encode(name, tool, inputs, arguments, extension, work)
                if len(streams) == 1 and streams[0][0].startswith("PCM"):
                    pcm = decoded(OUT / f"{name}.{extension}", coded_as(arguments), streams[0][2])
            path.write_text(json.dumps(manifest(name, encoder, streams, findings, extension, pcm), indent=1, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    print(f"Wrote {len(wanted) if wanted else len(FIXTURES)} {'manifests' if only_manifests else 'fixtures'} to {OUT}")


if __name__ == "__main__":
    main()
