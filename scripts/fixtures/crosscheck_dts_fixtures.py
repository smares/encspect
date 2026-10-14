#!/usr/bin/env python3
"""Checks the DTS fixtures' manifests (tests/fixtures/dts) against independent tools.

For every fixture, its manifest's "crosscheck" says what to confirm:

- "ffprobe": ffprobe (ffmpeg 6.1) must report the first stream's codec, sampling frequency (the
  extension's where the stream has one, such as 96 kHz with X96), channels, profile (DTS, DTS-ES,
  DTS 96/24, DTS-HD MA, DTS-HD HRA, DTS Express; "ffprobe_profile": false where ffmpeg reads the
  fixture otherwise), a channel layout that feeds the manifest's speakers (channel_layouts.py;
  ffmpeg places a lone surround pair at the sides, the analyzer at the back as the WAVE channel
  mask does, so a lone pair counts as one position), the nominal bitrate the first frame's RATE
  gives ("ffprobe_bitrate": false where ffmpeg reads it otherwise), and exactly the packets the
  manifest's frames make: ffmpeg's parser starts a packet at each core frame and takes the extension
  substreams after it into the same packet. Raw files are read as "-f dts", since ffmpeg's probe
  looks from byte 4096 on and the fixtures are smaller.
- "decode": ffmpeg with CRC checking must decode each packet alone without an error, unless the
  packet holds a frame the manifest lists as one decoders reject ("damaged": offsets of frames that
  are cut or invalid), which it must reject. MP4 files are decoded whole.
- "libdca": libdca (dca_frames.c, built here) must find the manifest's frames, with the sampling
  frequency, channels ("libdca_channels" where libdca reads the core only), samples per frame and
  nominal bitrate the manifest and the golden report give, and decode each without an error (dca_frame
  and dca_block), except the frames the manifest lists as damaged. libdca reads core frames only and
  skips extension substreams, junk and tags a byte at a time.
- "skip": the checks that are not run on the fixture, each with its reason; every one is printed.

Every fixture is also compared with MediaInfo 24.01 (mediainfo_tool.py, check_mediainfo), against its
golden report: the format and commercial name, the form of the stream (word size and byte order), the
sampling frequency, channels and layout, bit depth, the nominal bitrate and its mode, the samples per
frame, and the frames, samples and duration. MediaInfo's own readings are asserted as such, with the
source line that explains them (File_Dts.cpp of MediaInfoLib v24.01); fixtures on which it reads
otherwise name their evidence, and an explained difference that no longer occurs fails the run.

The analyzer's agreement with the manifests is checked by the tests (DtsFixtureTests). Needs ffmpeg,
libdca-dev (headers and library; or LIBDCA_INCLUDE and LIBDCA_LIB), mediainfo and a C compiler. Exits
non-zero when anything disagrees.

    python3 scripts/fixtures/crosscheck_dts_fixtures.py
"""

import json
import os
import pathlib
import subprocess
import sys
import tempfile

import channel_layouts
import mediainfo_tool as mi

ROOT = pathlib.Path(__file__).resolve().parent
FIXTURES = ROOT.parents[1] / "tests" / "fixtures" / "dts"
CHECKS = ("ffprobe", "ffprobe_profile", "ffprobe_bitrate", "ffprobe_packets", "decode", "libdca")

# ffmpeg's profile names (libavcodec/profiles.c, ff_dca_profiles) for the analyzer's codec names.
PROFILES = {"DTS": "DTS", "DTS-ES": "DTS-ES", "DTS 96/24": "DTS 96/24", "DTS-HD Master Audio": "DTS-HD MA",
            "DTS-HD High Resolution Audio": "DTS-HD HRA", "DTS Express": "DTS Express"}

# libdca's channel counts by arrangement code (dca.h), without the LFE flag.
LIBDCA_CHANNELS = {0: 1, 1: 2, 2: 2, 3: 2, 4: 2, 5: 3, 6: 3, 7: 4, 8: 4, 9: 5, 10: 6}


def nominal_bitrate(report):
    """The nominal bitrate in bit/s the golden report's statistics give, or None where they give none or several."""
    statistics = next((n for n in mi.walk(report["root"]) if n.get("kind") == "Statistics"), None)
    text = str(mi.value(statistics, "Nominal Bitrate")) if statistics else ""
    if "," in text:
        return None
    number = mi.leading_number(text)
    return None if number is None else round(number * 1000)


def extension_rate(manifest):
    value = manifest["fields"].get("DTS/Statistics/Extension Sampling Frequency")
    return int(value.split()[0]) if value else None


def normalized(speakers):
    speakers = list(speakers)
    if "side left" in speakers and "back left" not in speakers:
        speakers = ["back left" if s == "side left" else "back right" if s == "side right" else s for s in speakers]
    return sorted(speakers)


def ffprobe(path, manifest):
    raw = path.suffix == ".dts"
    out = subprocess.run(["ffprobe", "-v", "error", *(["-f", "dts"] if raw else []), "-select_streams", "a", "-show_entries",
                          "stream=codec_name,profile,sample_rate,channels,channel_layout,bit_rate:packet=pos,size", "-of", "json", str(path)],
                         capture_output=True, text=True, check=True).stdout
    return json.loads(out)


def check_ffprobe(path, manifest, report, problems):
    probe = ffprobe(path, manifest)
    streams = probe.get("streams", [])
    check = manifest["crosscheck"]
    if not streams:
        problems.append("ffprobe finds no audio stream")
        return
    actual, expected = streams[0], manifest["streams"][0]
    rate = extension_rate(manifest) or expected["sample_rate"]
    channels = check.get("ffprobe_channels", expected["channels"])
    if actual.get("codec_name") != "dts" or int(actual.get("sample_rate", 0)) != rate or int(actual.get("channels", 0)) != channels:
        problems.append(f"ffprobe: {actual.get('codec_name')}, {actual.get('sample_rate')} Hz, {actual.get('channels')} channels; expected dts, {rate} Hz, {channels}")
    if check.get("ffprobe_profile", True) and actual.get("profile") != PROFILES[expected["codec"]]:
        problems.append(f"ffprobe's profile {actual.get('profile')}, expected {PROFILES[expected['codec']]}")
    known = channel_layouts.FFMPEG.get(actual.get("channel_layout"))
    if "ffprobe_channels" not in check and (known is None or normalized(known) != normalized(expected["channel_layout"])):
        problems.append(f"ffprobe's channel layout {actual.get('channel_layout')}, expected {expected['channel_layout']}")
    packets = [[int(p["pos"]), int(p["size"])] for p in probe.get("packets", [])]
    expected_packets = check.get("ffprobe_packets", manifest["frames"])
    if expected_packets is not False and packets != expected_packets:
        problems.append(f"ffprobe lists {len(packets)} packets {packets[:3]}..., the manifest's frames make {len(expected_packets)} {expected_packets[:3]}...")
    if isinstance(check.get("ffprobe_packets"), list):
        print(f"{path.name}: ffprobe's packets differ from the frames: {check['ffprobe_packets_reason']}")
    nominal = nominal_bitrate(report)
    actual_rate = int(actual.get("bit_rate", 0) or 0)
    if check.get("ffprobe_bitrate", True):
        if nominal is None:
            print(f"{path.name}: skips the bitrate: the frames state more than one nominal bitrate or an open one")
        elif actual_rate != nominal:
            problems.append(f"ffprobe's bitrate {actual.get('bit_rate')}, the first frame's RATE gives {nominal}")


def decode_stderr(data, work):
    piece = work / "piece.dts"
    piece.write_bytes(data)
    result = subprocess.run(["ffmpeg", "-v", "error", "-err_detect", "crccheck", "-f", "dts", "-i", str(piece), "-f", "null", "-"], capture_output=True, text=True)
    return result.stderr.strip()


def check_decode(path, manifest, problems, work):
    check = manifest["crosscheck"]
    if check.get("decode_note"):
        print(f"{path.name}: ffmpeg decodes the damaged frames: {check['decode_note']}")
    if path.suffix != ".dts":
        result = subprocess.run(["ffmpeg", "-v", "error", "-err_detect", "crccheck", "-i", str(path), "-f", "null", "-"], capture_output=True, text=True)
        if result.stderr.strip():
            problems.append(f"ffmpeg reports errors: {result.stderr.strip().splitlines()[0]}")
        return
    data = path.read_bytes()
    packets = [[int(p["pos"]), int(p["size"])] for p in ffprobe(path, manifest).get("packets", [])]
    for offset, length in packets:
        errors = decode_stderr(data[offset:offset + length], work)
        if errors:
            problems.append(f"ffmpeg rejects the packet at {offset}: {errors.splitlines()[0]}")


def check_decode_errors(path, manifest, problems, work):
    """Each frame alone: ffmpeg must report the error the manifest names for it (a substring of its first message), or none."""
    check = manifest["crosscheck"]
    data = path.read_bytes()
    expectations = {}
    if isinstance(check.get("decode_error"), str):
        for offset, length in manifest["frames"]:
            expectations[offset] = (length, check["decode_error"])
    for key, value in check.get("decode_errors", {}).items():
        length = value[0] if isinstance(value, list) else next(l for o, l in manifest["frames"] if o == int(key))
        expectations[int(key)] = (length, value[1] if isinstance(value, list) else value)
    for offset, (length, wanted) in sorted(expectations.items()):
        errors = decode_stderr(data[offset:offset + length], work)
        first = errors.splitlines()[0] if errors else None
        if wanted is None and first is not None or wanted is not None and (first is None or wanted not in errors):
            problems.append(f"ffmpeg's error for the frame at {offset} is {first!r}, the manifest names {wanted!r}")


def build_libdca(work):
    include = os.environ.get("LIBDCA_INCLUDE")
    library = os.environ.get("LIBDCA_LIB")
    command = ["cc", "-O2", "-o", str(work / "dca_frames"), str(ROOT / "dca_frames.c")]
    command += [f"-I{include}"] if include else []
    command += [library] if library else ["-ldca"]
    command += ["-lm"]
    subprocess.run(command, check=True)
    return work / "dca_frames"


def check_libdca(tool, path, manifest, report, problems):
    lines = subprocess.run([str(tool), str(path)], capture_output=True, text=True, check=True, env=dict(os.environ)).stdout.splitlines()
    found = [line.split() for line in lines]
    check = manifest["crosscheck"]
    offsets = [int(f[0]) for f in found]
    expected = [f[0] for f in manifest["frames"]]
    if offsets != expected:
        problems.append(f"libdca finds frames at {offsets[:6]}..., the manifest at {expected[:6]}...")
    damaged = set(check.get("libdca_damaged", []))
    nominal = nominal_bitrate(report)
    # The stream each frame belongs to: the streams' samples add up frame by frame.
    streams = manifest["streams"]
    index, used = 0, 0
    for offset, length, flags, rate, bitrate, samples, status in found:
        if used >= streams[index]["samples"] and index + 1 < len(streams):
            index, used = index + 1, 0
        stream = streams[index]
        used += int(samples)
        if int(rate) != stream["sample_rate"]:
            problems.append(f"libdca reads {rate} Hz in the frame at {offset}, the manifest {stream['sample_rate']}")
        channels = LIBDCA_CHANNELS[int(flags) & 0x3F] + (1 if int(flags) & 0x80 else 0)
        if channels != check.get("libdca_channels", stream["channels"]):
            problems.append(f"libdca reads {channels} channels in the frame at {offset}, the manifest {check.get('libdca_channels', stream['channels'])}")
        if len(streams) == 1 and nominal is not None and int(bitrate) != nominal:
            problems.append(f"libdca reads {bitrate} bit/s in the frame at {offset}, the first frame's RATE gives {nominal}")
        if (status == "ok") == (int(offset) in damaged):
            problems.append(f"libdca {'decodes' if status == 'ok' else 'rejects'} the frame at {offset}, which the manifest calls {'damaged' if int(offset) in damaged else 'intact'}")
    total = sum(int(f[5]) for f in found)
    if total != sum(s["samples"] for s in streams):
        problems.append(f"libdca's frames hold {total} samples, the manifest's streams {sum(s['samples'] for s in streams)}")


# MediaInfo 24.01 reads these fixtures differently from the analyzer; the evidence is in MediaInfoLib v24.01,
# Source/MediaInfo/Audio/File_Dts.cpp. Each entry must still differ, or the run fails.
CORE_ONLY = ("MediaInfo takes a stream as DTS-HD only where it finds the component's sync word in the substream (File_Dts.cpp, Extensions: XLL() sets "
             "presence_Extended_XLL); {why}, so it reports the core: the format of the core, its bitrate, constant")
MIXED = ["commercial name", "bitrate", "bitrate mode"]
MEDIAINFO_READS_OTHERWISE = {
    "exss-bad-component-sync": dict.fromkeys(MIXED, CORE_ONLY.format(why="the lossless component does not start with it, which the analyzer reports as dts.component-sync "
                                                                        "and still names the stream from the asset descriptor's component mask")),
    "exss-bad-crc": dict.fromkeys(MIXED, CORE_ONLY.format(why="it checks the substream header's CRC16 first and skips a substream that fails it (Header - CRC NOK), which the "
                                                              "analyzer reports as dts.crc-mismatch and reads on")),
    "exss-sizes-overrun": dict.fromkeys(MIXED, CORE_ONLY.format(why="it cuts the asset at the end of the substream, 40 bytes of the 160 its size gives, and finds only the core "
                                                                    "component in them; the analyzer reports dts.substream-overrun and names the stream from the descriptor")),
    "exss-two-assets": dict.fromkeys(
        ["format", "commercial name", "sampling rate", "channels", "bit depth", "bitrate", "samples per frame", "bitrate mode"],
        "MediaInfo reads each asset descriptor's static fields in turn and never skips to the next descriptor by its size (File_Dts.cpp, AssetDescriptor loop, "
        "AssetDescriptFsize is only shown), so the second asset's fields are read from the first one's dynamic fields: 127 channels at 16 kHz"),
    "rate-code": {"bitrate": "MediaInfo gives no bitrate where RATE is not one of the 25 codes of Table 5-7, 29 for open or Unknown (File_Dts.cpp, Streams_Fill_Core: bit_rate<25)"},
    "reserved-values": dict.fromkeys(
        ["commercial name", "channels", "channel layout", "bit depth"],
        "MediaInfo splits PCMR into a two-bit resolution and the ES flag (File_Dts.cpp, Core: bits_per_sample, ES) and so takes PCMR 7, which Table 5-17 lists as invalid, "
        "for 24 bits with the ES flag; it counts LFF 3 as an LFE channel and the ES flag as one more, and lays out ES only for arrangement 3/2 (Streams_Fill_Core_ES)"),
    "user-defined-arrangement": dict.fromkeys(
        ["channels", "channel layout"], "MediaInfo gives no channels for a user defined arrangement (File_Dts.cpp, Streams_Fill_Core: Core_Core_AMODE<16), where the "
                                       "analyzer reports the two channels the core codes as discrete"),
    "xxch": dict.fromkeys(
        ["commercial name", "channels", "channel layout"], "MediaInfo names a core with XXCH \"DTS\" with the feature XXCh and counts only the core's channels "
                                                       "(File_Dts.cpp, Streams_Fill: presence_Core_XXCH fills the core), where the analyzer adds the extension's channels"),
}

# MediaInfo's commercial names (File_Dts.cpp, Streams_Fill) for the analyzer's codec names.
COMMERCIAL = {"DTS": "DTS", "DTS-ES": "DTS-ES Discrete", "DTS 96/24": "DTS 96/24", "DTS Express": "DTS Express"}


def frames_of(report):
    stream = report["streams"][0]
    return [n for index in stream["nodes"] for n in mi.walk(mi.stream_node(report, {"nodes": [index]})) if n.get("kind") == "Frame"]


def check_mediainfo(path, tally):
    """MediaInfo must report the format and its commercial name, the word size and byte order of the stream, the sampling frequency,
    channels and layout, the bit depth, the bitrate its first frame gives (File_Dts.cpp, BitRate_Get: the frame's bytes in the file over
    its samples), the bitrate mode and the samples per frame."""
    tally.start(path.stem)
    report = mi.golden(path)
    info = mi.read(path, full=True)
    audio = info["Audio"][0] if info and info["Audio"] else {}
    stream = report["streams"][0]
    tally.compare("format", audio.get("Format"), "DTS LBR" if stream["codec"] == "DTS Express" else "DTS")
    if not audio:
        return tally.problems
    if path.suffix == ".mp4":
        tally.skip("form and layout", "MediaInfo reads MP4's DTS from the sample entry (File_Mpeg4), without the stream's form or channels")
        return tally.problems
    frames = frames_of(report)
    first = frames[0]
    core = mi.child(first, "Core Substream")
    sync = mi.child(core, "Synchronization", "Sync Word")
    tally.compare("commercial name", audio.get("Format_Commercial"), COMMERCIAL.get(stream["codec"], stream["codec"]))
    if sync is not None:
        tally.compare("word size", audio.get("Format_Settings_Mode"), str(mi.leading_number(mi.value(sync, "Word Size"))))
        tally.compare("byte order", audio.get("Format_Settings_Endianness"), "Big" if mi.value(sync, "IsBigEndian") else "Little")
    statistics = next(n for n in mi.walk(report["root"]) if n.get("kind") == "Statistics")
    extension = mi.leading_number(mi.value(statistics, "Extension Sampling Frequency"))
    tally.compare("sampling rate", audio.get("SamplingRate"), str(extension or stream["sampleRate"]))
    tally.compare("channels", audio.get("Channels"), str(stream["channels"]))
    tally.compare("channel layout", mi.speakers(audio.get("ChannelLayout")), mi.normalized(stream["channelLayout"]))
    tally.compare("bit depth", audio.get("BitDepth"), str(stream.get("bitsPerSample")))
    exss = mi.child(first, "Extension Substream")
    blocks = mi.leading_number(mi.value(core, "Frame Header", "Sample Blocks")) if core is not None else None
    rate = mi.leading_number(mi.value(core, "Frame Header", "Sampling Frequency")) if core is not None else stream["sampleRate"]
    samples = (blocks or 16) * 32
    lossless = stream["codec"] == "DTS-HD Master Audio"
    # File_Dts.cpp, BitRate_Get: the core's frame bytes in the file over its samples (none where an LBR component stands in for the core),
    # plus the extension substream's bytes over its samples (WithHD); a lossless stream has no bitrate, and is variable.
    expected = None
    if not lossless:
        extended = stream["codec"] in ("DTS-HD High Resolution Audio", "DTS Express")
        expected = 0 if stream["codec"] == "DTS Express" or core is None else core["length"] * 8 * rate / samples
        expected += exss["length"] * 8 * stream["sampleRate"] / samples if extended and exss is not None else 0
    tally.compare("bitrate", audio.get("BitRate"), None if expected is None else str(round(expected)))
    if core is not None:
        tally.compare("samples per frame", audio.get("SamplesPerFrame"), str(round(samples * (extension or rate) / rate)))
    tally.compare("bitrate mode", audio.get("BitRate_Mode"), "VBR" if lossless else "CBR")
    tally.skip("duration, samples and stream size", "MediaInfo derives them from the file size and the first frame's bitrate (File_Dts.cpp, Streams_Finish)")
    return tally.problems


def main():
    failures = checked = 0
    mi.require()
    tally = mi.Tally(MEDIAINFO_READS_OTHERWISE)
    with tempfile.TemporaryDirectory() as directory:
        work = pathlib.Path(directory)
        tool = build_libdca(work)
        for manifest_path in sorted(FIXTURES.glob("*.expected.json")):
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            stem = manifest_path.name[:-len(".expected.json")]
            path = next(p for p in FIXTURES.iterdir() if p.name.startswith(stem + ".") and not p.name.endswith(".json"))
            report = mi.golden(path)
            check = manifest["crosscheck"]
            problems = []
            for name in CHECKS:
                if check.get(name) is False and name not in check.get("skip", {}):
                    problems.append(f"the {name} check is off without a reason")
            for name, reason in check.get("skip", {}).items():
                print(f"{path.name}: skips {name}: {reason}")
            if check.get("ffprobe"):
                check_ffprobe(path, manifest, report, problems)
            if check.get("decode"):
                check_decode(path, manifest, problems, work)
            if "decode_error" in check or "decode_errors" in check:
                check_decode_errors(path, manifest, problems, work)
            if check.get("libdca"):
                check_libdca(tool, path, manifest, report, problems)
            problems += check_mediainfo(path, tally)
            checked += 1
            for problem in problems:
                print(f"{path.name}: {problem}")
            failures += bool(problems)
    for problem in tally.unused():
        print(problem)
        failures += 1
    tally.print()
    print(f"{checked} fixtures checked, {failures} disagree.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
