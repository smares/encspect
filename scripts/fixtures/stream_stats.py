"""The statistics the analyzer reports for each MPEG Audio region and each stream, computed from a
fixture's frames.

This is the second implementation the analyzer's statistics are checked against; the cross-check
confirms both with mp3guessenc, mpg123 and ffmpeg. Its definitions:

- Statistics count the complete audio frames of a region: not a frame that holds a VBR header,
  not a truncated last frame. Time runs on a clock of 1/14,112,000 s (ffmpeg's time base for MP3,
  which every MPEG sample rate divides), so frames of different sample rates add up exactly.
- Rounding is half to even, as printf rounds mp3guessenc's figures, on exact fractions.
- A stream is a run of frames of one version, layer, sample rate and free format or not, in file
  order across gaps and tags; the next frame of another kind starts the next stream. Its "bitrate
  over time" holds, for each second of audio, the bitrate of the frames that start in it (their
  bits over their duration) in kbps; its "reservoir fill over time" the mean reservoir fill of the
  Layer III frames that start in it, in bytes, as each frame's side information reports it. Both
  are comma-separated, for streams of Layer III frames only in the case of the reservoir.
- "Gapless": with a LAME tag, the samples of the frames the tag covers minus its encoder delay and
  padding (LAME and ffmpeg's convention; decoders that trim by them output exactly that many).
- "VBR Header": the frame and byte counts a header claims, and the ones counted from its frame to
  the end of its stream: the next VBR header, a change of version, layer or sample rate, or the end
  of the file, across gaps and tags. Xing's and Fraunhofer's encoders count the header's own frame
  and LAME does not, so either count is consistent; byte counts include it. A truncated last frame
  was written whole, so it counts here too.
- Each header of a region has its own "VBR Header" and "Gapless" ("VBR Header[1]" for the second).
  Where a header is not in the region's first frame or does not describe all its frames (files were
  joined), their value says which frames they cover.
"""

from fractions import Fraction

CLOCK = 14_112_000
VERSIONS = {"MPEG-1": "1", "MPEG-2": "2", "MPEG-2.5": "2.5"}


def rounded(value):
    """A Fraction rounded half to even."""
    return round(Fraction(value))


def samples_per_frame(version, layer):
    if layer == "I":
        return 384
    return 1152 if layer == "II" or version == "MPEG-1" else 576


class FrameInfo:
    """What the statistics need of one frame."""

    def __init__(self, offset, length, version, layer, rate, bitrate, header=None, truncated=False, fill=None):
        self.offset, self.length, self.version, self.layer, self.rate = offset, length, version, layer, rate
        self.bitrate = bitrate  # kbps, or None for free format
        self.header = header  # the vbr_tags.read() result for a frame holding a VBR header
        self.truncated, self.fill = truncated, fill

    @property
    def samples(self):
        return samples_per_frame(self.version, self.layer)

    @property
    def ticks(self):
        return self.samples * (CLOCK // self.rate)

    @property
    def stream(self):
        return self.version, self.layer, self.rate

    @property
    def stream_key(self):
        """What the analyzer's MpegHeader.IsSameStream compares."""
        return self.version, self.layer, self.rate, self.bitrate is None

    @property
    def is_audio(self):
        return self.header is None and not self.truncated


def reservoir_fills(frames):
    """The reservoir fill each Layer III frame reports (the analyzer's BitReservoir): the free
    bytes after the previous frame's main data, capped at what main_data_begin can reach. frames
    are dicts with layer3, stream, area, main_data_begin and main_data_length; None for others."""
    fills = []
    key, position, previous_end = None, 0, 0
    for frame in frames:
        if not frame["layer3"]:
            key, position, previous_end = None, 0, 0
            fills.append(None)
            continue
        if frame["stream"] != key:
            key, position, previous_end = frame["stream"], 0, 0
        start = position
        cap = 511 if frame["stream"][0] == "MPEG-1" else 255
        fills.append(min(start - previous_end, cap))
        begin = start - frame["main_data_begin"]
        own_end = min(begin + frame["main_data_length"], start + frame["area"])
        previous_end = max(previous_end, own_end)
        position = start + frame["area"]
    return fills


def duration_text(ticks):
    ms = rounded(Fraction(ticks * 1000, CLOCK))
    return f"{ms // 1000}.{ms % 1000:03d} s"


def kbps_tenths_text(bits, ticks):
    tenths = rounded(Fraction(bits * CLOCK * 10, ticks * 1000))
    return f"{tenths // 10}.{tenths % 10} kbps"


def frames_text(count):
    return "1 frame" if count == 1 else f"{count} frames"


def region_fields(frames, all_frames):
    """The Statistics fields of a region, by node path. frames are the region's FrameInfo in order;
    all_frames every frame of the file, for the VBR header's counts across regions."""
    audio = [f for f in frames if f.is_audio]
    if not audio:
        return {}
    ticks = sum(f.ticks for f in audio)
    samples = sum(f.samples for f in audio)
    bits = sum(f.length * 8 for f in audio)
    fields = {
        "Statistics": f"{frames_text(len(audio))}, {duration_text(ticks)}",
        "Statistics/Frames": str(len(audio)),
        "Statistics/Samples": str(samples),
        "Statistics/Duration": duration_text(ticks),
        "Statistics/IsVbr": "true" if len({f.bitrate for f in audio}) > 1 else "false",
        "Statistics/Average Bitrate": kbps_tenths_text(bits, ticks),
    }
    fixed = sorted({f.bitrate for f in audio if f.bitrate is not None})
    if fixed:
        fields["Statistics/Minimum Bitrate"] = f"{fixed[0]} kbps"
        fields["Statistics/Maximum Bitrate"] = f"{fixed[-1]} kbps"
    counts = {}
    for f in audio:
        counts[f.bitrate] = counts.get(f.bitrate, 0) + 1
    fields["Statistics/Bitrate Distribution"] = f"{len(counts)} bitrates" if len(counts) != 1 else "1 bitrate"
    for bitrate in sorted(counts, key=lambda b: (b is None, b or 0)):
        tenths = rounded(Fraction(counts[bitrate] * 1000, len(audio)))
        name = "free" if bitrate is None else f"{bitrate} kbps"
        fields[f"Statistics/Bitrate Distribution/{name}"] = f"{frames_text(counts[bitrate])} ({tenths // 10}.{tenths % 10}%)"

    if all(f.layer == "III" for f in audio):
        fields["Statistics/Maximum Reservoir Fill"] = f"{max(f.fill for f in audio)} bytes"

    counts = lames = 0
    for header_frame in (f for f in frames if f.header is not None):
        described = covered(header_frame, all_frames)
        whole = header_frame is frames[0] and all(f in described for f in audio)
        scope = None if whole else (f"{frames_text(len(described))} after the {header_frame.header['type']} header at "
                                    f"0x{header_frame.offset:X}")
        tag = header_frame.header
        has_counts = tag.get("frames") is not None or tag.get("bytes") is not None
        has_lame = tag.get("lame") is not None
        for path, value in header_fields(header_frame, all_frames).items():
            path = path.replace("/VBR Header", f"/VBR Header[{counts}]" if counts else "/VBR Header")
            path = path.replace("/Gapless", f"/Gapless[{lames}]" if lames else "/Gapless")
            fields[path] = value
        if scope is not None:
            if has_counts:
                fields["Statistics/VBR Header" + (f"[{counts}]" if counts else "")] = scope
            if has_lame:
                fields["Statistics/Gapless" + (f"[{lames}]" if lames else "")] = scope
        counts += has_counts
        lames += has_lame
    return fields


def streams(all_frames):
    """The streams of a file, each with its figures over its complete audio frames."""
    runs = []
    for f in all_frames:
        if not runs or runs[-1][-1].stream_key != f.stream_key:
            runs.append([])
        runs[-1].append(f)
    return [stream_fields(run) for run in runs]


def stream_fields(frames):
    first = frames[0]
    audio = [f for f in frames if f.is_audio]
    ticks = sum(f.ticks for f in audio)
    fields = {
        "codec": f"{first.version} Layer {first.layer}",
        "sample_rate": first.rate,
        "samples": sum(f.samples for f in audio),
        "duration": duration_text(ticks),
    }
    if not audio:
        return fields
    fields["average_bitrate"] = kbps_tenths_text(sum(f.length * 8 for f in audio), ticks)
    fields["is_vbr"] = len({f.bitrate for f in audio}) > 1
    per_second = {}
    time = 0
    for f in audio:
        second = time // CLOCK
        entry = per_second.setdefault(second, [0, 0, 0, 0])
        entry[0] += f.length * 8
        entry[1] += f.ticks
        if f.fill is not None:
            entry[2] += f.fill
            entry[3] += 1
        time += f.ticks
    seconds = range(max(per_second) + 1)
    fields["bitrate_over_time"] = ",".join(
        str(rounded(Fraction(per_second[s][0] * CLOCK, per_second[s][1] * 1000))) if s in per_second else "0" for s in seconds)
    if all(f.layer == "III" for f in audio):
        fields["reservoir_fill_over_time"] = ",".join(
            str(rounded(Fraction(per_second[s][2], per_second[s][3]))) if s in per_second else "0" for s in seconds)
    return fields


def exact(frames):
    """The exact values behind a region's rounded figures, for comparing them with tools that round
    differently at a tie: the average bitrate in kbps, the duration in ms, and each bitrate's share
    of the frames in percent."""
    audio = [f for f in frames if f.is_audio]
    ticks = sum(f.ticks for f in audio)
    counts = {}
    for f in audio:
        counts[f.bitrate] = counts.get(f.bitrate, 0) + 1
    return {
        "kbps": Fraction(sum(f.length * 8 for f in audio) * CLOCK, ticks * 1000),
        "ms": Fraction(ticks * 1000, CLOCK),
        "percent": {b: Fraction(c * 100, len(audio)) for b, c in counts.items()},
        "counts": counts,
    }


def agrees(text, value, unit):
    """Whether a tool's rounded text matches an exact value: equal to it rounded half to even, or,
    where the value lies exactly halfway, rounded the other way."""
    shown = Fraction(text)
    nearest = rounded(value / unit) * unit
    if shown == nearest:
        return True
    return (value / unit).denominator == 2 and abs(shown - value) * 2 == unit


def covered(header_frame, all_frames):
    """The frames a VBR header describes: from its own frame to the end of its stream."""
    start = all_frames.index(header_frame)
    result = []
    for f in all_frames[start + 1 :]:
        if f.stream != header_frame.stream or f.header is not None:
            break
        result.append(f)
    return result


def header_fields(header_frame, all_frames):
    tag = header_frame.header
    frames = covered(header_frame, all_frames)
    fields = {}
    claimed_frames, claimed_bytes = tag.get("frames"), tag.get("bytes")
    if claimed_frames is not None:
        fields["Statistics/VBR Header/Claimed Frames"] = str(claimed_frames)
        fields["Statistics/VBR Header/Counted Frames"] = str(len(frames))
    if claimed_bytes is not None:
        fields["Statistics/VBR Header/Claimed Bytes"] = str(claimed_bytes)
        fields["Statistics/VBR Header/Counted Bytes"] = str(header_frame.length + sum(f.length for f in frames))
    lame = tag.get("lame")
    if lame is not None:
        samples = sum(f.samples for f in frames)
        count = samples - lame["delay"] - lame["padding"]
        fields["Statistics/Gapless/Encoder Delay"] = f"{lame['delay']} samples"
        fields["Statistics/Gapless/Encoder Padding"] = f"{lame['padding']} samples"
        if count >= 0:
            fields["Statistics/Gapless/Sample Count"] = str(count)
            fields["Statistics/Gapless/Duration"] = duration_text(count * (CLOCK // header_frame.rate))
    return fields


def header_findings(header_frame, all_frames):
    """Findings about a VBR header's counts and gapless values: (code, text the message contains)."""
    tag = header_frame.header
    frames = covered(header_frame, all_frames)
    at = f"0x{header_frame.offset:X}"
    result = []
    if tag.get("frames") is not None and tag["frames"] not in (len(frames), len(frames) + 1):
        result.append(("mpeg.vbr-frame-count-mismatch", at))
    counted = header_frame.length + sum(f.length for f in frames)
    if tag.get("bytes") is not None and tag["bytes"] not in (counted, counted - header_frame.length):
        result.append(("mpeg.vbr-byte-count-mismatch", at))
    lame = tag.get("lame")
    if lame is not None and lame["delay"] + lame["padding"] > sum(f.samples for f in frames):
        result.append(("mpeg.gapless-exceeds-stream", at))
    return result
