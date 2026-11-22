#!/usr/bin/env python3
"""Checks the sample kit's fixtures (import_sample_kit_fixtures.py) against FFmpeg and MediaInfo.

For every <name>.provenance.json in a sample-kit folder of tests/fixtures (mpeg-audio, aac, ogg, ac3, flac, alac), with FFmpeg 8.0.1 and
MediaInfo 26.01 of Ubuntu 26.04. Neither tool is a reference whose source was read: both are black boxes.

- The provenance record: the file's size and SHA-256 are the file's, and the record names no path, user or machine (a ":\\" or "/home/" in it fails).
- ffprobe reads the file as the manifest (<name>.expected.json, written from ffprobe's reading when the file was imported) says: the codec, the sample
  rate, the channels and the duration (within 10 ms; not for ADTS, whose duration ffprobe estimates from the bitrate), and the packets it counts.
- ffmpeg decodes the whole file to nothing with `-v error`: no message, except those a fixture is known for (EXPECTED_ERRORS, each with its reason).
- MediaInfo, against the analyzer's golden report (<name>.report.json, which SampleKitFixtureTests holds to the analysis): the format, for MPEG audio
  and AAC the version, layer and profile, the channels, the sample rate (or half of it, for an HE-AAC stream whose rate MediaInfo reports without SBR),
  and the duration (within 150 ms).

Every skipped check prints its reason. Disagreements fail the run (exit status 1).

    python3 scripts/fixtures/crosscheck_sample_kit_fixtures.py [--any-tool-version] [name ...]

--any-tool-version runs against other versions of the tools than the ones the checks were written for (it prints what differs instead of exiting).
"""

import hashlib
import json
import pathlib
import subprocess
import sys

import mediainfo_tool as mi
import tool_versions

ROOT = pathlib.Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "tests" / "fixtures"

# What ffmpeg reports on files that decode as they are written, name -> (substring of the message, why).
EXPECTED_ERRORS = {}

# MediaInfo's "Format" and its version, layer and profile fields -> the analyzer's codec text.
MPEG_VERSIONS = {"1": "MPEG-1", "2": "MPEG-2", "2.5": "MPEG-2.5", "Version 1": "MPEG-1", "Version 2": "MPEG-2", "Version 2.5": "MPEG-2.5"}
MPEG_LAYERS = {"Layer 1": "Layer I", "Layer 2": "Layer II", "Layer 3": "Layer III"}
AAC_FEATURES = {"LC": "AAC LC", "LC SBR": "HE-AAC", "LC SBR PS": "HE-AAC v2"}
SIMPLE = {"AC-3": "AC-3", "FLAC": "FLAC", "Vorbis": "Vorbis", "ALAC": "Apple Lossless"}

# Durations MediaInfo reports that differ from the analyzer's for a reason that is known, name -> (MediaInfo's duration, why). A duration that is neither the
# analyzer's nor the listed one fails.
MEDIAINFO_DURATIONS = {
    "external-nero-rom-mp3pro-vbr-low": (
        10.522, "MediaInfo 26.01 reported 10.522 s in the sample-kit-crosscheck job of #382, which equals the VBRI byte count over the 40 kbit/s MediaInfo reports as the "
                "stream's bitrate (52,610 x 8 / 40,000); the stream's 386 frames of 576 samples at 22,050 Hz are 10.083 s, which the analyzer reports, and the header's frame count "
                "(387, its own frame included) agrees with"),
}

SKIPS = {}
FAILURES = []


def skip(name, check, reason):
    SKIPS.setdefault((check, reason), []).append(name)


def fail(name, message):
    FAILURES.append(f"{name}: {message}")


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def stems():
    for folder in sorted(FIXTURES.glob("*/sample-kit")):
        for provenance in sorted(folder.glob("*.provenance.json")):
            yield provenance.with_suffix("").with_suffix("")


def media_file(stem):
    return next(p for p in stem.parent.glob(stem.name + ".*") if p.suffix != ".json")


def ffprobe(path):
    result = subprocess.run(["ffprobe", "-v", "error", "-count_packets", "-show_format", "-show_streams", "-of", "json", str(path)], capture_output=True, text=True)
    return json.loads(result.stdout)


def check_provenance(name, stem, path):
    text = stem.with_name(stem.name + ".provenance.json").read_text(encoding="utf-8")
    record = json.loads(text)
    data = path.read_bytes()
    if record["file"]["bytes"] != len(data) or record["file"]["sha256"] != sha256(data):
        fail(name, "the provenance record does not describe the file")
    for private in (":\\", "/home/", "/Users/", "C:"):
        if private in text:
            fail(name, f"the provenance record holds {private!r}")


def check_ffprobe(name, stem, path):
    manifest = json.loads(stem.with_name(stem.name + ".expected.json").read_text(encoding="utf-8"))["ffprobe"]
    actual = ffprobe(path)
    if actual["format"]["format_name"] != manifest["format_name"]:
        fail(name, f"ffprobe's format is {actual['format']['format_name']}, the manifest's {manifest['format_name']}")
    streams = [s for s in actual["streams"] if s.get("codec_type") == "audio"]
    if len(streams) != len(manifest["streams"]):
        fail(name, f"ffprobe finds {len(streams)} audio streams, the manifest {len(manifest['streams'])}")
        return
    for found, expected in zip(streams, manifest["streams"]):
        for key in ("codec_name", "sample_rate", "channels"):
            if str(found.get(key)) != str(expected[key]):
                fail(name, f"ffprobe's {key} is {found.get(key)}, the manifest's {expected[key]}")
        if "nb_read_packets" in expected and int(found.get("nb_read_packets", -1)) != expected["nb_read_packets"]:
            fail(name, f"ffprobe counts {found.get('nb_read_packets')} packets, the manifest {expected['nb_read_packets']}")
        if "duration" in expected and "duration" in found:
            if manifest["format_name"] == "aac":
                skip(name, "ffprobe duration", "an ADTS stream has no duration, and ffprobe estimates one from the bitrate")
            elif abs(float(found["duration"]) - expected["duration"]) > 0.01:
                fail(name, f"ffprobe's duration is {found['duration']}, the manifest's {expected['duration']}")


def check_decode(name, path):
    result = subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-f", "null", "-"], capture_output=True, text=True, errors="replace")
    messages = [line for line in result.stderr.splitlines() if line.strip()]
    known = EXPECTED_ERRORS.get(name)
    unexpected = [m for m in messages if not (known and known[0] in m)]
    if result.returncode != 0 or unexpected:
        fail(name, f"ffmpeg decodes with errors: {' | '.join(unexpected[:3]) or 'exit ' + str(result.returncode)}")


def codec_text(audio):
    """The analyzer's codec text for what MediaInfo calls the stream, or None where MediaInfo's naming has no counterpart."""
    fmt = audio.get("Format")
    if fmt == "MPEG Audio":
        version = MPEG_VERSIONS.get(audio.get("Format_Version", ""))
        layer = MPEG_LAYERS.get(audio.get("Format_Profile", ""))
        return f"{version} {layer}" if version and layer else None
    if fmt == "AAC":
        return AAC_FEATURES.get(audio.get("Format_AdditionalFeatures", ""))
    return SIMPLE.get(fmt)


def check_mediainfo(name, stem, path):
    golden = json.loads(stem.with_name(stem.name + ".report.json").read_text(encoding="utf-8"))
    golden = golden[0] if isinstance(golden, list) else golden
    tracks = mi.read(path)
    audio = tracks["Audio"]
    expected = [s for s in golden["streams"] if s.get("kind", "Audio") == "Audio"]
    if len(audio) != len(expected):
        fail(name, f"MediaInfo finds {len(audio)} audio streams, the analyzer {len(expected)}")
        return
    for found, ours in zip(audio, expected):
        text = codec_text(found)
        if text is None:
            skip(name, "MediaInfo codec", "MediaInfo's naming of the stream has no counterpart in the analyzer's")
        elif text != ours["codec"]:
            fail(name, f"MediaInfo says {text}, the analyzer {ours['codec']}")
        if int(found["Channels"]) != ours["channels"]:
            fail(name, f"MediaInfo's channels are {found['Channels']}, the analyzer's {ours['channels']}")
        rate = int(found["SamplingRate"])
        if ours["sampleRate"] not in (rate, rate * 2):
            fail(name, f"MediaInfo's sample rate is {rate}, the analyzer's {ours['sampleRate']}")
        if "Duration" in found:
            seconds = float(found["Duration"])
            listed = MEDIAINFO_DURATIONS.get(stem.name)
            if listed and abs(seconds - listed[0]) < 0.001:
                skip(name, "MediaInfo duration", listed[1])
            elif abs(seconds - float(ours["duration"])) > 0.15:
                fail(name, f"MediaInfo's duration is {seconds}, the analyzer's {ours['duration']}")
        else:
            skip(name, "MediaInfo duration", "MediaInfo reports no duration for an ADTS stream or a variable bitrate MPEG audio stream without a header that gives one")


def main():
    arguments = sys.argv[1:]
    any_version = "--any-tool-version" in arguments
    names = [a for a in arguments if not a.startswith("--")]
    problems = tool_versions.problems("ffmpeg", "ffprobe")
    if problems and not any_version:
        sys.exit("The cross-check needs Ubuntu 26.04's tools:\n  " + "\n  ".join(problems))
    if problems:
        print("Other tool versions than the checks were written for:\n  " + "\n  ".join(problems))
    if not any_version:
        mi.require()
    checked = 0
    for stem in stems():
        name = stem.parent.parent.name + "/" + stem.name
        if names and not any(n in name for n in names):
            continue
        path = media_file(stem)
        checked += 1
        check_provenance(name, stem, path)
        check_ffprobe(name, stem, path)
        check_decode(name, path)
        check_mediainfo(name, stem, path)
    for (check, reason), files in sorted(SKIPS.items()):
        print(f"skipped {check} on {len(files)} files: {reason}")
    print(f"{checked} fixtures checked.")
    if FAILURES:
        print("\n".join(FAILURES))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
