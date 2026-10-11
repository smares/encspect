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
- "mp3guessenc_guess": the encoder mp3guessenc guesses, which for mp3PRO and MP3 Surround follows
  the signature in the ancillary data (ancillary_scan.py). Every fixture must have the OFL block
  mp3guessenc reads ("mp3guessenc_ofl"), or none; where the Fraunhofer encoder wrote one,
  ffmpeg's output must start within 16 samples of its delay ("decoded_onset").
- "statistics" per region (stream_stats.py): mp3guessenc must count the same frames, length,
  average bitrate and bitrate histogram where it takes the same frames as audio (no gaps, one
  region and stream, constant header flags, no finding about frames it skips), and ffmpeg and
  mpg123 must decode the gapless sample count where the VBR header's counts hold and the padding
  covers their 529-sample decoder delay. At an exact tie, mp3guessenc's floating-point rounding
  may differ by one in the last digit. External fixtures record mp3guessenc's figures
  ("mp3guessenc_stream") and the decoders' sample counts ("decoded_samples") instead.

External fixtures list their encoder settings and ffprobe's packets instead of every frame; for
them, ffprobe's sample rate, channel count and bitrate must match those settings, and minimp3 and
mp3guessenc must still report what the manifest recorded.

Needs ffprobe, madplay, mpg123, curl, make and a C compiler on the PATH; minimp3 and mp3guessenc
are built from third_party. Exits
non-zero when anything disagrees.
"""

import json
import pathlib
import re
import subprocess
import sys

import guessenc_tool
import mctoolamed_tool
import minimp3_tool
import stream_stats
import vbr_tags
from conformance_mpeg_audio import guessenc as guessenc_multichannel

ROOT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures"
DIRECTORIES = [ROOT / "mpeg", ROOT / "layer3", ROOT / "layer2", ROOT / "vbr", ROOT / "stats"]
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
    output = subprocess.run([guessenc_tool.binary(), "-e", str(path)], capture_output=True, text=True).stdout
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


def decodes_to_silence(path, manifest):
    problems = []
    commands = {
        "ffmpeg": ["ffmpeg", "-v", "error", "-i", str(path), "-f", "s16le", "-"],
        "mpg123": ["mpg123", "-q", "-s", str(path)],
        "madplay": ["madplay", "-q", "--no-dither", "--stereo", "-o", "raw:-", str(path)],
    }
    first = manifest["frames"][0]["fields"]
    if first["Header/Bitrate"] == "free":
        # ffmpeg does not read free format, and mpg123 reads free-format Layer II with table B.2a
        # at every rate (layer2.c, II_select_table), where the captions of Tables 3-B.2a and
        # 3-B.2b give B.2b below 48000 Hz, as libmad reads it.
        del commands["ffmpeg"]
        if first["Header/Layer"] == "II" and first["Header/Sample Rate"] != "48000 Hz":
            del commands["mpg123"]
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
    output = subprocess.run([guessenc_tool.binary(), "-v", "-r", "-s", str(path)], capture_output=True, text=True).stdout
    lines = {}
    for line in output.splitlines():
        if " tag detected into the first frame" in line:
            lines.setdefault("detected", line.split()[0])
        elif line.startswith("Unexpected data at "):
            lines.setdefault("unexpected", line)
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
        version = re.match(r"LAME(\d+)\.(\d+)", lame["encoder"])
        if lame["encoder"].startswith("LAME") and not version:
            notes.append("Info: Cannot determine LAME version.")
        notes.append(f"Info: vbr mode {lame['method']}")
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
    elif "unexpected" in guessenc:
        # mp3guessenc stops at some leading tags, such as an ID3v2.4 tag with a SEEK frame, and
        # never reaches the header.
        pass
    elif tag["truncated"]:
        if guessenc.get("detected") != ("VBRI" if tag["type"] == "VBRI" else "Xing"):
            problems.append("mp3guessenc does not find the truncated VBR header")
    else:
        expected = expected_guessenc(tag, music_crc)
        # mp3guessenc reads 0xFFFFFFFF, as Helix writes it for CBR, as -1: its mark for no quality.
        if tag["type"] != "VBRI" and tag["quality"] is not None and tag["quality"] != 0xFFFFFFFF:
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
    if ffmpeg_reads(tag) and not tag["truncated"] and not manifest.get("encoder", {}).get("free_format"):
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


def ffmpeg_reads(tag):
    """Whether ffmpeg reads the header: it ignores a VBRI header of a version other than 1 (mp3dec.c,
    mp3_parse_vbri_tag)."""
    return tag is not None and (tag["type"] != "VBRI" or tag.get("version") == 1)


def ffprobe_skips_first(manifest):
    """ffmpeg skips the first frame when it holds a Layer III Xing, Info or VBRI header with a frame or byte count."""
    tag = manifest.get("vbr_header")
    return ffmpeg_reads(tag) and bool(tag.get("frames") or tag.get("bytes"))


def guessenc_stream(path):
    """mp3guessenc's figures for the stream: frames, length, average bitrate and the bitrate histogram."""
    output = subprocess.run([guessenc_tool.binary(), "-m", "-f", str(path)], capture_output=True, text=True).stdout
    stream = {"histogram": {}}
    in_stream = False
    for line in output.splitlines():
        if line.startswith("Detected MPEG stream"):
            in_stream = True
        key, _, value = (part.strip() for part in line.partition(":"))
        if in_stream and key in ("Length", "Data rate", "Number of frames") and key not in stream:
            stream[key] = value.removesuffix(" (free format bitstream)")
        match = re.match(r"\s*(\d+|free) kbps\s*:\s*(\d+)\s*\(\s*([\d.]+)%\)", line)
        if match:
            stream["histogram"][match[1]] = (int(match[2]), match[3])
    return stream


def region_infos(manifest, region):
    """A region's frames as stream_stats.FrameInfo, from the manifest."""
    infos = []
    for frame in manifest["frames"]:
        if not region["offset"] <= frame["offset"] < region["offset"] + region["length"]:
            continue
        fields = frame["fields"]
        bitrate = fields["Header/Bitrate"]
        header = {} if "Xing" in fields or any(k.startswith("VBRI") for k in fields) else None
        infos.append(stream_stats.FrameInfo(frame["offset"], frame["length"], fields["Header/Version"], fields["Header/Layer"],
                                            int(fields["Header/Sample Rate"].split()[0]),
                                            None if bitrate == "free" else int(bitrate.split()[0]),
                                            header=header, truncated=frame.get("truncated", False)))
    return infos


def guessenc_problems(actual, statistics, exact):
    """Differences between mp3guessenc's stream figures and a region's statistics; at an exact tie
    either rounding counts as agreement (stream_stats.agrees)."""
    problems = []
    if actual.get("Number of frames") != statistics["Statistics/Frames"]:
        problems.append(f"mp3guessenc counts {actual.get('Number of frames')} frames, the manifest {statistics['Statistics/Frames']}")
    hours, minutes, seconds = actual.get("Length", "0:0:0").split(":")
    ms = (int(hours) * 3600 + int(minutes) * 60) * 1000 + stream_stats.Fraction(seconds) * 1000
    if not stream_stats.agrees(str(ms), exact["ms"], stream_stats.Fraction(1)):
        problems.append(f"mp3guessenc's length {actual.get('Length')} disagrees with {statistics['Statistics/Duration']}")
    rate = actual.get("Data rate", "0 kbps").split()[0]
    if not stream_stats.agrees(rate, exact["kbps"], stream_stats.Fraction(1, 10)):
        problems.append(f"mp3guessenc's data rate {actual.get('Data rate')} disagrees with {statistics['Statistics/Average Bitrate']}")
    # mp3guessenc leaves free-format frames out of its histogram.
    expected_bars = {str(b): c for b, c in exact["counts"].items() if b is not None}
    if {k: v[0] for k, v in actual["histogram"].items()} != expected_bars:
        problems.append(f"mp3guessenc's histogram {actual['histogram']} disagrees with the counts {expected_bars}")
    for bitrate, (_, percent) in actual["histogram"].items():
        share = exact["percent"].get(int(bitrate))
        if share is None or not stream_stats.agrees(percent, share, stream_stats.Fraction(1, 10)):
            problems.append(f"mp3guessenc's {bitrate} kbps share {percent}% disagrees with {share}")
    return problems


def guessenc_guess(path):
    """The encoder mp3guessenc guesses, from its "Maybe this file is encoded by" line, or None."""
    output = subprocess.run([guessenc_tool.binary(), str(path)], capture_output=True, text=True).stdout
    for line in output.splitlines():
        if line.startswith("Maybe this file is encoded by "):
            return line[len("Maybe this file is encoded by "):].strip()
    return None


def guessenc_ofl(path):
    """The delay and original length mp3guessenc reads from an Original File Length block, or None.
    For MP3 Surround it adds 576 samples to the delay it reads."""
    output = subprocess.run([guessenc_tool.binary(), str(path)], capture_output=True, text=True).stdout
    if "Original File Length block found" not in output:
        return None
    ofl = {}
    for line in output.splitlines():
        key, _, value = (part.strip() for part in line.partition(":"))
        if key == "Encoder delay":
            ofl["delay"] = int(value.split()[0])
        elif key == "Length of original audio":
            ofl["length"] = int(value.split()[0])
    return ofl


def decoded_onset(path):
    """The first sample of ffmpeg's mono downmix louder than 200: where an encoded tone that starts at
    sample 0 comes out, the encoder's and the decoder's delay together."""
    pcm = subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-ac", "1", "-f", "s16le", "-"], capture_output=True).stdout
    for i in range(0, len(pcm) - 1, 2):
        if abs(int.from_bytes(pcm[i : i + 2], "little", signed=True)) > 200:
            return i // 2
    return None


DECODER_DELAY = 529


def decoded_samples(path, channels):
    """Samples per channel ffmpeg and mpg123 output with their gapless trimming, which follows the LAME tag."""
    counts = {}
    for name, command in [("ffmpeg", ["ffmpeg", "-v", "error", "-i", str(path), "-f", "s16le", "-"]),
                          ("mpg123", ["mpg123", "-q", "-s", str(path)])]:
        counts[name] = len(subprocess.run(command, capture_output=True).stdout) // (2 * channels)
    return counts


# Findings about frames mp3guessenc skips or reads differently; the others leave its counts alone.
GUESSENC_NEUTRAL = ("mpeg.vbr-", "mpeg.lame-", "mpeg.music-", "mpeg.xing-", "mpeg.gapless-")
# mp3guessenc reads free format, but takes every frame to be as long as the first.
GUESSENC_NEUTRAL_CODES = ("mpeg.free-format",)
VARIABLE_HEADER_FIELDS = {"Header/Bitrate", "Header/HasPadding", "Header/Mode Extension"}


def guessenc_counts_like_us(path, manifest):
    """Whether mp3guessenc takes the same frames as audio: it skips frames whose constant header
    fields change, frames that fail their CRC or hold invalid side information, and Layer II modes
    the standard does not allow; it reads "Xing" in any layer, and only the first VBR header."""
    frames = manifest["frames"]
    constant = [{k: v for k, v in f["fields"].items() if k.startswith("Header/") and k not in VARIABLE_HEADER_FIELDS} for f in frames]
    headers = sum(1 for f in frames if "Xing" in f["fields"] or "VBRI/Version" in f["fields"] or "VBRI" in f["fields"])
    return (sum(1 for r in manifest["regions"] if "statistics" in r) == 1 and not manifest["gaps"]
            and all(c == constant[0] for c in constant) and headers <= 1
            and all(f["code"].startswith(GUESSENC_NEUTRAL) or f["code"] in GUESSENC_NEUTRAL_CODES for f in manifest["findings"])
            and ("detected" in guessenc_tag_lines(path)) == (manifest.get("vbr_header") is not None))


def check_statistics(path, manifest):
    """mp3guessenc must count the frames, length, average bitrate and histogram of a single-region
    stream as the manifest does, and ffmpeg and mpg123 must decode the gapless sample count where
    the VBR header's counts hold; decoders trim by the counts the header claims."""
    problems = []
    regions = [r for r in manifest["regions"] if "statistics" in r]
    if guessenc_counts_like_us(path, manifest):
        problems += guessenc_problems(guessenc_stream(path), regions[0]["statistics"], stream_stats.exact(region_infos(manifest, regions[0])))
    consistent = not any(f["code"] in ("mpeg.vbr-frame-count-mismatch", "mpeg.vbr-header-mid-stream") for f in manifest["findings"])
    for region in regions:
        count = region["statistics"].get("Statistics/Gapless/Sample Count")
        padding = int(region["statistics"].get("Statistics/Gapless/Encoder Padding", "0 samples").split()[0])
        # Decoders discard their own 529-sample delay at the end too; with less padding (the first
        # file of a --nogap sequence) those samples belong to the next file.
        if count is not None and consistent and padding >= DECODER_DELAY:
            channels = 1 if manifest["frames"][0]["fields"]["Header/Channel Mode"] == "Mono" else 2
            for decoder, samples in decoded_samples(path, channels).items():
                if samples != int(count):
                    problems.append(f"{decoder} decodes {samples} samples with gapless trimming, the manifest {count}")
    return problems


def check(path, manifest, tools):
    dumper, decoder = tools
    problems = []
    if manifest.get("silent"):
        problems += decodes_to_silence(path, manifest)
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
    if "regions" in manifest:
        problems += check_statistics(path, manifest)
    if "mp3guessenc" in manifest:
        expected = dict(manifest["mp3guessenc"])
        expected["mode_extensions"] = {k: v for k, v in expected["mode_extensions"].items() if k != "None"}
        actual = guessenc_summary(path)
        if actual != expected:
            problems.append(f"mp3guessenc reports {json.dumps(actual)}, the manifest {json.dumps(expected)}")
    if "mp3guessenc_guess" in manifest and guessenc_guess(path) != manifest["mp3guessenc_guess"]:
        problems.append(f"mp3guessenc guesses {guessenc_guess(path)!r}, the manifest recorded {manifest['mp3guessenc_guess']!r}")
    if guessenc_ofl(path) != manifest.get("mp3guessenc_ofl"):
        problems.append(f"mp3guessenc reads the OFL block as {guessenc_ofl(path)}, the manifest recorded {manifest.get('mp3guessenc_ofl')}")
    if not manifest.get("crosscheck"):
        return problems

    if manifest["crosscheck"] in ("madplay", "mpg123"):
        if manifest["crosscheck"] == "madplay":
            count = madplay_frame_count(path)
        else:
            count = mpg123_frame_count(path, *frame_shape(manifest["frames"][0]["fields"]))
        # Both skip a VBR header in the first frame, as ffmpeg does.
        expected = len(manifest["frames"]) - (1 if ffprobe_skips_first(manifest) else 0)
        if count != expected:
            problems.append(f"{manifest['crosscheck']} decodes {count} frames, the manifest lists {expected} audio frames")
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

    if "decoded_onset" in manifest and decoded_onset(path) != manifest["decoded_onset"]:
        problems.append(f"ffmpeg's output starts at sample {decoded_onset(path)}, the manifest recorded {manifest['decoded_onset']}")
    if "decoded_onset" in manifest:
        # The tone comes out within a few samples of the delay the OFL block stores, as the filter bank
        # spreads it; a delay without the decoder's 529 samples would be far off.
        delay = manifest["mp3guessenc_ofl"]["delay"] - (576 if manifest.get("enhancement") == "MP3 Surround" else 0)
        if abs(manifest["decoded_onset"] - delay) > 16:
            problems.append(f"ffmpeg's output starts at sample {manifest['decoded_onset']}, not at the OFL delay {delay}")
    if "mp3guessenc_stream" in manifest:
        # Through JSON, so the histogram's tuples compare as the manifest's lists.
        actual = json.loads(json.dumps(guessenc_stream(path)))
        if actual != manifest["mp3guessenc_stream"]:
            problems.append(f"mp3guessenc's stream figures {actual} differ from the ones recorded in the manifest")
    if "decoded_samples" in manifest:
        settings = manifest["encoder"]
        actual = decoded_samples(path, CHANNELS.get(settings["channel_mode"], 2))
        if actual != manifest["decoded_samples"]:
            problems.append(f"ffmpeg and mpg123 decode {actual} samples, the manifest recorded {manifest['decoded_samples']}")
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
