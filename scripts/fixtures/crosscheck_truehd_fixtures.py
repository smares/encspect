#!/usr/bin/env python3
"""Checks the Dolby TrueHD and MLP fixtures' manifests against independent tools.

For every manifest in tests/fixtures/truehd (make_external_truehd_fixtures.py, make_truehd_fixtures.py) whose "crosscheck" says "ffmpeg": true,
that is every fixture FFmpeg reads the way the manifest does (the encoder-written files and the hand-built ones whose fault leaves the audio alone):

- FFmpeg 8.0.1 decodes the file to the samples the manifest's decoded_pcm_md5 is the MD5 of: read as 32-bit integers in FFmpeg's channel order,
  mapped by speaker name to the analyzer's, and cut to the stream's bit depth, as little-endian integers of whole bytes (the canonical layout).
  The analyzer's decoder is held to the same MD5 by TrueHdDecoderFixtureTests, so FFmpeg and Encspect agree sample for sample, in all channels.
  FFmpeg 6.1.1 gave the manifests' digests; if 8.0.1 gives others, the decoder changed and the manifests must be looked at.
- ffprobe: the codec, the sample rate, the channels, the bit depth and the number of packets, which is the number of access units.
- MediaInfo 26.01 (mediainfo_tool.py): the audio stream's format, commercial name, sampling rate, channels, samples per frame, frame count and,
  for MLP, the bit depth (it reports none for TrueHD).

A fixture with ffmpeg false is damaged or unusual in a way FFmpeg and MediaInfo do not read as the manifest does (units before the first major sync,
bytes skipped, cut units, streams that change): the tools are skipped for it with that reason, and the manifest is decided by truehd_model.py's reading
of docs/formats/truehd.md. Every skipped check prints its reason. Disagreements fail the run (exit status 1).

    python3 scripts/fixtures/crosscheck_truehd_fixtures.py [name ...]
"""

import json
import pathlib
import subprocess
import sys

import make_external_truehd_fixtures as ext
import mediainfo_tool as mi
import tool_versions

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "truehd"
SKIPS = {}


def skip(name, check, reason):
    SKIPS.setdefault((check, reason), []).append(name)


def find(manifest):
    stem = manifest.name[:-len(".expected.json")]
    return next(p for p in DIRECTORY.glob(stem + ".*") if not p.name.endswith(".json"))


def unit_samples(rate):
    """Samples per channel in an access unit: 40 at 44.1 and 48 kHz, 80 at 88.2 and 96 kHz, 160 at 176.4 and 192 kHz."""
    return 40 * (1 if rate <= 48000 else 2 if rate <= 96000 else 4)


def readable_units(manifest):
    """The access units both tools count: those from the first major sync on, as FFmpeg's parser and MediaInfo skip the ones before it."""
    return sum(1 for e in manifest["entries"] if e[0] == "unit" and e[4] != "leading")


def check_ffmpeg(manifest, path, problems):
    values, channels, ff_layout = ext.ffmpeg_pcm(path)
    info = manifest["stream_infos"][0]
    samples, digest = ext.canonical(values, channels, ff_layout, info["layout"], info["bits_per_sample"])
    if samples != manifest["decoded_samples"]:
        problems.append(f"FFmpeg decodes {samples} samples, the manifest {manifest['decoded_samples']}")
    if digest != manifest["decoded_pcm_md5"]:
        problems.append(f"FFmpeg decodes to {digest}, the manifest says {manifest['decoded_pcm_md5']}")


def check_ffprobe(manifest, path, problems):
    forced = "truehd" if path.suffix == ".thd" else "mlp"
    document = json.loads(subprocess.run(["ffprobe", "-v", "error", "-f", forced, "-count_packets", "-select_streams", "a:0", "-show_entries",
                                          "stream=codec_name,sample_rate,channels,bits_per_raw_sample,nb_read_packets", "-of", "json", str(path)],
                                         capture_output=True, text=True, check=True).stdout)
    stream = document["streams"][0]
    info = manifest["stream_infos"][0]
    units = readable_units(manifest)
    for key, want in (("codec_name", forced), ("sample_rate", str(info["sample_rate"])), ("channels", info["channels"]), ("nb_read_packets", str(units))):
        if stream.get(key) != want:
            problems.append(f"ffprobe {key} is {stream.get(key)}, the manifest {want}")
    if str(stream.get("bits_per_raw_sample")) != str(info["bits_per_sample"]):
        problems.append(f"ffprobe bits_per_raw_sample is {stream.get('bits_per_raw_sample')}, the manifest {info['bits_per_sample']}")


def check_mediainfo(name, manifest, path, problems):
    tracks = mi.read_or_problem(path, problems)
    if tracks is None:
        return
    audio = tracks["Audio"][0] if tracks["Audio"] else None
    if audio is None:
        problems.append("MediaInfo finds no audio stream")
        return
    info = manifest["stream_infos"][0]
    is_mlp = path.suffix == ".mlp"
    units = readable_units(manifest)
    commercial = "MLP Lossless" if is_mlp else "Dolby TrueHD with Dolby Atmos" if any(s["info"] & 0x80 and s["substreams"] == 4 for s in manifest["syncs"]) else "Dolby TrueHD"
    expected = [("Format", "MLP" if is_mlp else "MLP FBA"), ("Format_Commercial_IfAny", commercial), ("SamplingRate", str(info["sample_rate"])),
                ("Channels", str(info["channels"])), ("SamplesPerFrame", str(unit_samples(info["sample_rate"]))), ("FrameCount", str(units))]
    if is_mlp:
        expected.append(("BitDepth", str(info["bits_per_sample"])))
    else:
        skip(name, "MediaInfo BitDepth", "MediaInfo reports no bit depth for TrueHD")
    for key, want in expected:
        if audio.get(key) != want:
            problems.append(f"MediaInfo {key} is {audio.get(key)}, the manifest {want}")


def main():
    tool_versions.require("ffmpeg", "ffprobe")
    mi.require()
    chosen = set(a for a in sys.argv[1:] if not a.startswith("-"))
    failures = 0
    checked = 0
    for manifest_path in sorted(DIRECTORY.glob("*.expected.json")):
        name = manifest_path.name[:-len(".expected.json")]
        if chosen and name not in chosen:
            continue
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        path = find(manifest_path)
        problems = []
        if manifest["crosscheck"]["ffmpeg"]:
            check_ffmpeg(manifest, path, problems)
            check_ffprobe(manifest, path, problems)
            check_mediainfo(name, manifest, path, problems)
        else:
            skip(name, "FFmpeg, ffprobe and MediaInfo", "the fixture is damaged or unusual, so the tools do not read it as the manifest does")
        checked += 1
        if problems:
            failures += 1
            print(f"FAIL {name}")
            for problem in problems:
                print(f"    {problem}")
    for (check, reason), names in sorted(SKIPS.items()):
        print(f"skipped {check} for {len(names)} fixtures: {reason}")
    print(f"{checked} fixtures checked, {failures} with disagreements.")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
