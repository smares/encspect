#!/usr/bin/env python3
"""Takes a selection of the encodes of the private sample kit into tests/fixtures.

    python3 scripts/fixtures/import_sample_kit_fixtures.py path/to/samples-request [--only NAME ...] [--force] [--list]

The kit (samples-request/submissions/<format>/<encoder>-<version>/) holds files that people encoded by hand with encoders that CI does not
run: Nero Burning ROM's MP3, mp3PRO and AAC plug-ins, neroAacEnc 1.0.0.2 to 1.5.4.0, qaac with Core Audio, iTunes' AAC and MP3 encoders, Winamp's
Fraunhofer AAC, aacPlus and Vorbis encoders and Windows 10's Media Foundation encoders. Its sources are synthetic (sources/make_sources.py writes
them from nothing), so the audio carries no copyright. The kit is private; this script reads it and writes only what belongs in the repository.

SELECTION below lists the files that show something the fixtures lack (a profile, mode, rate, header or tag), not every bitrate: about 5 MB of the
kit's 85 MB, with Media Foundation's lossless files cut to one FLAC and one ALAC file. For each file it writes, byte for byte and unrewritten (the
encoders stamp the time of the encode, so the files are committed once and checked by their content):

- tests/fixtures/<format>/sample-kit/external-<name>.<ext>, in the folder of its format;
- external-<name>.provenance.json: the encoder, its version and build, the package it came from, the settings, the source WAV and its SHA-256, why the
  file was chosen, and the file's size and SHA-256;
- external-<name>.expected.json: the manifest, which comes from ffprobe alone (codec, profile, rate, channels, duration and packets), never from the
  analyzer. crosscheck_sample_kit_fixtures.py recomputes it and adds MediaInfo's reading.

The kit's logs and INFO.txt files stay private: they hold the contributor's paths and machine. Nothing from them is copied but the encoder's name,
version, build and settings. A file that is already there is left alone unless it is the same bytes or --force is given.

The analyzer's reading is held by SampleKitFixtureTests (tests/Encspect.Tests/Host), against <name>.report.json, the full golden report that
ENCSPECT_UPDATE_GOLDEN=1 writes, as for the other fixtures. --list prints the selection and its sizes.
"""

import hashlib
import json
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent
FIXTURES = ROOT.parents[1] / "tests" / "fixtures"

SOURCES = {
    "44k-stereo": "music-44k-stereo.wav", "44k-mono": "music-44k-mono.wav", "22k-stereo": "music-22k-stereo.wav", "16k-mono": "music-16k-mono.wav",
    "8k-mono": "music-8k-mono.wav", "48k-5.1": "music-48k-5.1.wav", "48k-stereo": "music-48k-stereo.wav", "32k-stereo": "music-32k-stereo.wav",
}

NERO_ACCESS = "Nero's license limits how the software is used, not the files it writes."
NERO_PACKAGES = {
    "1.0.0.2": ("NeroAACCodec-1.0.0.2.zip", "24a7c56a9b5bc3b9528f4cbf8ff9a693c8727ba48a13af25f16e1ad99fe7297c", "May 26 2006"),
    "1.0.7.0": ("NeroAACCodec-1.0.7.0.zip", "ab11ba359e315f61ec25da45b4768a2c09aa5b25668ee00935b56a32fa757084", "Feb 12 2007"),
    "1.1.34.2": ("NeroDigitalAudio-1.1.34.2.zip", "dc2978c9b501697fb8695192426181b3a128b249edc704ca879b6f51bad248af", "Aug 6 2007"),
    "1.3.3.0": ("NeroAACCodec-1.3.3.zip", "1801df16797ffd38709bd5938785d32f534413d71a3c0fb4016a7a6ed35daaa3", "Sep 17 2008"),
    "1.5.3.0": ("NeroAACCodec-1.5.1.zip", "bf00888c8e73ab3c19a2a1a91a1e26e85cf666b9efb240d7f3a6cb0bee7f0a70", "Dec 29 2009"),
}


def nero_aac_enc(version, kit_folder, wine=True):
    """neroAacEnc's win32 build; the packages before 1.5.4.0 were run under Wine 9.0 (which writes the same bytes as Windows, apart from the 12
    bytes of the MP4 creation and modification times), 1.5.4.0 on Windows. The package named 1.5.1 reports version 1.5.3.0 in its banner."""
    encoder = {"tool": "neroAacEnc (Nero AAC Codec)", "version": version, "build": "win32", "kit_folder": kit_folder}
    if version in NERO_PACKAGES:
        package, sha, date = NERO_PACKAGES[version]
        encoder.update({"package": package, "package_sha256": sha, "package_build_date": date})
    else:
        encoder.update({"package_build_date": "Feb 18 2010"})
    if wine:
        encoder["run_under"] = "Wine 9.0"
    return encoder


ENCODERS = {
    "nero-rom-mp3": {
        "tool": "Nero Burning ROM 2026 (28.0.1.7), Extras > Encode Files, MP3 and mp3PRO output", "version": "28.0.1.7",
        "component": "APM_mp3pro.dll, mp3PRO Audio Plugin, file version 28.0.1.3, product version 2.1.0.0", "kit_folder": "mp3/nero-burning-rom-28.0.1.7",
    },
    "nero-rom-aac": {
        "tool": "Nero Burning ROM 2026 (28.0.1.7), Extras > Encode Files, Nero AAC output", "version": "28.0.1.7",
        "component": "APM_NeroDigital.dll, NeroDigital Audio Plugin, file version 28.0.1.3, product version 1.0.0.2", "kit_folder": "aac/nero-burning-rom-28.0.1.7",
        "note": "The profile is Nero's choice; the dialog offers none.",
    },
    "neroaacenc-1002": nero_aac_enc("1.0.0.2", "aac/neroaacenc-1.0.0.2-win32"),
    "neroaacenc-1070": nero_aac_enc("1.0.7.0", "aac/neroaacenc-1.0.7.0-win32"),
    "neroaacenc-11342": nero_aac_enc("1.1.34.2", "aac/neroaacenc-1.1.34.2-win32"),
    "neroaacenc-1330": nero_aac_enc("1.3.3.0", "aac/neroaacenc-1.3.3-win32"),
    "neroaacenc-1530": nero_aac_enc("1.5.3.0", "aac/neroaacenc-1.5.1-win32"),
    "neroaacenc-1540": nero_aac_enc("1.5.4.0", "aac/neroaacenc-1.5.4.0", wine=False),
    "qaac": {
        "tool": "qaac (Apple's AAC encoder in CoreAudioToolbox)", "version": "qaac 3.07, CoreAudioToolbox 7.10.9.0",
        "component": "the CoreAudioToolbox of Apple's standalone iTunes 12.13.12.3 installer", "kit_folder": "aac/qaac-3.07-coreaudio-7.10.9.0",
    },
    "itunes-aac": {
        "tool": "iTunes AAC encoder (Apple's CoreAudioToolbox 7.10.9.0)", "version": "iTunes 12.13.12.3, converted through iTunes' ConvertFile2",
        "kit_folder": "aac/itunes-12.13.12.3-windows", "note": "At low bitrates iTunes picks the output rate itself.",
    },
    "itunes-mp3": {
        "tool": "iTunes MP3 encoder", "version": "iTunes 12.13.12.3, converted through iTunes' ConvertFile2", "kit_folder": "mp3/itunes-12.13.12.3-windows",
    },
    "winamp-fhgaac": {
        "tool": "Fraunhofer IIS AAC encoder in Winamp's enc_fhgaac.dll (MPEG-4 AAC Encoder v1.08)", "version": "enc_fhgaac.dll 1.0.8.0, Fraunhofer library 03.02.16, Winamp 5.666 build 3516",
        "component": "driven by fhgaacenc (tmkk, MIT); the MP4 files without fhgaacenc's own optimize pass, so they are as Winamp's plug-in writes them, "
                     "and the ADTS files with fhgaacenc's own ADTS headers, not the SHOUTcast plug-in's", "kit_folder": "aac/winamp-5.666-fhgaac-1.0.8",
    },
    "winamp-aacplus": {
        "tool": "Coding Technologies aacPlus encoder in Winamp's enc_aacplus.dll (aacPlus encoding 1998-2009 Coding Technologies, v1.3)", "version": "Winamp 5.572",
        "component": "enc_aacplus.dll, 912,896 bytes, SHA-256 F4333EB10A78C26772026BEC6A9182A9C6811E52B2C93BFF586E49C69F25EC91, no version resource; Format Converter",
        "kit_folder": "aac/winamp-5.572-aacplus-1.3",
    },
    "winamp-vorbis-5666": {
        "tool": "Winamp's Ogg Vorbis encoder plug-in, Format Converter, quality 5", "version": "Winamp 5.666 build 3516, enc_vorbis.dll 1.54.0.0",
        "kit_folder": "vorbis/winamp-5.666",
    },
    "winamp-vorbis-592": {
        "tool": "Winamp's Ogg Vorbis encoder plug-in, Format Converter, quality 5", "version": "Winamp 5.9.2 build 10042, enc_vorbis.dll 1.58.0.0",
        "kit_folder": "vorbis/winamp-5.9.2",
    },
    "ms-aac": {
        "tool": "Microsoft AAC Audio Encoder MFT (Media Foundation)", "version": "mfAACEnc.dll 10.0.19041.1",
        "kit_folder": "windows-media-foundation", "note": "Driven through the transform's documented interface (ADTS) or Microsoft's Sink Writer (MP4).",
    },
    "ms-mp3acm": {
        "tool": "MP3 Encoder ACM Wrapper MFT (Media Foundation) over the ACM codec l3codeca.acm", "version": "mfcore.dll 10.0.19041.1; l3codeca.acm 1.9.0.401, Fraunhofer IIS",
        "kit_folder": "windows-media-foundation",
    },
    "ms-mpa": {
        "tool": "Microsoft MPEG-2 Audio Encoder MFT (Media Foundation)", "version": "msmpeg2enc.dll 10.0.19041.4522", "kit_folder": "windows-media-foundation",
    },
    "ms-ac3": {
        "tool": "Microsoft Dolby Digital Encoder MFT (Media Foundation)", "version": "msac3enc.dll 10.0.19041.1", "kit_folder": "windows-media-foundation",
    },
    "ms-flac": {
        "tool": "Microsoft FLAC encoder (Media Foundation)", "version": "MSFlacEncoder.dll 10.0.19041.4355", "kit_folder": "windows-media-foundation",
    },
    "ms-alac": {
        "tool": "Microsoft ALAC encoder (Media Foundation)", "version": "MSAlacEncoder.dll 10.0.19041.3636", "kit_folder": "windows-media-foundation",
    },
}

NERO_MP3_VERSION = "28.0.1.7"

# (kit file under submissions/, folder under tests/fixtures, name without "external-" and the extension, encoder, source, settings, what it shows)
SELECTION = [
    # Nero Burning ROM's MP3 and mp3PRO plug-in.
    ("mp3/nero-burning-rom-28.0.1.7/music-44k-stereo-cbr128.mp3", "mpeg-audio", "nero-rom-mp3-cbr128", "nero-rom-mp3", "44k-stereo",
     "list entry MP3 128 kbit/s 44100 Hz stereo, constant bitrate, quality Medium, padding ISO", "the plug-in's plain MP3, tagged with an empty ID3v2 PRIV frame"),
    ("mp3/nero-burning-rom-28.0.1.7/music-44k-stereo-vbr-highest.mp3", "mpeg-audio", "nero-rom-mp3-vbr-highest", "nero-rom-mp3", "44k-stereo",
     "variable bitrate on, quality Highest, list entry MP3 64 kbit/s 22050 Hz stereo (the file is 44100 Hz)", "a VBR file with a VBRI header and a main_data_begin the buffer cannot hold"),
    ("mp3/nero-burning-rom-28.0.1.7/music-8k-mono-cbr8.mp3", "mpeg-audio", "nero-rom-mp3-8k-mono-cbr8", "nero-rom-mp3", "8k-mono",
     "list entry MP3 8 kbit/s 8000 Hz mono, constant bitrate, quality Medium, padding ISO", "MPEG 2.5 at the lowest rate"),
    ("mp3/nero-burning-rom-28.0.1.7/music-44k-stereo-pro-cbr64.mp3", "mpeg-audio", "nero-rom-mp3pro-cbr64", "nero-rom-mp3", "44k-stereo",
     "list entry mp3PRO 64 kbit/s 44100 Hz stereo, constant bitrate", "mp3PRO written by Nero's plug-in (the Thomson encoder's layout)"),
    ("mp3/nero-burning-rom-28.0.1.7/music-44k-mono-pro-cbr32.mp3", "mpeg-audio", "nero-rom-mp3pro-mono-cbr32", "nero-rom-mp3", "44k-mono",
     "list entry mp3PRO 32 kbit/s 44100 Hz mono, constant bitrate, source 44100 Hz mono", "mono mp3PRO"),
    ("mp3/nero-burning-rom-28.0.1.7/music-44k-stereo-pro-cbr18-32k-mono.mp3", "mpeg-audio", "nero-rom-mp3pro-cbr18-mono", "nero-rom-mp3", "44k-stereo",
     "list entry mp3PRO 18 kbit/s 32000 Hz mono, constant bitrate, from the 44100 Hz stereo source", "mp3PRO at 18 kbit/s, a rate MPEG-2 Layer III does not list (the file is free format or VBR)"),
    ("mp3/nero-burning-rom-28.0.1.7/music-44k-stereo-pro-vbr-low.mp3", "mpeg-audio", "nero-rom-mp3pro-vbr-low", "nero-rom-mp3", "44k-stereo",
     "list entry mp3PRO 80 kbit/s 44100 Hz stereo, variable bitrate on, quality Low", "an mp3PRO entry whose file the analyzer does not call mp3PRO"),
    # iTunes' MP3 encoder.
    ("mp3/itunes-12.13.12.3-windows/music-44k-stereo-cbr128-joint.mp3", "mpeg-audio", "itunes-mp3-cbr128-joint", "itunes-mp3", "44k-stereo",
     "MP3, 128 kbit/s, 44100 Hz, joint stereo, default options", "iTunes' plain MP3"),
    ("mp3/itunes-12.13.12.3-windows/music-44k-mono-cbr64.mp3", "mpeg-audio", "itunes-mp3-mono-cbr64", "itunes-mp3", "44k-mono",
     "MP3, 64 kbit/s, mono, sample rate and channels automatic", "iTunes resamples to 22.05 kHz at this rate"),
    ("mp3/itunes-12.13.12.3-windows/music-44k-stereo-vbr-hoechste.mp3", "mpeg-audio", "itunes-mp3-vbr-highest", "itunes-mp3", "44k-stereo",
     "128 kbit/s, variable bitrate on, quality 7 (Highest), joint stereo, sample rate and channels automatic", "iTunes' Xing header; its table of contents is not increasing"),
    # Media Foundation's ACM wrapper and MPEG-2 audio encoder.
    ("windows-media-foundation/windows-10-22h2-19045.7846/music-16k-mono-ms-mp3acm-32k.mp3", "mpeg-audio", "ms-mp3acm-16k-mono-32k", "ms-mp3acm", "16k-mono",
     "MP3, 32 kbit/s, 16000 Hz, mono", "Fraunhofer's l3codeca behind Windows' wrapper, MPEG-2 at 16 kHz"),
    ("windows-media-foundation/windows-10-22h2-19045.7846/music-44k-mono-ms-mp3acm-64k.mp3", "mpeg-audio", "ms-mp3acm-44k-mono-64k", "ms-mp3acm", "44k-mono",
     "MP3, 64 kbit/s, 44100 Hz, mono", "the same codec at an MPEG-1 rate"),
    ("windows-media-foundation/windows-10-22h2-19045.7846/music-44k-mono-ms-mpa-l2-64k.mp2", "mpeg-audio", "ms-mpa-l2-44k-mono-64k", "ms-mpa", "44k-mono",
     "MPEG-1 Layer II, 64 kbit/s, 44100 Hz, mono", "a Layer II encoder of Windows"),
    ("windows-media-foundation/windows-10-22h2-19045.7846/music-16k-mono-ms-mpa-l2-32k.mp2", "mpeg-audio", "ms-mpa-l2-16k-mono-32k", "ms-mpa", "16k-mono",
     "MPEG-2 Layer II, 32 kbit/s, 16000 Hz, mono", "MPEG-2 Layer II at a half rate"),
    # Nero Burning ROM's AAC plug-in.
    ("aac/nero-burning-rom-28.0.1.7/music-44k-stereo-auto-cbr128.mp4", "aac", "nero-rom-aac-cbr128", "nero-rom-aac", "44k-stereo",
     "mode Constant bitrate (CBR), 128 kbit/s; the profile is Nero's choice", "the plug-in's MP4 layout and its Nero writer string"),
    ("aac/nero-burning-rom-28.0.1.7/music-44k-stereo-auto-cbr64.mp4", "aac", "nero-rom-aac-cbr64", "nero-rom-aac", "44k-stereo",
     "mode Constant bitrate (CBR), 64 kbit/s; the profile is Nero's choice", "Nero's own choice of HE-AAC at 64 kbit/s"),
    ("aac/nero-burning-rom-28.0.1.7/music-44k-stereo-auto-abr32.mp4", "aac", "nero-rom-aac-abr32", "nero-rom-aac", "44k-stereo",
     "mode Average bitrate (ABR), 32 kbit/s; the profile is Nero's choice", "HE-AAC v2 chosen by the plug-in"),
    ("aac/nero-burning-rom-28.0.1.7/music-44k-mono-auto-cbr32.m4a", "aac", "nero-rom-aac-mono-cbr32", "nero-rom-aac", "44k-mono",
     "mode Constant bitrate (CBR), 32 kbit/s, mono source; the profile is Nero's choice", "mono HE-AAC in an M4A"),
    ("aac/nero-burning-rom-28.0.1.7/music-22k-stereo-auto-vbr-q0.50.mp4", "aac", "nero-rom-aac-22k-vbr-q050", "nero-rom-aac", "22k-stereo",
     "mode Variable bitrate (VBR), quality 0.50, 22050 Hz source; the profile is Nero's choice", "AAC-LC at 22.05 kHz with variable bitrate"),
    ("aac/nero-burning-rom-28.0.1.7/music-8k-mono-auto-cbr12.mp4", "aac", "nero-rom-aac-8k-mono-cbr12", "nero-rom-aac", "8k-mono",
     "mode Constant bitrate (CBR), the dialog's lowest target (12 kbit/s), 8000 Hz mono source", "the lowest rate"),
    ("aac/nero-burning-rom-28.0.1.7/music-48k-5.1-auto-vbr-q0.50.mp4", "aac", "nero-rom-aac-51-vbr-q050", "nero-rom-aac", "48k-5.1",
     "mode Variable bitrate (VBR), quality 0.50, 48000 Hz 5.1 source", "5.1 channels"),
    # neroAacEnc, one pair per package: HE-AAC mono and HE-AAC v2, which show each build's writer string and layout.
    *[
        (f"aac/{folder}/music-44k-mono-he-cbr32.m4a", "aac", f"neroaacenc-{key}-he-mono-cbr32", f"neroaacenc-{key}", "44k-mono",
         "neroAacEnc -he -cbr 32000, 44100 Hz mono source", f"the {version} build's HE-AAC mono file")
        for key, version, folder in (("1002", "1.0.0.2", "neroaacenc-1.0.0.2-win32"), ("1070", "1.0.7.0", "neroaacenc-1.0.7.0-win32"),
                                     ("11342", "1.1.34.2", "neroaacenc-1.1.34.2-win32"), ("1330", "1.3.3.0", "neroaacenc-1.3.3-win32"),
                                     ("1530", "1.5.3.0", "neroaacenc-1.5.1-win32"), ("1540", "1.5.4.0", "neroaacenc-1.5.4.0"))
    ],
    *[
        (f"aac/{folder}/music-44k-stereo-hev2-cbr32.m4a", "aac", f"neroaacenc-{key}-hev2-cbr32", f"neroaacenc-{key}", "44k-stereo",
         "neroAacEnc -hev2 -cbr 32000, 44100 Hz stereo source", f"the {version} build's HE-AAC v2 file")
        for key, version, folder in (("1002", "1.0.0.2", "neroaacenc-1.0.0.2-win32"), ("1070", "1.0.7.0", "neroaacenc-1.0.7.0-win32"),
                                     ("11342", "1.1.34.2", "neroaacenc-1.1.34.2-win32"), ("1330", "1.3.3.0", "neroaacenc-1.3.3-win32"),
                                     ("1530", "1.5.3.0", "neroaacenc-1.5.1-win32"), ("1540", "1.5.4.0", "neroaacenc-1.5.4.0"))
    ],
    ("aac/neroaacenc-1.5.4.0/music-22k-stereo-auto-q0.5.m4a", "aac", "neroaacenc-1540-22k-q050", "neroaacenc-1540", "22k-stereo",
     "neroAacEnc -q 0.5 on a 22050 Hz source", "variable bitrate at 22.05 kHz, the profile the encoder chose"),
    # qaac with Core Audio.
    ("aac/qaac-3.07-coreaudio-7.10.9.0/music-16k-mono-lc-cbr32.m4a", "aac", "qaac-16k-mono-lc-cbr32", "qaac", "16k-mono",
     "qaac -c 32, 16000 Hz mono source", "Core Audio's AAC-LC at 16 kHz in an M4A with qaac's writer string"),
    ("aac/qaac-3.07-coreaudio-7.10.9.0/music-44k-mono-lc-tvbr91.m4a", "aac", "qaac-mono-lc-tvbr91", "qaac", "44k-mono",
     "qaac -V 91, 44100 Hz mono source", "true VBR, which afconvert's files here do not cover"),
    ("aac/qaac-3.07-coreaudio-7.10.9.0/music-44k-stereo-he-cbr64.m4a", "aac", "qaac-he-cbr64", "qaac", "44k-stereo",
     "qaac --he -c 64, 44100 Hz stereo source", "HE-AAC in an M4A by Core Audio"),
    ("aac/qaac-3.07-coreaudio-7.10.9.0/music-44k-stereo-he-cbr64.aac", "aac", "qaac-he-cbr64-adts", "qaac", "44k-stereo",
     "qaac --adts --he -c 64, 44100 Hz stereo source", "HE-AAC as an ADTS stream, whose SBR is signaled implicitly"),
    ("aac/qaac-3.07-coreaudio-7.10.9.0/music-48k-5.1-he-cbr160.m4a", "aac", "qaac-51-he-cbr160", "qaac", "48k-5.1",
     "qaac --he -c 160, 48000 Hz 5.1 source", "HE-AAC with 5.1 channels"),
    ("aac/qaac-3.07-coreaudio-7.10.9.0/music-8k-mono-lc-cbr16.m4a", "aac", "qaac-8k-mono-lc-cbr16", "qaac", "8k-mono",
     "qaac -c 16, 8000 Hz mono source", "the lowest rate"),
    # iTunes' AAC encoder.
    ("aac/itunes-12.13.12.3-windows/music-44k-stereo-lc-128-vbroff-tagged.m4a", "aac", "itunes-aac-lc-128-tagged", "itunes-aac", "44k-stereo",
     "AAC Encoder, custom, 128 kbit/s, variable bitrate off, sample rate and channels automatic; tagged through iTunes", "iTunes' MP4 layout with its tags"),
    ("aac/itunes-12.13.12.3-windows/music-44k-stereo-he-48.m4a", "aac", "itunes-aac-he-48", "itunes-aac", "44k-stereo",
     "AAC Encoder, custom, 48 kbit/s, High Efficiency on, 44100 Hz stereo source", "HE-AAC; iTunes never used parametric stereo here"),
    ("aac/itunes-12.13.12.3-windows/music-44k-mono-lc-64-mono.m4a", "aac", "itunes-aac-mono-64", "itunes-aac", "44k-mono",
     "AAC Encoder, custom, 64 kbit/s, channels mono, 44100 Hz mono source", "iTunes picks 32 kHz for mono at 64 kbit/s"),
    ("aac/itunes-12.13.12.3-windows/music-44k-stereo-preset-spoken-podcast.m4a", "aac", "itunes-aac-spoken-podcast", "itunes-aac", "44k-stereo",
     "AAC Encoder, preset Spoken Podcasts", "the preset's 22.05 kHz output"),
    # Winamp's Fraunhofer AAC encoder.
    ("aac/winamp-5.666-fhgaac-1.0.8/music-44k-stereo-he-cbr64.m4a", "aac", "winamp-fhgaac-he-cbr64", "winamp-fhgaac", "44k-stereo",
     "fhgaacenc --cbr 64 --profile he --no-optimize", "Fraunhofer's HE-AAC in the MP4 layout of Winamp's plug-in"),
    ("aac/winamp-5.666-fhgaac-1.0.8/music-44k-stereo-hev2-cbr32.m4a", "aac", "winamp-fhgaac-hev2-cbr32", "winamp-fhgaac", "44k-stereo",
     "fhgaacenc --cbr 32 --profile hev2 --no-optimize", "Fraunhofer's HE-AAC v2"),
    ("aac/winamp-5.666-fhgaac-1.0.8/music-44k-mono-vbr4.m4a", "aac", "winamp-fhgaac-mono-vbr4", "winamp-fhgaac", "44k-mono",
     "fhgaacenc --vbr 4 --no-optimize, 44100 Hz mono source", "Fraunhofer's variable bitrate mode (--vbr 4)"),
    ("aac/winamp-5.666-fhgaac-1.0.8/music-8k-mono-cbr16.m4a", "aac", "winamp-fhgaac-8k-mono-cbr16", "winamp-fhgaac", "8k-mono",
     "fhgaacenc --cbr 16 --no-optimize, 8000 Hz mono source", "8 kHz input at 16 kbit/s"),
    ("aac/winamp-5.666-fhgaac-1.0.8/music-44k-stereo-he-cbr64.aac", "aac", "winamp-fhgaac-he-cbr64-adts", "winamp-fhgaac", "44k-stereo",
     "fhgaacenc --cbr 64 --profile he --adts", "HE-AAC as ADTS"),
    # Winamp's aacPlus encoder.
    ("aac/winamp-5.572-aacplus-1.3/music-44k-stereo-hev2-cbr32.m4a", "aac", "winamp-aacplus-hev2-cbr32", "winamp-aacplus", "44k-stereo",
     "aacPlus (HE-AAC) Encoder v1.3, MP4, 32 kbit/s, stereo", "Coding Technologies' HE-AAC v2 with implicit parametric stereo"),
    ("aac/winamp-5.572-aacplus-1.3/music-44k-stereo-lc-cbr128-mp4entry.m4a", "aac", "winamp-aacplus-lc-cbr128-mp4entry", "winamp-aacplus", "44k-stereo",
     "Format Converter entry MP4/LC-AAC Encoder v1.3, 128 kbit/s", "an M4A with three tracks"),
    ("aac/winamp-5.572-aacplus-1.3/music-44k-stereo-he-cbr64.aac", "aac", "winamp-aacplus-he-cbr64-adts", "winamp-aacplus", "44k-stereo",
     "aacPlus (HE-AAC) Encoder v1.3, raw, 64 kbit/s, stereo", "an ID3v2 tag before the ADTS stream"),
    ("aac/winamp-5.572-aacplus-1.3/music-16k-mono-he-cbr8-mono.aac", "aac", "winamp-aacplus-16k-mono-he-cbr8-adts", "winamp-aacplus", "16k-mono",
     "aacPlus (HE-AAC) Encoder v1.3, raw, 8 kbit/s, mono, 16000 Hz", "the lowest HE-AAC rate"),
    # Media Foundation's AAC encoder.
    ("windows-media-foundation/windows-10-22h2-19045.7846/music-44k-mono-ms-aac-96k.aac", "aac", "ms-aac-mono-96k-adts", "ms-aac", "44k-mono",
     "the AAC MFT at 96 kbit/s (12000 bytes/s), mono, raw ADTS output", "Microsoft's ADTS headers"),
    ("windows-media-foundation/windows-10-22h2-19045.7846/music-44k-mono-ms-aac-96k-sinkwriter.m4a", "aac", "ms-aac-mono-96k-sinkwriter", "ms-aac", "44k-mono",
     "the AAC MFT at 96 kbit/s, mono, through the Sink Writer into MP4", "Microsoft's MP4 layout, with the movie box after the media data"),
    # Winamp's Vorbis encoder.
    ("vorbis/winamp-5.666/music-44k-stereo-vorbis-q5.ogg", "ogg", "winamp-vorbis-5666-q5", "winamp-vorbis-5666", "44k-stereo",
     "Format Converter, Ogg Vorbis, quality 5 (160 kbit/s nominal)", "an aoTuV b6.03 stream, whole length"),
    ("vorbis/winamp-5.9.2/music-44k-stereo-vorbis-q5.ogg", "ogg", "winamp-vorbis-592-q5", "winamp-vorbis-592", "44k-stereo",
     "Format Converter, Ogg Vorbis, quality 5 (160 kbit/s nominal)", "libvorbis 1.3.7's vendor string, and the last 1.044 s of the source missing"),
    # Media Foundation's AC-3, FLAC and ALAC encoders.
    ("windows-media-foundation/windows-10-22h2-19045.7846/music-44k-mono-ms-ac3-64k.ac3", "ac3", "ms-ac3-44k-mono-64k", "ms-ac3", "44k-mono",
     "the Dolby Digital MFT at 64 kbit/s, mono", "Microsoft's AC-3 at 44.1 kHz"),
    ("windows-media-foundation/windows-10-22h2-19045.7846/music-44k-mono-ms-flac-16bit.flac", "flac", "ms-flac-44k-mono-16", "ms-flac", "44k-mono",
     "16 bits per sample, mono", "Microsoft's FLAC encoder (the vendor string names libFLAC 1.3.0)"),
    ("windows-media-foundation/windows-10-22h2-19045.7846/music-44k-mono-ms-alac-16bit.m4a", "alac", "ms-alac-44k-mono-16", "ms-alac", "44k-mono",
     "16 bits per sample, mono", "Microsoft's Apple Lossless encoder and its MP4 layout"),
]

def sha256(data):
    return hashlib.sha256(data).hexdigest()


def probe(path):
    """What ffprobe says of the file, in the fields every cross-check compares. ffprobe is run as a program."""
    result = subprocess.run(["ffprobe", "-v", "error", "-count_packets", "-show_format", "-show_streams", "-of", "json", str(path)], capture_output=True, text=True, check=True)
    data = json.loads(result.stdout)
    streams = []
    for stream in data["streams"]:
        if stream.get("codec_type") != "audio":
            continue
        item = {k: stream[k] for k in ("codec_name", "profile", "channel_layout") if k in stream}
        for key in ("sample_rate", "channels", "bits_per_sample", "bits_per_raw_sample", "nb_read_packets"):
            if key in stream:
                item[key] = int(stream[key])
        for key in ("duration", "bit_rate"):
            if key in stream and stream[key] != "N/A":
                item[key] = float(stream[key]) if key == "duration" else int(stream[key])
        streams.append(item)
    return {"format_name": data["format"]["format_name"], "streams": streams}


def source_sha(kit, source_key):
    name = SOURCES[source_key]
    for line in (kit / "sources" / "SHA256SUMS").read_text(encoding="utf-8").splitlines():
        digest, _, file = line.partition("  ")
        if file.strip() == name:
            return name, digest
    raise SystemExit(f"{name} is not in sources/SHA256SUMS.")


def import_one(kit, entry, force):
    relative, folder, name, encoder_key, source_key, settings, shows = entry
    source = kit / "submissions" / relative
    data = source.read_bytes()
    extension = source.suffix
    directory = FIXTURES / folder / "sample-kit"
    directory.mkdir(parents=True, exist_ok=True)
    target = directory / f"external-{name}{extension}"
    if target.exists() and target.read_bytes() != data and not force:
        print(f"{name}: skipped, {target.name} exists with other bytes; pass --force")
        return False
    target.write_bytes(data)
    encoder = {k: v for k, v in ENCODERS[encoder_key].items() if k != "kit_folder"}
    encoder["settings"] = settings
    input_name, input_sha = source_sha(kit, source_key)
    provenance = {
        "description": f"{encoder['tool']} {encoder['version']}: {settings}, on the synthetic source {input_name}.",
        "encoder": encoder,
        "input": {"name": input_name, "sha256": input_sha, "generator": "sources/make_sources.py of the sample kit (written from nothing, reproducible byte for byte)"},
        "selected_for": shows,
        "file": {"name_in_kit": source.name, "bytes": len(data), "sha256": sha256(data)},
        "reproducible": False,
        "note": "The encoder stamps the time of the encode into the file; the file is committed as written, and checked by its content.",
    }
    if encoder_key.startswith("nero"):
        provenance["license"] = NERO_ACCESS
    (directory / f"external-{name}.provenance.json").write_text(json.dumps(provenance, indent=1, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    manifest = {"description": "What ffprobe reads of the file (crosscheck_sample_kit_fixtures.py recomputes it and adds MediaInfo's reading).", "ffprobe": probe(target)}
    (directory / f"external-{name}.expected.json").write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    print(f"{name}: {len(data)} bytes into {directory.relative_to(FIXTURES.parent.parent)}")
    return True


def main():
    arguments = sys.argv[1:]
    if not arguments or arguments[0].startswith("--"):
        raise SystemExit(__doc__)
    kit = pathlib.Path(arguments[0])
    rest = arguments[1:]
    only = [a for a in rest[rest.index("--only") + 1:] if not a.startswith("--")] if "--only" in rest else None
    names = [e[2] for e in SELECTION]
    if len(set(names)) != len(names):
        raise SystemExit("two entries have the same name.")
    for name in only or []:
        if name not in names:
            raise SystemExit(f"{name} is not in the selection.")
    if "--list" in rest:
        total = 0
        for entry in SELECTION:
            size = (kit / "submissions" / entry[0]).stat().st_size
            total += size
            print(f"{size:>8}  {entry[1]}/sample-kit/external-{entry[2]}")
        print(f"{total:>8}  {len(SELECTION)} files")
        return 0
    force = "--force" in rest
    imported = sum(import_one(kit, e, force) for e in SELECTION if only is None or e[2] in only)
    print(f"{imported} fixtures imported.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
