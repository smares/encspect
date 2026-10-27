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

The coding statistics, also over the complete audio frames, are read from each frame's bytes
(read_frame), as mp3guessenc and EncSpot define them:

- "Channel Modes": the frames of each channel mode, always listed, as the bitrate distribution is
  for a single bitrate. "Joint Stereo Modes" (Layer III) counts the joint stereo frames by their
  stereo coding, "Intensity Stereo Bounds" (Layers I and II) by the subband intensity stereo starts
  at; each share is of the joint stereo frames.
- "Stereo Coding" counts every audio frame by how it codes its channels (STEREO_CODINGS): the joint
  stereo frames by their tools (Layer III: mode extension bit 1 mid/side stereo, bit 0 intensity
  stereo; a joint stereo frame of Layer I or II is intensity stereo above its bound), the others by
  their channel mode: left/right stereo, dual channel, mono. EncSpot's mode 0 is the entries
  left/right stereo, dual channel and mono together.
- Under each bitrate of the distribution, its frames without and with padding and their length,
  mp3guessenc's "size distr".
- Layer III: "Block Usage" counts granules per channel by block type: long (normal), start, short,
  mixed (short with mixed_block_flag), stop and reserved (block type 0 with window switching).
  mp3guessenc's "switch" granules are start, stop and reserved; EncSpot counts those as long.
  "Block Sizes" groups the same granules (BLOCK_SIZES): long with start and stop, short, mixed, and
  the reserved ones apart, which EncSpot counts as long.
  "Global Gain" is the smallest and largest global_gain of each channel; "UsesScfsi" and
  "UsesScalefactorScaling" whether any frame sets scfsi or scalefac_scale; "Scalefactor Scaling"
  the share of granules per channel with scalefac_scale set (EncSpot's "Scalefac", over granules
  times channels times frames); "Average Main Data Begin" and "Maximum Main Data Begin" the mean
  and the largest main_data_begin (EncSpot's "Av. Reservoir", which it truncates to whole bytes,
  and "Max. Reservoir", mp3guessenc's "Max reservoir").
- "Coded Bandwidth" (Layers I and II): the lowest, most common (the lowest of equally common ones)
  and highest, over the frames whose allocation, scfsi and scalefactors fit, of the upper edge of
  the highest subband with an allocation other than 0 in either channel, (subband + 1) * sample
  rate / 64 rounded half to even to whole hertz, 0 where no subband has one.
- "Channel Bit Share" (frames of two channels with a share): the lowest, overall and highest share
  of the first channel in the frame's audio bits, in tenths of a percent rounded half to even;
  overall is the sum of the first channel's bits over the sum of both channels' bits. Layer III:
  part2_3_length of every granule, per channel. Layers I and II: each channel's allocation,
  scfsi, scalefactors and samples, without the allocation and samples above the intensity stereo
  bound, which both channels share (channel_bits, audio_end). Frames without bits have none.
- "Ancillary Data" as mp3guessenc counts it: in Layer III each frame's segment from the end of its
  main data, rounded up to a byte, to where the next frame's main data starts in the bit reservoir
  (ancillary_segments, the analyzer's AncillaryScan), without a truncated frame's; in Layers I and
  II the whole bytes after the audio data of a frame whose audio data fits, without the padding
  slot. "Share" is of the audio
  frames' bytes, "Data Rate" over their duration, and the smallest and largest amount leave out
  frames without any, as mp3guessenc's "Min packet" and "Max packet" do.
- "UsesPadding": whether any frame is padded.
"""

from units import bytes_text
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

    def __init__(self, offset, length, version, layer, rate, bitrate, header=None, truncated=False, fill=None, coding=None, mode=None):
        self.offset, self.length, self.version, self.layer, self.rate = offset, length, version, layer, rate
        self.mode = mode  # the channel mode as the report names it, where the caller knows it
        self.bitrate = bitrate  # kbps, or None for free format
        self.header = header  # the vbr_tags.read() result for a frame holding a VBR header
        self.truncated, self.fill = truncated, fill
        self.coding = coding  # read_frame(), for the coding statistics

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


def bitrate_name(bitrate, audio):
    """The Bitrate Distribution entry's name; a free-format entry carries the average bitrate its frame lengths give, in whole kbps."""
    if bitrate is not None:
        return f"{bitrate} kbps"
    free = [f for f in audio if f.bitrate is None]
    return f"free ({rounded(Fraction(sum(f.length * 8 for f in free) * CLOCK, sum(f.ticks for f in free) * 1000))} kbps)"


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
        name = bitrate_name(bitrate, audio)
        fields[f"Statistics/Bitrate Distribution/{name}"] = f"{frames_text(counts[bitrate])} ({tenths // 10}.{tenths % 10}%)"

    if all(f.layer == "III" for f in audio):
        fields["Statistics/Maximum Reservoir Fill"] = bytes_text(max(f.fill for f in audio))

    if all(f.coding is not None for f in frames):
        fields.update(coding_fields(frames, audio, ticks))

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
    if first.mode is not None:
        fields["channel_layout"] = channel_layout(first.mode)
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


def channel_layout(mode):
    """The speakers of a stream whose first frame has this channel mode: the two programs of dual
    channel feed no speaker positions."""
    return {"Mono": ["front center"], "Dual channel": ["discrete", "discrete"]}.get(mode, ["front left", "front right"])


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


# --- Coding statistics --------------------------------------------------------------------------

BITRATES = {
    ("1", 1): [None, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448],
    ("1", 2): [None, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],
    ("1", 3): [None, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],
    ("2", 1): [None, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256],
    ("2", 2): [None, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
    ("2", 3): [None, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160],
}
RATES = {3: ("MPEG-1", [44100, 48000, 32000]), 2: ("MPEG-2", [22050, 24000, 16000]), 0: ("MPEG-2.5", [11025, 12000, 8000])}
CHANNEL_MODES = ["Stereo", "Joint stereo", "Dual channel", "Mono"]
JOINT_STEREO_MODES = ["Left/right stereo", "Intensity stereo", "Mid/side stereo", "Mid/side and intensity stereo"]
BLOCK_KINDS = ["Long", "Start", "Short", "Mixed", "Stop", "Reserved"]
STEREO_CODINGS = ["Left/right stereo", "Mid/side stereo", "Intensity stereo", "Mid/side and intensity stereo", "Dual channel", "Mono"]
# The block kinds EncSpot's long, short and mixed counts and the report's "Block Sizes" group.
BLOCK_SIZES = {"Long, start and stop": ("Long", "Start", "Stop"), "Short": ("Short",), "Mixed": ("Mixed",), "Reserved": ("Reserved",)}


class BitStream:
    """Bits of bytes, most significant first; reads past the end give None."""

    def __init__(self, data):
        self.value, self.size, self.position = int.from_bytes(data, "big"), len(data) * 8, 0

    def read(self, count):
        self.position += count
        if self.position > self.size:
            return None
        return (self.value >> (self.size - self.position)) & ((1 << count) - 1)


def read_frame(data, offset, available):
    """What the coding statistics need of the frame at offset, of which available bytes are there:
    its header fields, for Layer III the side information when it fits (ISO/IEC 11172-3 2.4.1.7,
    13818-3 2.4.1.7), and for Layers I and II where the audio data ends."""
    raw = int.from_bytes(data[offset : offset + 4], "big")
    version_bits, layer = (raw >> 19) & 3, 4 - ((raw >> 17) & 3)
    version, rates = RATES[version_bits]
    mode = (raw >> 6) & 3
    coding = {
        "version": version, "layer": layer, "protected": not (raw >> 16) & 1, "bitrate_index": (raw >> 12) & 15,
        "rate": rates[(raw >> 10) & 3], "padding": (raw >> 9) & 1, "mode": mode, "mode_ext": (raw >> 4) & 3,
        "channels": 1 if mode == 3 else 2,
    }
    start = offset + 4 + (2 if coding["protected"] else 0)
    if layer == 3:
        mpeg1 = version == "MPEG-1"
        size = (17 if mode == 3 else 32) if mpeg1 else (9 if mode == 3 else 17)
        coding["side_info_end"] = start + size
        if start + size <= offset + available:
            coding["side_info"] = read_side_info(data[start : start + size], mpeg1, coding["channels"])
    else:
        coding["audio_end"] = audio_end(data[offset : offset + available], coding)
    return coding


def read_side_info(data, mpeg1, channels):
    bits = BitStream(data)
    info = {"main_data_begin": bits.read(9 if mpeg1 else 8)}
    bits.read((5 if channels == 1 else 3) if mpeg1 else (1 if channels == 1 else 2))
    info["scfsi"] = [bits.read(4) for _ in range(channels)] if mpeg1 else []
    info["granules"] = []
    for _ in range(2 if mpeg1 else 1):
        for ch in range(channels):
            part2_3, _, gain, _ = bits.read(12), bits.read(9), bits.read(8), bits.read(4 if mpeg1 else 9)
            switched = bits.read(1)
            if switched:
                block_type, mixed = bits.read(2), bits.read(1)
                bits.read(10 + 9)
                kind = {1: "Start", 2: "Mixed" if mixed else "Short", 3: "Stop"}.get(block_type, "Reserved")
            else:
                bits.read(15 + 7)
                kind = "Long"
            if mpeg1:
                bits.read(1)
            scaled = bits.read(1)
            bits.read(1)
            info["granules"].append({"channel": ch, "part2_3_length": part2_3, "global_gain": gain, "block": kind, "scalefac_scale": scaled})
    info["main_data_length"] = (sum(g["part2_3_length"] for g in info["granules"]) + 7) // 8
    return info


def audio_end(frame, coding):
    """Where a Layer I or II frame's samples end, in bits from its start, or None where its
    allocation, scfsi or scalefactors do not fit. Layer I: ISO/IEC 11172-3 2.4.1.5, with the
    forbidden allocation 15 read as 16-bit samples as mpg123 reads it; Layer II: 2.4.1.6 with the
    allocation tables of Annex B (make_layer2_fixtures.py). Where the samples' end can be given,
    coding["figures"] also holds what the allocation says: the highest subband with an allocation in
    either channel (-1 for none) and each channel's own bits (allocation, scfsi, scalefactors and
    samples; the allocation and samples above the intensity stereo bound belong to neither)."""
    import make_layer2_fixtures as layer2

    bits = BitStream(frame)
    bits.read(32 + (16 if coding["protected"] else 0))
    channels = coding["channels"]
    joint = coding["mode"] == 1
    if coding["layer"] == 1:
        bound = (coding["mode_ext"] + 1) * 4 if joint else 32
        allocation = [[0] * 32 for _ in range(channels)]
        for sb in range(32):
            for ch in range(channels if sb < bound else 1):
                allocation[ch][sb] = bits.read(4)
            if sb >= bound:
                for ch in range(1, channels):
                    allocation[ch][sb] = allocation[0][sb]
        if any(a is None for row in allocation for a in row):
            return None
        scalefactors = 6 * sum(1 for row in allocation for a in row if a)
        samples = 12 * sum(allocation[ch][sb] + 1 for sb in range(32) for ch in range(channels if sb < bound else 1) if allocation[ch][sb])
        end = bits.position + scalefactors
        if end > len(frame) * 8:
            return None
        coding["figures"] = figures(allocation, 32, [
            4 * min(bound, 32) + 6 * sum(1 for a in allocation[ch] if a)
            + 12 * sum(allocation[ch][sb] + 1 for sb in range(bound) if allocation[ch][sb]) for ch in range(channels)])
        return end + samples
    version = "1" if coding["version"] == "MPEG-1" else "2"
    bitrate = BITRATES[(version, 2)][coding["bitrate_index"]]
    table = layer2.TABLES[layer2.table_for(version, coding["rate"], bitrate, channels)]
    bound = min((coding["mode_ext"] + 1) * 4, len(table)) if joint else len(table)
    allocation = [[0] * len(table) for _ in range(channels)]
    for sb in range(len(table)):
        for ch in range(channels if sb < bound else 1):
            allocation[ch][sb] = bits.read(layer2.NBAL[len(table[sb])])
        if sb >= bound:
            for ch in range(1, channels):
                allocation[ch][sb] = allocation[0][sb]
    if any(a is None for row in allocation for a in row):
        return None
    scalefactors = 0
    own = [sum(layer2.NBAL[len(table[sb])] for sb in range(min(bound, len(table)))) for _ in range(channels)]
    for sb in range(len(table)):
        for ch in range(channels):
            if allocation[ch][sb]:
                scfsi = bits.read(2)
                if scfsi is None:
                    return None
                scalefactors += 6 * layer2.SCALEFACTORS_PER_SCFSI[scfsi]
                own[ch] += 2 + 6 * layer2.SCALEFACTORS_PER_SCFSI[scfsi]
    samples = 12 * sum(layer2.zero_code(table[sb][allocation[ch][sb]])[0]
                       for sb in range(len(table)) for ch in range(channels if sb < bound else 1) if allocation[ch][sb])
    for ch in range(channels):
        own[ch] += 12 * sum(layer2.zero_code(table[sb][allocation[ch][sb]])[0] for sb in range(bound) if allocation[ch][sb])
    end = bits.position + scalefactors
    if end > len(frame) * 8:
        return None
    coding["figures"] = figures(allocation, len(table), own)
    return end + samples


def figures(allocation, subbands, own):
    """The highest subband with an allocation in either channel, -1 for none, and each channel's bits."""
    highest = max((sb for sb in range(subbands) if any(row[sb] for row in allocation)), default=-1)
    return {"highest": highest, "bits": own}


def ancillary_segments(frames):
    """The ancillary segment of each Layer III frame of a region, in bytes, as mp3guessenc 0.27.6
    scans them (scan_ancillary_III) and the analyzer's AncillaryScan follows it: main data runs
    through the data of consecutive Layer III frames of one stream after their side information; a
    frame's segment runs from the end of its main data to where the next frame's main data starts,
    and the last frame's to the end of its data. It is left out where the next frame's main data
    starts before the stream or inside this frame's, or this frame's main data does not fit. A frame
    holding a VBR header keeps its place in the reservoir without main data or a segment; a frame of
    another layer or stream ends the stream. A truncated frame's segment is left out, as mp3guessenc
    leaves out the frame. frames: FrameInfo with coding, in file order."""
    segments = []
    state = {"key": None, "position": 0, "previous_end": None, "has_previous": False, "truncated": False}

    def segment(length):
        if not state["truncated"]:
            segments.append(length)

    def close():
        if state["has_previous"] and state["previous_end"] is not None:
            segment(state["position"] - state["previous_end"])
        state.update(key=None, position=0, previous_end=None, has_previous=False)

    for f in frames:
        coding = f.coding
        in_reservoir = coding["layer"] == 3 and (f.header is not None or "side_info" in coding)
        key = (f.version, f.layer, f.rate, f.bitrate is None)
        if not in_reservoir or state["key"] != key:
            close()
            if not in_reservoir:
                continue
            state["key"] = key
        start = state["position"]
        state["position"] += f.offset + f.length - coding["side_info_end"]
        if f.header is not None:
            continue
        side = coding["side_info"]
        begin = start - side["main_data_begin"]
        end = begin + side["main_data_length"]
        if state["has_previous"] and state["previous_end"] is not None and begin >= 0 and state["previous_end"] <= begin:
            segment(begin - state["previous_end"])
        state["has_previous"], state["truncated"] = True, f.truncated
        state["previous_end"] = end if begin >= 0 and end <= state["position"] else None
    close()
    return segments


def share(count, whole, unit):
    tenths = rounded(Fraction(count * 1000, whole))
    return f"{count} {unit if count != 1 else unit[:-1]} ({tenths // 10}.{tenths % 10}%)"


def tenths_text(value, unit):
    tenths = rounded(value * 10)
    return f"{tenths // 10}.{tenths % 10} {unit}"


def stereo_code(coding):
    """How a frame codes its channels, as the report's "Stereo Coding" entries (STEREO_CODINGS) number them. A joint stereo
    frame of Layer I or II is intensity stereo above its bound, whatever the bound."""
    if coding["mode"] != 1:
        return {0: 0, 2: 4, 3: 5}[coding["mode"]]
    return 2 if coding["layer"] != 3 else {0: 0, 1: 2, 2: 1, 3: 3}[coding["mode_ext"]]


def channel_bits(coding):
    """The bits of each of a two-channel frame's channels, or None: Layer III's part2_3_length summed per channel, Layers I
    and II's allocation, scfsi, scalefactors and samples that belong to the channel alone."""
    if coding["channels"] != 2:
        return None
    if coding["layer"] == 3:
        side = coding.get("side_info")
        return None if side is None else tuple(sum(g["part2_3_length"] for g in side["granules"] if g["channel"] == ch) for ch in (0, 1))
    figure = coding.get("figures")
    return None if figure is None else tuple(figure["bits"])


def coding_fields(frames, audio, ticks):
    """The coding statistics of a region (see the module docstring), by node path. frames are all
    the region's frames in file order, audio its complete audio frames."""
    fields = {}
    modes = [sum(1 for f in audio if f.coding["mode"] == m) for m in range(4)]
    used = [m for m in range(4) if modes[m]]
    fields["Statistics/Channel Modes"] = f"{len(used)} mode" + ("s" if len(used) != 1 else "")
    for m in used:
        fields[f"Statistics/Channel Modes/{CHANNEL_MODES[m]}"] = share(modes[m], len(audio), "frames")
    fields["Statistics/Stereo Coding"] = frames_text(len(audio))
    for code, name in enumerate(STEREO_CODINGS):
        count = sum(1 for f in audio if stereo_code(f.coding) == code)
        if count:
            fields[f"Statistics/Stereo Coding/{name}"] = share(count, len(audio), "frames")
    for name, layers, label in [("Joint Stereo Modes", (3,), lambda e: JOINT_STEREO_MODES[e]),
                                ("Intensity Stereo Bounds", (1, 2), lambda e: f"Bands {(e + 1) * 4} to 31")]:
        joint = [f for f in audio if f.coding["mode"] == 1 and f.coding["layer"] in layers]
        if joint:
            fields[f"Statistics/{name}"] = frames_text(len(joint))
            for e in range(4):
                count = sum(1 for f in joint if f.coding["mode_ext"] == e)
                if count:
                    fields[f"Statistics/{name}/{label(e)}"] = share(count, len(joint), "frames")

    sizes = {}
    for f in audio:
        key = (f.bitrate, f.coding["padding"], f.length)
        sizes[key] = sizes.get(key, 0) + 1
    for bitrate in {b for b, _, _ in sizes}:
        name = bitrate_name(bitrate, audio)
        for padding, label in [(0, "Unpadded"), (1, "Padded")]:
            lengths = sorted((length, count) for (b, p, length), count in sizes.items() if b == bitrate and p == padding)
            if lengths:
                fields[f"Statistics/Bitrate Distribution/{name}/{label}"] = ", ".join(f"{frames_text(c)} of {l} bytes" for l, c in lengths)

    layer3 = [f.coding["side_info"] for f in audio if f.coding["layer"] == 3]
    if layer3:
        granules = [g for side in layer3 for g in side["granules"]]
        fields["Statistics/Block Usage"] = f"{len(granules)} granule" + ("s" if len(granules) != 1 else "")
        for kind in BLOCK_KINDS:
            count = sum(1 for g in granules if g["block"] == kind)
            if count:
                fields[f"Statistics/Block Usage/{kind}"] = share(count, len(granules), "granules")
        fields["Statistics/Block Sizes"] = fields["Statistics/Block Usage"]
        for name, kinds in BLOCK_SIZES.items():
            count = sum(1 for g in granules if g["block"] in kinds)
            if count:
                fields[f"Statistics/Block Sizes/{name}"] = share(count, len(granules), "granules")
        for ch in sorted({g["channel"] for g in granules}):
            gains = [g["global_gain"] for g in granules if g["channel"] == ch]
            fields[f"Statistics/Global Gain/Channel {ch}/Minimum"] = str(min(gains))
            fields[f"Statistics/Global Gain/Channel {ch}/Maximum"] = str(max(gains))

    bandwidths = [rounded(Fraction((f.coding["figures"]["highest"] + 1) * f.rate, 64)) if f.coding["figures"]["highest"] >= 0 else 0
                  for f in audio if f.coding["layer"] != 3 and "figures" in f.coding]
    if bandwidths:
        fields["Statistics/Coded Bandwidth/Lowest"] = f"{min(bandwidths)} Hz"
        fields["Statistics/Coded Bandwidth/Most Common"] = f"{min(b for b in set(bandwidths) if bandwidths.count(b) == max(map(bandwidths.count, set(bandwidths))))} Hz"
        fields["Statistics/Coded Bandwidth/Highest"] = f"{max(bandwidths)} Hz"
    shares = [bits for bits in (channel_bits(f.coding) for f in audio) if bits and sum(bits) > 0]
    if shares:
        tenths = [rounded(Fraction(first * 1000, first + second)) for first, second in shares]
        overall = rounded(Fraction(sum(first for first, _ in shares) * 1000, sum(first + second for first, second in shares)))
        for name, value in [("Lowest", min(tenths)), ("Overall", overall), ("Highest", max(tenths))]:
            fields[f"Statistics/Channel Bit Share/{name}"] = f"{value // 10}.{value % 10} %"

    segments = ancillary_segments(frames)
    pieces = [s for s in segments if s > 0]
    for f in audio:
        if f.coding["layer"] != 3 and f.coding["audio_end"] is not None and f.coding["audio_end"] <= f.length * 8:
            amount = (f.length * 8 - f.coding["audio_end"]) // 8 - f.coding["padding"] * (4 if f.coding["layer"] == 1 else 1)
            if amount > 0:
                pieces.append(amount)
    total = sum(pieces)
    fields["Statistics/Ancillary Data"] = bytes_text(total)
    fields["Statistics/Ancillary Data/Share"] = tenths_text(Fraction(total * 100, sum(f.length for f in audio)), "%")
    fields["Statistics/Ancillary Data/Data Rate"] = kbps_tenths_text(total * 8, ticks)
    if total:
        fields["Statistics/Ancillary Data/Smallest Nonzero Amount"] = bytes_text(min(pieces))
        fields["Statistics/Ancillary Data/Largest Amount"] = bytes_text(max(pieces))

    fields["Statistics/UsesPadding"] = "true" if any(f.coding["padding"] for f in audio) else "false"
    if layer3:
        fields["Statistics/UsesScfsi"] = "true" if any(any(side["scfsi"]) for side in layer3) else "false"
        scaled = sum(g["scalefac_scale"] for g in granules)
        fields["Statistics/UsesScalefactorScaling"] = "true" if scaled else "false"
        fields["Statistics/Scalefactor Scaling"] = tenths_text(Fraction(scaled * 100, len(granules)), "%")
        begins = [side["main_data_begin"] for side in layer3]
        fields["Statistics/Average Main Data Begin"] = tenths_text(Fraction(sum(begins), len(begins)), "bytes")
        fields["Statistics/Maximum Main Data Begin"] = bytes_text(max(begins))
    return fields
