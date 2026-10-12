#!/usr/bin/env python3
"""Checks tests/conformance/mpeg2-audio.json against the ISO/IEC 13818-4 audio conformance streams.

ISO publishes 35 MPEG-2 audio conformance test sets, each a bitstream, an optional extension
bitstream, a description and the reference decoder's output. Their licence lets them be used,
unmodified, for the purpose of the standard, but not redistributed, so they are not in this
repository: this script downloads them into a cache directory and checks each archive's SHA-256
against the manifest before using it.

The manifest is ours. Per stream it records the version, layer, sample rate and frame count, the
channels, speaker layout and multilingual channels the analyzer's stream reports, and for the 22
MPEG-1 Layer II streams with the MPEG-2 multichannel extension, what the analyzer must report: the multichannel header (configuration, center, surround, LFE, extension bitstream,
dematrix procedure, multilingual channels), whether any frame uses prediction or dynamic
crosstalk, and how many frames have a multichannel CRC that holds and how many continue their
multichannel side information in the extension bitstream, a separate file the analyzer does not
read, and how many channel files mctoolamed writes. The header values match ISO's description of
every stream. This script confirms the manifest with independent tools:

- ffprobe finds the listed number of frames, and the channels of the stereo or mono base: the
  stream's channels, or two where the stream carries a multichannel extension the analyzer verifies.
- mp3guessenc reports the listed multichannel parameters; it verifies the multichannel CRC
  before it reports any.
- mctoolamed, a decoder derived from the ISO reference software, decodes every multichannel
  stream, with its extension bitstream, without a multichannel CRC error, into the listed number of
  channel files (one per channel, the LFE's included); where the analyzer verifies the extension,
  that is the stream's number of channels, and the multilingual channels it writes apart are the
  stream's multilingual channels.

With --analyzer, the analyzer's report on every stream (encspect --ndjson) must match the
manifest instead: frame count, version, layer and sample rate, the stream's channels, layout and
multilingual channels, the multichannel configuration and header, prediction and dynamic crosstalk, the frames whose multichannel CRC holds and those that
continue in the extension bitstream, and no warning or error beyond the stream's "findings". A
stream whose multichannel side information never fits its base frame ("verified": false) cannot
be verified at all, so the analyzer must report it as a possible extension instead, and its stream as
the stereo base.

Needs curl, ffprobe and a C compiler, which builds mctoolamed and mp3guessenc (from third_party). Usage:

    python3 scripts/fixtures/conformance_mpeg_audio.py [cache directory]
    python3 scripts/fixtures/conformance_mpeg_audio.py --analyzer path/to/encspect [cache directory]
"""

import json
import pathlib
import re
import subprocess
import sys
import tarfile

import downloads
import guessenc_tool
import mctoolamed_tool

ROOT = pathlib.Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "tests" / "conformance" / "mpeg2-audio.json"


def addresses(manifest, name):
    """ISO's address of a file, then the Wayback Machine's copy, then the private repository's."""
    url = manifest["source"] + name
    return [url, downloads.archived(url, manifest["archive_year"]), downloads.private(f"conformance/mpeg2-audio/{name}")]


def fetch(manifest, cache):
    """Downloads and unpacks every archive the manifest lists, checking its SHA-256."""
    cache.mkdir(parents=True, exist_ok=True)
    for stream in manifest["streams"]:
        archive = cache / f"{stream['name']}.tar.gz"
        downloads.fetch(addresses(manifest, archive.name), stream["archive_sha256"], archive)
        target = cache / stream["name"]
        if not (target / stream["file"]).exists():
            with tarfile.open(archive) as tar:
                tar.extractall(target, filter="data")


def guessenc(path):
    output = subprocess.run([guessenc_tool.binary(), "-e", str(path)], capture_output=True, text=True).stdout
    if "Multichannel parameters" not in output:
        return None
    field = lambda key: re.search(rf"{key}\s*:\s*(.+)", output).group(1).strip()
    return {"extension": field("Audio bitstream extension"), "lfe": field("LFE"), "channels": field("Channels"),
            "ml": field("Multi lingual channels"), "config": field("Channel configuration")}


def ffprobe_channels(path):
    result = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "stream=channels", "-of", "json", str(path)],
                            capture_output=True, text=True, check=True)
    return [s["channels"] for s in json.loads(result.stdout).get("streams", [])]


def verified(stream):
    """Whether the stream carries a multichannel extension the analyzer verifies."""
    mc = stream.get("multichannel")
    return mc is not None and stream["layer"] == "II" and mc.get("verified") is not False


def ffprobe_frames(path):
    result = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "frame=pkt_pos", "-of", "json", str(path)],
                            capture_output=True, text=True, check=True)
    return len(json.loads(result.stdout).get("frames", []))


def walk(node, kind):
    """The nodes of a kind (report schema 1: "Tag", "Stream", "Frame", "SideInfo", "Statistics")."""
    if node.get("kind") == kind:
        yield node
    for child in node.get("children", []):
        yield from walk(child, kind)


def child(node, name):
    return next((c for c in node.get("children", []) if c["name"] == name), None)


def analyzer_problems(analyzer, path, stream):
    """Differences between the analyzer's report on path and the manifest."""
    report = json.loads(subprocess.run([analyzer, "--ndjson", str(path)], capture_output=True, text=True, check=True).stdout)
    frames = list(walk(report["root"], "Frame"))
    problems = []
    if report.get("schemaVersion") != 1:
        problems.append(f"the analyzer writes report schema {report.get('schemaVersion')}, this script reads 1")
    if len(frames) != stream["frames"]:
        problems.append(f"the analyzer finds {len(frames)} frames, the manifest lists {stream['frames']}")
    header = {c["name"]: c.get("value") for c in child(frames[0], "Header")["children"]} if frames else {}
    expected = {"Version": stream["version"], "Layer": stream["layer"], "Sample Rate": stream["sample_rate"]}
    if {k: header.get(k) for k in expected} != expected:
        problems.append(f"the first frame is {header}, the manifest says {expected}")
    streams = [(s["channels"], s.get("channelLayout"), s.get("multilingualChannels", 0)) for s in report["streams"]]
    wanted = [(stream["channels"], stream["channel_layout"], stream.get("multilingual_channels", 0))]
    if streams != wanted:
        problems.append(f"the analyzer's streams have the channels, layout and multilingual channels {streams}, the manifest lists {wanted}")
    serious = sorted(f["code"] for f in report["findings"] if f["severity"] != "Info")
    if serious != sorted(stream.get("findings", [])):
        problems.append(f"the analyzer reports {serious}, the manifest expects {stream.get('findings', [])}")
    mc = stream.get("multichannel")
    nodes = [n for f in frames for n in [child(f, "Multichannel")] if n is not None]
    if mc and mc.get("verified") is False:
        found = [f for f in report["findings"] if f["code"] == "mpeg.multichannel-unverified" and mc["configuration"] in f["message"]]
        if nodes or not found:
            problems.append(f"expected an unverified {mc['configuration']} extension and no multichannel nodes")
        return problems
    if not mc:
        if nodes:
            problems.append("the analyzer reports a multichannel extension the stream does not have")
        return problems
    verified = [n for n in nodes if (crc := child(n, "CRC")) and (computed := child(crc, "Computed")) and computed["value"] == crc["value"]]
    continued = [n for n in nodes if child(child(n, "CRC"), "Computed") is None]
    if (len(verified), len(continued)) != (mc["crc_verified_frames"], mc["frames_continued_in_extension"]):
        problems.append(f"{len(verified)} frames pass the multichannel CRC and {len(continued)} continue in the extension bitstream, "
                        f"the manifest says {mc['crc_verified_frames']} and {mc['frames_continued_in_extension']}")
    if not verified:
        return problems + ["no frame's multichannel side information is verified"]
    first = verified[0]
    fields = {c["name"]: c.get("value") for c in child(first, "Header")["children"]}
    status = [s for n in verified for s in [child(n, "Composite Status")] if s]
    flags = lambda name: any(child(s, name)["value"] is True for s in status)
    actual = {
        "configuration": first["value"], "center": fields["Center"], "surround": fields["Surround"],
        "lfe": fields["HasLfe"] is True, "extension_bitstream": fields["HasExtensionBitstream"] is True,
        "dematrix_procedure": int(fields["Dematrix Procedure"]), "multilingual_channels": int(fields["Multilingual Channels"]),
        "prediction": flags("HasPrediction"), "dynamic_crosstalk": flags("HasDynamicCrosstalk"),
    }
    wanted = {k: v for k, v in mc.items() if k in actual}
    if actual != wanted:
        problems.append(f"the multichannel extension reads {actual}, the manifest says {wanted}")
    return problems


def main():
    arguments = sys.argv[1:]
    analyzer = None
    if arguments[:1] == ["--analyzer"]:
        analyzer, arguments = arguments[1], arguments[2:]
    cache = pathlib.Path(arguments[0] if arguments else ROOT / ".cache" / "conformance")
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    fetch(manifest, cache)
    if analyzer is not None:
        problems = 0
        for stream in manifest["streams"]:
            for problem in analyzer_problems(analyzer, cache / stream["name"] / stream["file"], stream):
                problems += 1
                print(f"{stream['name']}: {problem}")
        print(f"Analyzed {len(manifest['streams'])} conformance streams, {problems} problems.", file=sys.stderr)
        sys.exit(1 if problems else 0)

    decoder = mctoolamed_tool.build()
    problems = 0
    for stream in manifest["streams"]:
        directory = cache / stream["name"]
        path = directory / stream["file"]
        found = []
        if ffprobe_frames(path) != stream["frames"]:
            found.append(f"ffprobe finds {ffprobe_frames(path)} frames, the manifest lists {stream['frames']}")
        base = 2 if verified(stream) else stream["channels"]
        if ffprobe_channels(path) != [base]:
            found.append(f"ffprobe finds the channels {ffprobe_channels(path)}, the manifest's stream has a base of {base}")
        if guessenc(path) != stream.get("mp3guessenc"):
            found.append(f"mp3guessenc reports {guessenc(path)}, the manifest {stream.get('mp3guessenc')}")
        if "multichannel" in stream and stream["layer"] == "II":
            extension = directory / stream["extension_file"] if stream["extension_file"] else None
            errors, _, channels, multilingual = mctoolamed_tool.decode_all(decoder, path, extension)
            if errors or channels != stream["multichannel"]["decoded_channels"]:
                found.append(f"mctoolamed reports {errors} multichannel CRC errors and writes {channels} channels")
            if verified(stream) and stream["multichannel"]["decoded_channels"] != stream["channels"]:
                found.append(f"mctoolamed writes {channels} channels, the manifest's stream has {stream['channels']}")
            if multilingual != stream["multichannel"]["multilingual_channels"] or (verified(stream) and multilingual != stream.get("multilingual_channels", 0)):
                found.append(f"mctoolamed reports {multilingual} multilingual channels")
        for problem in found:
            problems += 1
            print(f"{stream['name']}: {problem}")
    print(f"Checked {len(manifest['streams'])} conformance streams, {problems} problems.", file=sys.stderr)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
