#!/usr/bin/env python3
"""Checks the MPEG fixtures' expected results against independent decoders.

For every fixture in tests/fixtures/mpeg, layer3, layer2 and vbr whose manifest has
"crosscheck": true, ffprobe must report exactly the frames the manifest lists (offset and length
of each), and for protected frames libmad (madplay) must report a CRC failure for exactly the
frames the manifest expects one for. libmad is used for CRCs because ffmpeg 6.1 rejects every
Layer I CRC, correct or not. With "crosscheck": "madplay" or "mpg123", that decoder's count of
decoded frames is compared instead, where ffmpeg resyncs differently or cannot read the stream at
all (free format; libmad also skips the first frame of a LAME free-format stream, so mpg123 counts
those).

Layer II and III fixtures are checked whatever their "crosscheck" value:

- "silent": ffmpeg, mpg123 and libmad (without dither) decode the file to all-zero samples and
  report no error, which only happens when every granule's bits, or every Layer II allocation,
  scalefactor and sample, start and end where the fixture says.
- "scale_info" per Layer II frame: minimp3 reads exactly this allocation, scfsi and these
  scalefactors.
- "mctoolamed": the multichannel decoder decodes every channel to silence with exactly this many
  multichannel CRC errors.
- "mp3guessenc_multichannel": the multichannel parameters mp3guessenc prints.
- "side_info" per frame: minimp3 reads exactly these values (minimp3_side_info.c), or rejects the
  frame where the manifest says it does. Its reservoir before each frame must match, except where
  minimp3 resets or discards it: after a gap, after a frame it rejects, and after a frame whose
  main data it could not find. Frames minimp3 skips before it locks on are only allowed before a
  gap or a stream change.
- "mp3guessenc": the statistics mp3guessenc prints about the stream.

- "vbr_header": the raw values of the first frame's Xing, Info, LAME or VBRI header (vbr_tags.py).
  mp3guessenc must report the same header fields, LAME tag fields, tag CRC verdict and music CRC
  verdict; mpg123 the same frame and byte counts, quality, encoder string, VBR method, ReplayGain,
  delay and padding; ffprobe the duration the frame count gives, the start time the delay gives,
  and the encoder string and ReplayGain only when the tag CRC holds. Where these tools read a
  header differently from LAME (mp3guessenc looks for Xing in every layer and checks the music CRC
  over every later frame, ffmpeg drops the frame count of a much longer file and reads a delay
  after any "LAME", mpg123 prints the lowpass byte as the revision), the check follows the tool.
  The peak is the one field no tool reports.
- "mp3guessenc_encoder_string": the encoder string mp3guessenc finds in the tag or ancillary data.

External fixtures list their encoder settings and ffprobe's packets instead of every frame; for
them, ffprobe's sample rate, channel count and bitrate must match those settings, and minimp3 and
mp3guessenc must still report what the manifest recorded.

Needs ffprobe, madplay, mpg123, mp3guessenc, curl, make and a C compiler on the PATH. Exits
non-zero when anything disagrees.
"""

import json
import pathlib
import re
import subprocess
import sys

import mctoolamed_tool
import minimp3_tool
import vbr_tags
from conformance_mpeg_audio import guessenc as guessenc_multichannel

ROOT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures"
DIRECTORIES = [ROOT / "mpeg", ROOT / "layer3", ROOT / "layer2", ROOT / "vbr"]
CHANNELS = {"Mono": 1}
GUESSENC_MODES = {"Simple stereo": "0", "Intensity stereo": "1", "Mid-side stereo": "2", "Intensity and mid-side stereo": "3"}
GUESSENC_BLOCKS = {"Long block": "long", "Switch block": "switch", "Short block": "short", "Mixed block": "mixed"}


def ffprobe_frames(path):
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "frame=pkt_pos,pkt_size", "-of", "json", str(path)],
        capture_output=True,
        text=True,
        check=True,
    )
    return [(int(f["pkt_pos"]), int(f["pkt_size"])) for f in json.loads(result.stdout).get("frames", [])]


def ffprobe_packets(path):
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "packet=pos,size", "-of", "json", str(path)],
        capture_output=True,
        text=True,
        check=True,
    )
    return [(int(p["pos"]), int(p["size"])) for p in json.loads(result.stdout).get("packets", [])]


def ffprobe_stream(path):
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "stream=sample_rate,channels,bit_rate", "-of", "json", str(path)],
        capture_output=True,
        text=True,
        check=True,
    )
    return json.loads(result.stdout)["streams"][0]


def madplay_output(path):
    result = subprocess.run(["madplay", "-v", "-o", "null:/dev/null", str(path)], capture_output=True, text=True)
    return (result.stdout + result.stderr).replace("\r", "\n").splitlines()


def madplay_crc_failures(path):
    return sum("CRC check failed" in line for line in madplay_output(path))


def mpg123_frame_count(path, samples_per_frame, channels):
    pcm = subprocess.run(["mpg123", "-q", "--no-gapless", "-s", str(path)], capture_output=True, check=True).stdout
    return len(pcm) // (samples_per_frame * channels * 2)


def frame_shape(fields):
    layer = fields["Header/Layer"]
    samples = 384 if layer == "I" else 576 if layer == "III" and fields["Header/Version"] != "MPEG-1" else 1152
    return samples, 1 if fields["Header/Channel Mode"] == "Mono" else 2


def madplay_frame_count(path):
    for line in madplay_output(path):
        if " frames decoded" in line:
            return int(line.split()[0])
    return 0


def guessenc_summary(path):
    """The Layer III statistics mp3guessenc prints, in the shape make_layer3_fixtures.py writes."""
    # The exit status is the guessed encoder, not an error.
    output = subprocess.run(["mp3guessenc", "-e", str(path)], capture_output=True, text=True).stdout
    summary = {"blocks": {"long": 0, "switch": 0, "short": 0, "mixed": 0}, "mode_extensions": {}, "global_gain": {},
               "ancillary": [0, 0, 0]}
    gains = {}
    for line in output.splitlines():
        key, _, value = (part.strip() for part in line.partition(":"))
        number = value.split()[0] if value else ""
        if key in GUESSENC_MODES:
            summary["mode_extensions"][GUESSENC_MODES[key]] = int(number)
        elif key.removesuffix(" granules") in GUESSENC_BLOCKS:
            summary["blocks"][GUESSENC_BLOCKS[key.removesuffix(" granules")]] = int(number)
        elif key in ("Min global gain", "Max global gain"):
            for channel, gain in re.findall(r"([lrc])=\s*(\d+)", value):
                gains.setdefault("1" if channel == "r" else "0", []).append(int(gain))
        elif key == "Max reservoir":
            summary["max_main_data_begin"] = int(number)
        elif key in ("Total amount", "Min packet", "Max packet"):
            summary["ancillary"][["Total amount", "Min packet", "Max packet"].index(key)] = int(number)
        elif key == "Scalefactor scaling used":
            summary["scalefac_scale_used"] = value == "yes"
        elif key == "Scalefactor selection information used":
            summary["scfsi_used"] = value == "yes"
    summary["global_gain"] = {channel: sorted(values) for channel, values in sorted(gains.items())}
    return summary


def decodes_to_silence(path):
    problems = []
    commands = {
        "ffmpeg": ["ffmpeg", "-v", "error", "-i", str(path), "-f", "s16le", "-"],
        "mpg123": ["mpg123", "-q", "-s", str(path)],
        "madplay": ["madplay", "-q", "--no-dither", "--stereo", "-o", "raw:-", str(path)],
    }
    for name, command in commands.items():
        result = subprocess.run(command, capture_output=True)
        if not result.stdout or any(result.stdout) or result.stderr.strip():
            problems.append(f"{name} does not decode to silence without errors: {result.stderr.decode()[:200].strip()}")
    return problems


def check_side_info(path, manifest, dumper):
    problems = []
    rows = {row["offset"]: row for row in minimp3_tool.side_info(dumper, path)}
    frames = manifest["frames"]
    gap_ends = {gap["offset"] + gap["length"] for gap in manifest["gaps"]}
    breaks = [i for i in range(1, len(frames)) if frames[i]["offset"] in gap_ends
              or frames[i]["fields"]["Header/Sample Rate"] != frames[i - 1]["fields"]["Header/Sample Rate"]]
    previous = None
    for i, frame in enumerate(frames):
        row = rows.get(frame["offset"])
        if row is None:
            if not breaks or i >= breaks[-1]:
                problems.append(f"minimp3 does not decode the frame at {frame['offset']}")
            previous = None
            continue
        actual = {key: row[key] for key in ("main_data_begin", "granules") if key in row}
        if actual != frame["side_info"]:
            problems.append(f"minimp3 reads different side information at {frame['offset']}: {json.dumps(actual)[:300]}")
        discarded = previous is None or previous["main_data_begin"] < 0 or previous["reservoir"] < previous["main_data_begin"]
        if frame["offset"] not in gap_ends and not discarded and row["reservoir"] != min(frame["reservoir"], 511):
            problems.append(f"minimp3 has {row['reservoir']} bytes in the reservoir at {frame['offset']}, the manifest {frame['reservoir']}")
        previous = row
    return problems


def check_scale_info(path, manifest, dumper):
    """Layer II allocation, scfsi and scalefactors as minimp3 reads them; scalefactors as floats."""
    rows = {row["offset"]: row for row in minimp3_tool.side_info(dumper, path)}
    normal = lambda info: (info["bands"], info["bound"], info["scale_info_bits"], [
        [{k: [float.fromhex(s) for s in v] if k == "scalefactors" else v for k, v in channel.items()} for channel in subband]
        for subband in info["subbands"]])
    problems = []
    for frame in manifest["frames"]:
        row = rows.get(frame["offset"])
        if row is None or normal(row) != normal(frame["scale_info"]):
            problems.append(f"minimp3 reads different allocation, scfsi or scalefactors at {frame['offset']}")
    return problems


GUESSENC_MODES_BY_MISC = {0: "mono", 4: "simple LR stereo", 8: "dual channel", 12: "joint stereo", 16: "forced MS stereo", 20: "auto",
                          24: "intensity stereo"}


def guessenc_tag_lines(path):
    """mp3guessenc's report of the VBR header, the music CRC and the encoder string, by label (first occurrence)."""
    output = subprocess.run(["mp3guessenc", "-v", "-r", "-s", str(path)], capture_output=True, text=True).stdout
    lines = {}
    for line in output.splitlines():
        if " tag detected into the first frame" in line:
            lines.setdefault("detected", line.split()[0])
        elif line.startswith("Music CRC verification"):
            lines.setdefault("music crc", line.split()[-1].rstrip("."))
        elif ":" in line:
            key, _, value = (part.strip() for part in line.partition(":"))
            lines.setdefault(key, value)
    return lines


def guessenc_lame_recognized(encoder):
    """mp3guessenc reads a LAME tag after "LAME", "L<digit>.<digit><digit>" or "lavc" (any case)."""
    return encoder.startswith("LAME") or re.match(r"L\d\.\d\d", encoder) is not None or encoder[:4].lower() == "lavc"


def guessenc_preset(value):
    if value == 0:
        return "No preset."
    if value < 321:
        return f"{value} kbps"
    if 409 < value < 501:
        return f"V{50 - value // 10} / VBR_{value - 400}"
    return {1000: "R3mix.", 1001: "Standard.", 1002: "Extreme.", 1003: "Insane.", 1004: "Standard fast.", 1005: "Extreme fast.",
            1006: "Medium.", 1007: "Medium fast."}.get(value, "Unknown preset.")


def guessenc_strategy(method, bitrate):
    if method in (2, 9):
        prefix = "ABR, "
    elif method in (1, 8):
        prefix = "CBR, "
    elif 2 < method < 7:
        prefix = f"VBR method {'old/rh' if method == 3 else 'mtrh' if method == 4 else 'mt'}, min "
    else:
        prefix = ""
    rate = "unknown" if bitrate == 0 else f"{bitrate} kbps" + (" or higher" if bitrate == 255 else "")
    return prefix + rate


def expected_guessenc(tag, music_crc):
    expected = {}
    if tag["type"] == "VBRI":
        size = tag["entry_size"]
        expected.update({"detected": "VBRI", "Tag version": str(tag["version"]), "Encoder delay": f"{tag['delay']} samples",
                         "File size": f"{tag['bytes']} bytes", "Number of frames": str(tag["frames"]), "Quality": str(tag["quality"]),
                         "TOC": f"{size * tag['toc_entries']} bytes ({tag['toc_entries']} entries, {size} byte{'' if size == 1 else 's'} each)"})
        return expected
    expected.update({"detected": "Xing", "File size": f"{tag['bytes'] or 0} bytes", "Number of frames": str(tag["frames"] or 0),
                     "TOC": "100 bytes (100 entries, 1 byte each)" if tag["toc"] else "no"})
    lame = tag.get("lame")
    if lame and guessenc_lame_recognized(lame["encoder"]):
        misc, flags, verified = lame["misc"], lame["flags"], lame["tag_crc"] == lame["tag_crc_computed"]
        expected.update({
            "Lame tag": "yes",
            "Lame short string": lame["encoder"],
            "Tag revision": "invalid!" if lame["revision"] == 15 else str(lame["revision"]),
            "Bitrate strategy": guessenc_strategy(lame["method"], lame["bitrate"]),
            "Lowpass value": str(lame["lowpass"] * 100) if lame["lowpass"] else "unknown",
            "nspsytune": "yes" if flags & 0x10 else "no",
            "nssafejoint": "yes" if flags & 0x20 else "no",
            "nogap continued": "yes" if flags & 0x40 else "no",
            "nogap continuation": "yes" if flags & 0x80 else "no",
            "ATH type": str(flags & 15),
            "Encoder delay (start)": f"{lame['delay']} samples",
            "Encoder padding (end)": f"{lame['padding']} samples",
            "Encoding mode": GUESSENC_MODES_BY_MISC.get(misc & 0x1C, "other"),
            "Unwise settings": "used" if misc & 0x20 else "not used",
            "Source frequency": ["32 kHz or below", "44.1 kHz", "48 kHz", "higher than 48 kHz"][misc >> 6],
            "Preset": guessenc_preset(lame["preset_surround"] & 0x7FF),
            "Originally encoded": f"{lame['music_length']} bytes",
            "Tag verification": "passed" if verified else "failed",
        })
        # mp3guessenc checks the music CRC, when the tag's holds, over every frame after the tag
        # frame, not over the music length.
        if not verified:
            expected["music crc"] = "performed"
        else:
            expected["music crc"] = "passed" if lame["music_crc"] == music_crc else "failed"
    else:
        expected["Lame tag"] = "no"
    return expected


def expected_mpg123(tag):
    """The notes mpg123 -vvv prints about a Xing/Info header and LAME tag (libmpg123 parse.c, check_lame_tag)."""
    notes = ["Xing/Lame/Info header detected"]
    if tag["frames"] is not None:
        notes.append(f"Xing: {tag['frames']} frames")
    if tag["bytes"] is not None:
        notes.append(f"Xing: {tag['bytes']} bytes")
    if tag["quality"] is not None:
        notes.append(f"Xing: quality = {tag['quality']}")
    lame = tag.get("lame")
    if lame:
        notes.append(f"Info: Encoder: {lame['encoder']}")
        notes.append(f"Info: vbr mode {lame['method']}")
        version = re.match(r"LAME(\d+)\.(\d+)", lame["encoder"])
        offset = 6 if version and (int(version[1]), int(version[2])) < (3, 95) else 0
        gains = [0.0, 0.0]
        for value in (lame["radio_gain"], lame["audiophile_gain"]):
            name, origin = value >> 13, (value >> 10) & 7
            if origin and name in (1, 2):
                gains[name - 1] = (-0.1 if value & 0x200 else 0.1) * (value & 0x1FF) + (offset if origin == 3 else 0)
        notes.append(f"Info: Radio Gain = {gains[0]:03.1f}dB")
        notes.append(f"Info: Audiophile Gain = {gains[1]:03.1f}dB")
        if lame["method"] in (2, 9):
            notes.append(f"Info: ABR rate = {lame['bitrate']}")
        notes.append(f"Encoder delay = {lame['delay']}; padding = {lame['padding']}")
    return notes


def frames_after_tag(manifest):
    if "frames" in manifest:
        return [(f["offset"], f["length"]) for f in manifest["frames"][1:] if not f.get("truncated")]
    return [tuple(f) for f in manifest.get("ffprobe_packets", [])]


def check_vbr_header(path, manifest):
    """mp3guessenc, mpg123 and ffprobe must read the first frame's VBR header as the manifest records it."""
    tag = manifest["vbr_header"]
    problems = []
    data = path.read_bytes()
    frames = frames_after_tag(manifest)
    music = b"".join(data[offset : offset + length] for offset, length in frames)
    # Without a frame list (free format), the bytes after the tag frame, which the manifest's own CRC covers.
    lame = tag.get("lame") if tag else None
    music_crc = vbr_tags.crc16(music) if frames or not lame else lame["music_crc_computed"]
    guessenc = guessenc_tag_lines(path)
    layer3 = "frames" not in manifest or manifest["frames"][0]["fields"]["Header/Layer"] == "III"
    if tag is None:
        # mp3guessenc looks for Xing in every layer; LAME, ffmpeg and mpg123 only in Layer III.
        if "detected" in guessenc and layer3:
            problems.append(f"mp3guessenc finds a {guessenc['detected']} header the manifest does not list")
    elif tag["truncated"]:
        if guessenc.get("detected") != ("VBRI" if tag["type"] == "VBRI" else "Xing"):
            problems.append("mp3guessenc does not find the truncated VBR header")
    else:
        expected = expected_guessenc(tag, music_crc)
        if tag["type"] != "VBRI" and tag["quality"] is not None:
            expected["Quality"] = str(tag["quality"])
        for key, value in expected.items():
            # mp3guessenc appends the -q and -V switches a LAME VBR quality stands for.
            actual = guessenc.get(key, "").split(" (")[0] if key == "Quality" else guessenc.get(key)
            if actual != value:
                problems.append(f"mp3guessenc reports {key} {actual!r}, the manifest implies {value!r}")
    if "mp3guessenc_encoder_string" in manifest and guessenc.get("Encoder string") != manifest["mp3guessenc_encoder_string"]:
        problems.append(f"mp3guessenc finds the encoder string {guessenc.get('Encoder string')!r}, the manifest "
                        f"{manifest['mp3guessenc_encoder_string']!r}")

    notes = [line[len("Note: "):] for line in subprocess.run(["mpg123", "-vvv", "-t", str(path)], capture_output=True, text=True).stderr.splitlines()
             if line.startswith("Note: ") and (line.startswith("Note: Xing") or line.startswith("Note: Info: ") or line.startswith("Note: Encoder delay"))]
    # mpg123 prints the lowpass byte as "rev", never reads the peak, and compares the byte count with the file.
    notes = [n for n in notes if not n.startswith(("Info: rev ", "Info: peak", "Xing stream size")) and "Old LAME detected" not in n]
    if tag is None or tag["type"] == "VBRI":
        if notes:
            problems.append(f"mpg123 reads a header the manifest does not expect: {notes}")
    elif not tag["truncated"] and "encoder_string" not in tag and notes != expected_mpg123(tag):
        problems.append(f"mpg123 notes {notes}, the manifest implies {expected_mpg123(tag)}")

    # ffmpeg does not read free format.
    if tag is not None and not tag["truncated"] and not manifest.get("encoder", {}).get("free_format"):
        stream = json.loads(subprocess.run(["ffprobe", "-v", "error", "-show_streams", "-of", "json", str(path)],
                                           capture_output=True, text=True, check=True).stdout)["streams"][0]
        rate, mpeg1 = int(stream["sample_rate"]), rate_is_mpeg1(int(stream["sample_rate"]))
        # ffmpeg drops the frame count of a file much longer than the header's byte count, as
        # "invalid concatenated file" (mp3dec.c, mp3_parse_info_tag).
        size = len(data) - tag["frame_offset"] - 4
        concatenated = tag["bytes"] and size > tag["bytes"] and size - tag["bytes"] > tag["bytes"] >> 4
        if tag["frames"] and not concatenated:
            duration = f"{tag['frames'] * (1152 if mpeg1 else 576) / rate:.6f}"
            if stream.get("duration") != duration:
                problems.append(f"ffprobe duration {stream.get('duration')}, the header's frame count implies {duration}")
        lame = tag.get("lame")
        delay = ffmpeg_delay(tag)
        start = f"{(delay + 529) / rate:.6f}" if delay is not None else "0.000000"
        if stream.get("start_time") != start:
            problems.append(f"ffprobe start time {stream.get('start_time')}, the manifest implies {start}")
        valid = lame is not None and lame["tag_crc"] == lame["tag_crc_computed"]
        encoder = stream.get("tags", {}).get("encoder")
        if encoder != (lame["encoder"] if valid else None):
            problems.append(f"ffprobe encoder {encoder!r}, the manifest implies {lame['encoder'] if valid else None!r}")
        gains = valid and (lame["radio_gain"] >> 13 == 1 or lame["audiophile_gain"] >> 13 == 2)
        has_gain = any(d.get("side_data_type") == "Replay Gain" for d in stream.get("side_data_list", []))
        if has_gain != bool(gains):
            problems.append(f"ffprobe {'finds' if has_gain else 'does not find'} ReplayGain, the manifest implies the opposite")
    return problems


def ffmpeg_delay(tag):
    """The encoder delay ffmpeg reads: after any string starting LAME, Lavf or Lavc, a full tag or not."""
    after = bytes.fromhex(tag.get("after_fields", "")) if tag else b""
    return (after[21] << 4 | after[22] >> 4) if len(after) >= 24 and after[:4] in (b"LAME", b"Lavf", b"Lavc") else None


def rate_is_mpeg1(rate):
    return rate in (32000, 44100, 48000)


def ffprobe_skips_first(manifest):
    """ffmpeg skips the first frame when it holds a Layer III Xing, Info or VBRI header with a frame or byte count."""
    tag = manifest.get("vbr_header")
    return tag is not None and bool(tag.get("frames") or tag.get("bytes"))


def check(path, manifest, tools):
    dumper, decoder = tools
    problems = []
    if manifest.get("silent"):
        problems += decodes_to_silence(path)
    if manifest.get("frames") and "scale_info" in manifest["frames"][0]:
        problems += check_scale_info(path, manifest, dumper)
    if "mctoolamed" in manifest:
        errors, silent, channels = mctoolamed_tool.decode(decoder, path)
        if errors != manifest["mctoolamed"]["crc_errors"] or not silent:
            problems.append(f"mctoolamed reports {errors} multichannel CRC errors, silent {silent}, {channels} channels")
    if "mp3guessenc_multichannel" in manifest and guessenc_multichannel(path) != manifest["mp3guessenc_multichannel"]:
        problems.append(f"mp3guessenc reports {guessenc_multichannel(path)}, the manifest {manifest['mp3guessenc_multichannel']}")
    if manifest.get("frames") and "side_info" in manifest["frames"][0]:
        problems += check_side_info(path, manifest, dumper)
    if "minimp3_side_info" in manifest and minimp3_tool.side_info(dumper, path) != manifest["minimp3_side_info"]:
        problems.append("minimp3 reads different side information than the manifest recorded")
    if "vbr_header" in manifest:
        problems += check_vbr_header(path, manifest)
    if "mp3guessenc" in manifest:
        expected = dict(manifest["mp3guessenc"])
        expected["mode_extensions"] = {k: v for k, v in expected["mode_extensions"].items() if k != "None"}
        actual = guessenc_summary(path)
        if actual != expected:
            problems.append(f"mp3guessenc reports {json.dumps(actual)}, the manifest {json.dumps(expected)}")
    if not manifest.get("crosscheck"):
        return problems

    if manifest["crosscheck"] in ("madplay", "mpg123"):
        if manifest["crosscheck"] == "madplay":
            count = madplay_frame_count(path)
        else:
            count = mpg123_frame_count(path, *frame_shape(manifest["frames"][0]["fields"]))
        if count != len(manifest["frames"]):
            problems.append(f"{manifest['crosscheck']} decodes {count} frames, the manifest lists {len(manifest['frames'])}")
        return problems

    if "frames" in manifest:
        expected = [(f["offset"], f["length"]) for f in manifest["frames"] if not f.get("truncated")]
        if ffprobe_skips_first(manifest):
            expected = expected[1:]
            # ffmpeg skips delay + 529 samples when decoding, so frames it skips whole are not listed.
            delay = ffmpeg_delay(manifest["vbr_header"])
            if delay is not None:
                samples, _ = frame_shape(manifest["frames"][0]["fields"])
                expected = expected[(delay + 529) // samples :]
        try:
            actual = ffprobe_frames(path)
        except subprocess.CalledProcessError as error:
            return [f"ffprobe failed: {error.stderr.strip()[:200]}"]
        # ffprobe drops a truncated final frame, which the manifest marks.
        if actual != expected:
            problems.append(f"ffprobe frames {actual[:4]}... ({len(actual)}) differ from the manifest {expected[:4]}... ({len(expected)})")

        protected = [f for f in manifest["frames"] if f["fields"].get("Header/IsProtected") == "true"]
        if protected:
            expected_failures = sum(f["fields"]["CRC"] != f["fields"]["CRC/Computed"] for f in protected)
            failures = madplay_crc_failures(path)
            if failures != expected_failures:
                problems.append(f"madplay reports {failures} CRC failures, the manifest expects {expected_failures}")

    if "encoder" in manifest:
        settings = manifest["encoder"]
        if settings.get("free_format"):
            count = mpg123_frame_count(path, 1152, CHANNELS.get(settings["channel_mode"], 2))
            if count != manifest["mpg123_frames"]:
                problems.append(f"mpg123 decodes {count} frames, the manifest recorded {manifest['mpg123_frames']}")
        else:
            if ffprobe_packets(path) != [tuple(f) for f in manifest["ffprobe_packets"]]:
                problems.append("ffprobe's packets differ from the ones recorded in the manifest")
            stream = ffprobe_stream(path)
            if int(stream["sample_rate"]) != settings["sample_rate"]:
                problems.append(f"ffprobe sample rate {stream['sample_rate']} differs from {settings['sample_rate']}")
            if int(stream["channels"]) != CHANNELS.get(settings["channel_mode"], 2):
                problems.append(f"ffprobe channels {stream['channels']} differ from {settings['channel_mode']}")
            if settings.get("bitrate") and int(stream["bit_rate"]) != settings["bitrate"] * 1000:
                problems.append(f"ffprobe bitrate {stream['bit_rate']} differs from {settings['bitrate']} kbps")
        if settings.get("protected") and madplay_crc_failures(path) != 0:
            problems.append("madplay reports CRC failures in an encoder-protected stream")

    return problems


def main():
    failed = 0
    checked = 0
    tools = (minimp3_tool.build(), mctoolamed_tool.build())
    for manifest_path in sorted(p for directory in DIRECTORIES for p in directory.glob("*.expected.json")):
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        name = manifest_path.name[: -len(".expected.json")]
        path = next(p for p in manifest_path.parent.glob(name + ".*") if not p.name.endswith(".json"))
        problems = check(path, manifest, tools)
        checked += 1
        for problem in problems:
            failed += 1
            print(f"{path.name}: {problem}")
    print(f"Cross-checked {checked} fixtures, {failed} problems.", file=sys.stderr)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
