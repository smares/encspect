#!/usr/bin/env python3
"""Writes the hand-built Windows Media Audio Voice fixtures into tests/fixtures/asf, bit by bit from voice_model.py: valid streams
of every frame type (10 and 16 LSPs, residual and per-frame LSPs, a pitch per frame and per block, silence, sample counts and
additional data, packets with one and two blocks) and damaged ones (a configuration FFmpeg's decoder rejects, a frame type table
with codes that reach no type, truncated data, spillover that does not agree with the superframes, music and invalid superframes,
a packet header that runs past the packet). Microsoft has published no specification of the codec; the writer follows FFmpeg 6.1.1's
wmavoice.c, and crosscheck_asf_fixtures.py has FFmpeg decode every file: the valid ones to the samples they hold, the damaged ones to
the sample count and the errors the analysis predicts.

Each fixture has a manifest (voice-*.expected.json) in the form of the other hand-built ASF fixtures (the objects, streams, packets and
media objects the container model reads) and, under "voice", what the writer put in: the frame types of each superframe and the
samples a decoder outputs. Run with no arguments to rewrite them; CI does and fails if they differ from the committed files.
"""

import json
import pathlib
import sys

import asf_model as m
import voice_model as v

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "asf"
FILE_ID = bytes(range(1, 17))

FIXTURES = []


def finding(code, contains):
    return {"code": code, "contains": contains}


def fixture(name, description, builder, findings=(), crosscheck=None):
    FIXTURES.append((name, description, builder, list(findings), crosscheck or {"ffprobe": True, "decode": "ok"}))


def all_types(count, seed, avoid=()):
    """count superframes of three frame types each, covering every type (the others in a shuffled order)."""
    import random
    rng = random.Random(seed)
    pool = [t for t in range(17) if t not in avoid]
    types = []
    while len(types) < count * 3:
        order = pool[:]
        rng.shuffle(order)
        types += order
    return [types[i:i + 3] for i in range(0, count * 3, 3)]


class Stream:
    """A written stream: the superframes, their packets and what the writer knows of each."""

    def __init__(self, config, plan, seed, samples=None, additional=None, pad=None, orphan=0):
        """plan: the frame types of each superframe. samples: {index: sample count}. additional: {index: groups of 10 bits}. pad:
        {index: zero bits to put after the superframe}, which a decoder ignores where the superframe is the one a packet ends in.
        orphan: bits at the start of the first packet that finish a superframe nobody began."""
        self.config = config
        writer = v.Writer(config, seed)
        self.superframes = []
        self.info = []
        for index, types in enumerate(plan):
            bits, info = writer.superframe(types, (samples or {}).get(index), (additional or {}).get(index, 0))
            if pad and index in pad:
                bits.write(0, pad[index])
            self.superframes.append(bits)
            self.info.append(info)
        self.orphan = orphan
        self.blocks, self.details = packetize(config, self.superframes, orphan, seed)

    def decoded_samples(self):
        return sum(i["samples"] for i in self.info)


def packetize(config, superframes, orphan, seed, pending=None, sequence=0):
    import random
    junk = v.BitWriter().random(random.Random(seed ^ 0xBEEF), orphan)
    capacity = config.align * 8
    packets = []
    details = []
    pending = junk if pending is None else pending
    index = 0
    while index < len(superframes) or len(pending):
        spill = len(pending)
        room = capacity - (5 + 6 + config.spillover_bits) - spill
        body = v.BitWriter().append(pending)
        pending = v.BitWriter()
        count = 0
        cut = None
        tail = 0
        while index < len(superframes) and room > 0:
            sf = superframes[index]
            count += 1
            if len(sf) <= room:
                body.append(sf)
                room -= len(sf)
            else:
                body.append(sf, 0, room)
                pending = v.BitWriter().append(sf, room)
                cut = index
                tail = room
                room = 0
            index += 1
        header = v.BitWriter().write(sequence & 15, 4).write(1 if config.residual else 0, 1).write(count, 6).write(spill, config.spillover_bits)
        packets.append(header.append(body).to_bytes(config.align))
        details.append({"spill": spill, "count": count, "cut": cut, "first": index - count, "tail": tail if cut is not None else None})
        sequence += 1
    return packets, details


class FlaggedStream(Stream):
    """A stream whose packets set the residual LSPs flag as the list says (one entry per packet, repeated). A superframe that lies in a
    packet is written with the packet's flag, one that goes on in the next packet with the next one's, which is the flag a decoder
    reads it with."""

    def __init__(self, config, plan, flags, seed, samples=None, additional=None):
        import copy
        self.config = config
        writer = v.Writer(config, seed)
        capacity = config.align * 8
        self.superframes, self.info, self.blocks, self.details = [], [], [], []
        pending = v.BitWriter()
        index = 0
        while index < len(plan) or len(pending):
            flag = flags[len(self.blocks) % len(flags)]
            following = flags[(len(self.blocks) + 1) % len(flags)]
            spill = len(pending)
            room = capacity - (5 + 6 + config.spillover_bits) - spill
            body = v.BitWriter().append(pending)
            pending = v.BitWriter()
            count = 0
            cut = None
            while index < len(plan) and room > 0:
                count += 1
                args = (plan[index], (samples or {}).get(index), (additional or {}).get(index, 0))
                trial = copy.deepcopy(writer)
                bits, info = trial.superframe(*args, residual=flag)
                last = False
                if len(bits) <= room:
                    writer = trial
                else:
                    bits, info = writer.superframe(*args, residual=following)
                    last = True
                self.superframes.append(bits)
                self.info.append(info)
                if len(bits) <= room:
                    body.append(bits)
                    room -= len(bits)
                    assert room > 0 or following == flag or index == len(plan) - 1
                else:
                    body.append(bits, 0, room)
                    pending = v.BitWriter().append(bits, room)
                    cut = index
                    room = 0
                index += 1
                if last:
                    break
            header = v.BitWriter().write(len(self.blocks) & 15, 4).write(1 if flag else 0, 1).write(count, 6).write(spill, config.spillover_bits)
            self.blocks.append(header.append(body).to_bytes(config.align))
            self.details.append({"spill": spill, "count": count, "cut": cut, "first": index - count, "residual": flag})
        self.orphan = 0


def asf(config, blocks, per_object=1, seconds=None, tail=0, rate=None, align=None, extra=None):
    """The ASF file of a stream's blocks: each media object (per_object blocks) is the payload of a packet, in the layout of
    Windows Media's audio spread error correction with one chunk. rate, align and extra replace the WAVEFORMATEX's fields."""
    objects = [b"".join(blocks[i:i + per_object]) for i in range(0, len(blocks), per_object)]
    if tail:
        objects[-1] += bytes(range(1, tail + 1))
    seconds = seconds if seconds is not None else len(blocks) * config.align * 8 / 6000
    return asf_objects(config, objects, seconds, per_object, rate, align, extra)


def asf_objects(config, objects, seconds, per_object=1, rate=None, align=None, extra=None):
    """The ASF file of media objects given as they are: each is the payload of a packet."""
    size = max(len(o) for o in objects) + 48
    chunk = config.align * per_object
    stream = m.stream_properties(1, config.wave_format(rate, align, extra), ec_type=m.AUDIO_SPREAD, ec_data=m.spread_audio(1, chunk, chunk, b"\0"))
    per_object_seconds = seconds / len(objects)
    packets = []
    for i, o in enumerate(objects):
        time = int(i * per_object_seconds * 1000)
        packets.append(m.packet([m.Payload(1, i % 255 + 1, o, time=time)], size, m.Layout(), send_time=time))
    return m.asf_file([stream], packets, size, file_id=FILE_ID, play=int(per_object_seconds * len(objects) * 10_000_000))


def voice_info(stream):
    def frame(f):
        # What the analysis reports of a frame: its type, the pitch of a pitch per frame (the frame's) and of a pitch per block (the last block's),
        # the silence gain index or the window's index, and its bits (with its LSPs where each frame has its own).
        return {key: f[key] for key in ("type", "bits", "pitch", "parameter", "window_index") if key in f}

    return {"superframes": [{"types": i["types"], "samples": i["samples"], "bits": i["bits"], "additional": i["additional"], "frames": [frame(f) for f in i["frames"]]} for i in stream.info],
            "decoded_samples": stream.decoded_samples()}


# ---- valid streams ----

def eight_khz(seed=7, **kw):
    config = v.Config(8000, 120, denoise=5, dc_level=9)
    return Stream(config, all_types(24, seed), seed, samples={10: 200, 23: 300}, additional={3: 2, 8: 1, 20: 4}, **kw)


def sixteen_khz(seed=11):
    config = v.Config(16000, 300, groups=[0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5], lsp16=True, q_mode=1, default_mode=1, residual=False, denoise=11, tilt=True, dc_level=0)
    return Stream(config, all_types(20, seed), seed, samples={19: 419})


def seconds_of(stream):
    return stream.decoded_samples() / stream.config.rate


def eleven_khz(seed=5):
    config = v.Config(11025, 100, groups=[3, 3, 3, 4, 4, 4, 5, 5, 5, 2, 2, 2, 1, 1, 1, 0, 0], postfilter=False, denoise=0, dc_level=15)
    return Stream(config, all_types(18, seed), seed)


class Built:
    """A fixture's file and what the writer knows of it."""

    def __init__(self, data, voice):
        self.data = data
        self.voice = voice


_BUILT = {}


def built(name, make):
    if name not in _BUILT:
        _BUILT[name] = make()
    return _BUILT[name]


def valid(name, description, make_stream, per_object=1, findings=(), tail=0, crosscheck=None):
    def build():
        stream = make_stream()
        return Built(asf(stream.config, stream.blocks, per_object, stream.decoded_samples() / stream.config.rate, tail), voice_info(stream))
    fixture(name, description, lambda: built(name, build).data, findings, crosscheck)
    VOICE[name] = lambda: built(name, build).voice


VOICE = {}

valid("voice-8k-residual", "Mono at 8 kHz with 10 LSPs sent once for the three frames of each superframe, the postfilter and a DC level of 9, all 17 frame types, sample counts of 200 and 300, additional data in three superframes, one block per media object.", eight_khz)
valid("voice-16k-independent", "Mono at 16 kHz with 16 LSPs in every frame, the second quantizer and default modes, a denoise strength of 11 with tilt correction, a frame type table that gives the types groups 0 to 5, and a last superframe of 419 samples.", sixteen_khz)
valid("voice-11k-two-blocks", "Mono at 11.025 kHz with two blocks of 100 bytes in each media object, as the ASF packets of Windows Media Encoder's low bitrate files hold them, a frame type table of reversed groups and no postfilter.", eleven_khz, per_object=2)
def object_not_whole():
    stream = eleven_khz()
    return asf(stream.config, stream.blocks, 2, seconds_of(stream), tail=10)


fixture("voice-object-not-whole", "Voice blocks of 100 bytes, two to a media object, the last media object 10 bytes longer than a whole number of blocks. A decoder cuts blocks from the end of the object, so it reads the first 10 bytes as the rest of a packet and the blocks 10 bytes late, where the superframes no longer fit.",
        object_not_whole,
        [finding("wma.packet-long", "10 bytes before its whole blocks"), finding("wma.voice-packet-sequence", "follow the packet before"), finding("wma.voice-spillover-orphan", "no start is left"),
         finding("wma.voice-frame-type", "gives no type")],
        {"ffprobe": True, "decode": "errors", "messages": ["Invalid frame type VLC code"], "ffmpeg": {"samples": 7680, "superframes": 16}})


def flag_changes():
    config = v.Config(8000, 120, denoise=5, dc_level=9)
    return FlaggedStream(config, all_types(24, 13), [True, False, False, True, False, True], 13, samples={23: 300})


valid("voice-lsp-flag-changes", "Mono at 8 kHz whose packets set the residual LSPs flag in a pattern (set, clear, clear, set, clear, set), so that the superframes that go on from one packet into the next are written with the flag of the later packet, which a decoder reads them with.",
      flag_changes, findings=[finding("wma.voice-lsp-flag-changes", "differently from the packet before")])


# ---- a configuration FFmpeg's decoder rejects, or that reaches no type ----

def damaged_config(name, description, config_args, code, contains, decode_message, rate=None, size=None, align=None, extra=b""):
    def build():
        stream = Stream(v.Config(8000, 120, denoise=5, dc_level=9), all_types(6, 7), 7)
        config = v.Config(8000, 120, **config_args)
        data = (config.codec_data()[:size] if size else config.codec_data()) + extra
        return Built(asf(stream.config, stream.blocks, 1, stream.decoded_samples() / 8000, rate=rate, align=align, extra=data), None)
    fixture(name, description, lambda: built(name, build).data,
            [finding(code, contains), finding("wma.voice-not-read", "rejects the stream's configuration")],
            {"ffprobe": False, "reason": "ffprobe opens the decoder to read the stream and fails with the decoder's message.", "decode": "error", "messages": [decode_message]})


damaged_config("voice-config-denoise", "A denoise strength of 12 in the flags, one more than FFmpeg's decoder accepts.",
               {"denoise": 12, "dc_level": 9}, "wma.voice-denoise-strength", "denoise strength of 12", "Invalid denoise filter strength 12")
damaged_config("voice-config-table", "A frame type table that puts five frame types in the first group of codes, which FFmpeg's decoder rejects.",
               {"groups": [0, 0, 0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4], "denoise": 5, "dc_level": 9}, "wma.voice-frame-type-table", "type 4 in group 0", "Invalid VBM tree")
damaged_config("voice-config-size", "Codec-specific data of 45 bytes, one fewer than the 46 FFmpeg's decoder needs.",
               {"denoise": 5, "dc_level": 9}, "wma.codec-data-size", "is 45 bytes", "Invalid extradata size 45", size=45)
damaged_config("voice-config-extra", "Codec-specific data of 50 bytes, four more than the 46 FFmpeg's decoder needs.",
               {"denoise": 5, "dc_level": 9}, "wma.codec-data-size", "is 50 bytes", "Invalid extradata size 50", extra=bytes([1, 2, 3, 4]))
damaged_config("voice-config-rate", "A sample rate of 100 Hz, below the 322 Hz at which FFmpeg's decoder can give a pitch a whole sample.",
               {"denoise": 5, "dc_level": 9}, "wma.voice-sample-rate", "100 Hz", "Unsupported samplerate 100", rate=100)
damaged_config("voice-config-rate-low", "A sample rate of 7,500 Hz, above 322 Hz but below the 7,990 Hz from which FFmpeg's decoder can code the pitch of a block as a step from the one before.",
               {"denoise": 5, "dc_level": 9}, "wma.voice-sample-rate", "7500 Hz", "Invalid delta pitch hrange", rate=7500)
damaged_config("voice-config-align", "A block alignment of 0 in the stream's WAVEFORMATEX, which FFmpeg's decoder rejects.",
               {"denoise": 5, "dc_level": 9}, "wma.voice-block-alignment", "block alignment is 0", "Invalid block alignment 0", align=0)


def table_stream(name, description, groups, avoid, findings):
    def build():
        config = v.Config(8000, 120, groups=groups, denoise=5, dc_level=9)
        stream = Stream(config, all_types(12, 3, avoid=avoid), 3)
        return Built(asf(config, stream.blocks, 1, stream.decoded_samples() / 8000), voice_info(stream))
    fixture(name, description, lambda: built(name, build).data, findings)
    VOICE[name] = lambda: built(name, build).voice


table_stream("voice-config-unreachable", "A frame type table that gives type 15 the slot 21 and type 16 the slot 22, which no code reaches; the stream uses types 0 to 15.",
             [0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 7, 7], [16], [finding("wma.voice-frame-type-unreachable", "type 16")])
table_stream("voice-config-overlap", "A frame type table that puts four types in group 0, so that the fourth takes the first slot of group 1, which type 4 then overwrites; the stream uses the other 16 types.",
             [0, 0, 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5], [3],
             [finding("wma.voice-frame-type-overlap", "four frame types in group 0"), finding("wma.voice-frame-type-unreachable", "type 3")])


# ---- damaged streams ----

def truncated():
    stream = eight_khz()
    data = asf(stream.config, stream.blocks, 1, stream.decoded_samples() / 8000)
    return data[:-90]


fixture("voice-truncated", "The 8 kHz stream cut in the middle of its last packet: the container drops the incomplete media object, so the superframe that began in the packet before is never finished.",
        truncated,
        [finding("asf.object-truncated", "the file is cut short"), finding("asf.file-size", "but the file holds"), finding("asf.object-incomplete", "ends before all of its bytes arrive"),
         finding("asf.packet-truncated", "ends before its header or a payload does"), finding("asf.payload-overrun", "extends past the packet's end"), finding("wma.voice-unfinished", "no packet follows")],
        {"ffprobe": False, "reason": "the file is cut inside a payload, which ffmpeg returns as a short packet.", "decode": "errors", "messages": [], "ffmpeg": {"samples": 10760}})


def spillover():
    config = v.Config(8000, 100, denoise=5, dc_level=9)
    plan = all_types(18, 21)
    # The first packet starts with the end of a superframe nobody began; the superframe that the 4th packet ends in is given 16 bits
    # of zeros, which a decoder ignores; the spillover of the next to last packet is 24 bits short of what the superframe needs.
    probe = Stream(config, plan, 21, orphan=9)
    pad = {probe.details[3]["cut"]: 16}
    stream = Stream(config, plan, 21, pad=pad, orphan=9)
    blocks = list(stream.blocks)
    last = len(blocks) - 2
    spill = stream.details[last]["spill"]
    assert spill > 24 and stream.details[last]["count"] > 1
    blocks[last] = v.set_bits(blocks[last], 5 + 6, config.spillover_bits, spill - 24)
    return asf(config, blocks, 1, stream.decoded_samples() / 8000)


fixture("voice-spillover", "Spillover that does not agree with the superframes: bits at the start of the first packet that finish a superframe nobody began, a superframe that ends 16 bits before the span its packets give it, and a spillover 24 bits short of what the superframe needs, after which the rest of the packet is read 24 bits early (and, as FFmpeg skips, 2 bits more) and a superframe reads as music.",
        spillover,
        [finding("wma.voice-spillover-orphan", "spillover bits (9)"), finding("wma.voice-superframe-padding", "16 bits"), finding("wma.voice-superframe-overrun", "more bits than the packets give"),
         finding("wma.voice-music", "first bit is clear"), finding("wma.voice-spillover-after-music", "lost to a superframe of music")],
        {"ffprobe": True, "decode": "errors", "messages": ["WMAPro-in-WMAVoice"], "ffmpeg": {"samples": 7200, "superframes": 15}})


def invalid_superframes():
    config = v.Config(8000, 100, denoise=5, dc_level=9)
    stream = Stream(config, all_types(26, 33, avoid=(0,)), 33)
    blocks = list(stream.blocks)
    # Superframes that begin in the packet and are read there: the first of the 2nd packet gets a clear speech bit (music), the first
    # of the 4th a sample count of 4000, the first of the 6th a frame type code (14 ones) that gives no type; the 9th packet's
    # sequence number is changed.
    def first_in_packet(packet):
        d = stream.details[packet]
        position = 5 + 6 + config.spillover_bits + d["spill"]
        return position
    blocks[1] = v.set_bits(blocks[1], first_in_packet(1), 1, 0)
    position = first_in_packet(3)
    blocks[3] = v.set_bits(v.set_bits(blocks[3], position + 1, 1, 1), position + 2, 12, 4000)
    # The first frame's code of the first superframe of the 6th packet, after the speech bit, the sample count flag and the 48 bits of LSPs.
    blocks[5] = v.set_bits(blocks[5], first_in_packet(5) + 2 + config.residual_lsp_bits, 14, 0x3FFF)
    blocks[8] = v.set_bits(blocks[8], 0, 4, (8 + 5) & 15)
    return asf(config, blocks, 1, stream.decoded_samples() / 8000)


fixture("voice-invalid-superframes", "Superframes a decoder rejects: a clear speech bit (music, which FFmpeg's decoder does not support), a sample count of 4000, a frame type code that gives no type, and a packet whose sequence number does not follow the one before; each ends its packet, so the next packet's spillover has no start to finish.",
        invalid_superframes,
        [finding("wma.voice-music", "first bit is clear"), finding("wma.voice-sample-count", "more samples than the 480"), finding("wma.voice-frame-type", "gives no type"),
         finding("wma.voice-packet-sequence", "follow the packet before"), finding("wma.voice-spillover-orphan", "spillover bits"),
         finding("wma.voice-spillover-after-music", "lost to a superframe of music"), finding("wma.voice-superframe-padding", "ends before the bits the packets give it")],
        {"ffprobe": True, "decode": "errors", "messages": ["WMAPro-in-WMAVoice", "Superframe encodes > 480 samples", "Invalid frame type VLC code"], "ffmpeg": {"samples": 10080, "superframes": 21}})


def header_invalid():
    config = v.Config(8000, 2, denoise=5, dc_level=9)
    return asf(config, [bytes([0x07, 0xE0])] * 3, 1, 0.5)


fixture("voice-packet-header", "Blocks of 2 bytes whose header counts superframes in a 6-bit value of 63 with no room for the value that must follow.",
        header_invalid,
        [finding("wma.voice-packet-header", "run past the packet"), finding("wma.voice-packet-sequence", "follow the packet before")],
        {"ffprobe": True, "decode": "errors", "messages": []})


# ---- media objects that are not one block per packet ----

def junk(length, seed):
    import random
    return bytes(random.Random(seed).getrandbits(8) for _ in range(length))


def eleven_khz_objects(per_object, seed=5):
    stream = eleven_khz(seed)
    return stream, [b"".join(stream.blocks[i:i + per_object]) for i in range(0, len(stream.blocks), per_object)]


def object_dropped():
    # A superframe that lies in the second block of a media object gets a clear speech bit (music): a decoder drops what is left of the
    # media object it was handed, here that block, and the packet after the object has spillover bits with no start to finish.
    stream = eleven_khz()
    packet = next(i for i in range(3, len(stream.details), 2) if stream.details[i]["count"] >= 2)
    blocks = list(stream.blocks)
    blocks[packet] = v.set_bits(blocks[packet], 5 + 6 + stream.config.spillover_bits + stream.details[packet]["spill"], 1, 0)
    return asf(stream.config, blocks, 2, seconds_of(stream))


fixture("voice-object-dropped", "Voice blocks of 100 bytes, two to a media object; a superframe in the second block of an object has a clear speech bit (music). A decoder drops the rest of the media object, which is the rest of that block, and the next packet's spillover has no start to finish.",
        object_dropped,
        [finding("wma.voice-music", "the rest of its media object"), finding("wma.voice-spillover-after-music", "lost to a superframe of music")],
        {"ffprobe": True, "decode": "errors", "messages": ["WMAPro-in-WMAVoice"], "ffmpeg": {"samples": 7680, "superframes": 16}})


def object_dropped_blocks():
    # The same with the media object of three blocks: the error is in the first, and the two after it are dropped as well.
    stream = eleven_khz()
    packet = next(i for i in range(3, len(stream.details) - 2, 3) if stream.details[i]["count"] >= 2)
    blocks = list(stream.blocks)
    blocks[packet] = v.set_bits(blocks[packet], 5 + 6 + stream.config.spillover_bits + stream.details[packet]["spill"], 1, 0)
    return asf(stream.config, blocks, 3, seconds_of(stream))


fixture("voice-object-dropped-blocks", "Voice blocks of 100 bytes, three to a media object; a superframe in the first block of an object has a clear speech bit (music), and a decoder drops the two blocks after it with it.",
        object_dropped_blocks,
        [finding("wma.voice-music", "the rest of its media object"), finding("wma.voice-packet-sequence", "follow the packet before"), finding("wma.voice-spillover-after-music", "lost to a superframe of music")],
        {"ffprobe": True, "decode": "errors", "messages": ["WMAPro-in-WMAVoice"], "ffmpeg": {"samples": 6240, "superframes": 13}})


def object_short():
    # A media object of 50 bytes between two packets, the first of which ends in the start of a superframe. A decoder takes the 50 bytes for
    # the rest of a packet with no header, which drops the start it was keeping, so the spillover of the next packet has no start to finish.
    stream = eleven_khz()
    packet = next(i for i in range(2, len(stream.details) - 2) if stream.details[i]["cut"] is not None)
    objects = list(stream.blocks)
    objects.insert(packet + 1, junk(50, 3))
    return asf_objects(stream.config, objects, seconds_of(stream))


fixture("voice-object-short", "Voice blocks of 100 bytes, one to a media object, with a media object of 50 bytes after a packet that ends in the start of a superframe: a decoder takes the 50 bytes for the rest of a packet and loses the start it kept, so the next packet's spillover finishes nothing.",
        object_short,
        [finding("wma.packet-short", "fewer bytes than the block alignment"), finding("wma.voice-spillover-orphan", "no start is left")],
        {"ffprobe": True, "decode": "clean", "messages": [], "ffmpeg": {"samples": 8160, "superframes": 17}})


def object_remainder_first():
    # 10 bytes before the whole blocks of a media object: a decoder cuts blocks from the end, so it reads them as the rest of a packet,
    # which drops the start kept from the packet before, and finds the blocks aligned.
    stream, objects = eleven_khz_objects(2)
    index = next(i for i in range(1, len(objects)) if stream.details[2 * i - 1]["cut"] is not None)
    objects[index] = junk(10, 4) + objects[index]
    return asf_objects(stream.config, objects, seconds_of(stream), 2)


fixture("voice-object-remainder-first", "Voice blocks of 100 bytes, two to a media object, with 10 bytes in front of the blocks of one media object, after a packet that ends in the start of a superframe: a decoder cuts blocks from the end of the object, so it reads the 10 bytes as the rest of a packet, loses the start it kept, and reads the blocks aligned.",
        object_remainder_first,
        [finding("wma.packet-long", "10 bytes before its whole blocks"), finding("wma.voice-spillover-orphan", "no start is left")],
        {"ffprobe": True, "decode": "clean", "messages": [], "ffmpeg": {"samples": 8160, "superframes": 17}})


def headerless_start():
    # A packet that counts no superframe, then a media object of 10 bytes and a block: after a packet that counted none a decoder keeps the 10
    # bytes as the start of a superframe, and the spillover of the block after them finishes it.
    config = v.Config(11025, 100, groups=[3, 3, 3, 4, 4, 4, 5, 5, 5, 2, 2, 2, 1, 1, 1, 0, 0], postfilter=False, denoise=0, dc_level=15)
    plan = all_types(10, 17)
    writer = v.Writer(config, 17)
    first, _ = writer.superframe(plan[0])
    lead = 80
    rest = [writer.superframe(types)[0] for types in plan[1:]]
    blocks, details = packetize(config, rest, 0, 17, pending=v.BitWriter().append(first, lead), sequence=1)
    empty = v.BitWriter().write(0, 4).write(1, 1).write(0, 6).write(0, config.spillover_bits).to_bytes(config.align)
    objects = [empty, first_bytes(first, lead) + blocks[0]] + blocks[1:]
    return asf_objects(config, objects, (len(plan) * 480) / config.rate)


def first_bytes(bits, count):
    return v.BitWriter().append(bits, 0, count).to_bytes((count + 7) // 8)


fixture("voice-object-headerless-start", "Voice blocks of 100 bytes: a packet that counts no superframe, then a media object of 10 bytes in front of a block. After a packet that counted none, a decoder keeps the 10 bytes as the start of a superframe, and the spillover of the block after them finishes it.",
        headerless_start,
        [finding("wma.packet-long", "10 bytes before its whole blocks")],
        {"ffprobe": True, "decode": "clean", "messages": [], "ffmpeg": {"samples": 4800, "superframes": 10}})


def exact_setup(seed=24, align=120):
    """The first two superframes of a stream and a first block that they fill to its last bit after the header of 21 bits; the third
    superframe is 560 bits, 70 bytes."""
    config = v.Config(8000, align, denoise=5, dc_level=9)
    writer = v.Writer(config, seed)
    built = [writer.superframe(types) for types in all_types(6, seed)]
    assert [len(b) for b, _ in built[:3]] == [386, 553, 560]

    def first_block(count):
        header = v.BitWriter().write(0, 4).write(1, 1).write(count, 6).write(0, config.spillover_bits)
        return header.append(built[0][0]).append(built[1][0]).to_bytes(config.align)

    return config, built, first_block


class Assembled(Stream):
    """A stream put together from superframes and blocks that are given."""

    def __init__(self, config, built, blocks):
        self.config = config
        self.superframes = [b for b, _ in built]
        self.info = [i for _, i in built]
        self.blocks = blocks
        self.details = []
        self.orphan = 0


def exact_fill():
    config, built, first_block = exact_setup()
    rest, _ = packetize(config, [b for b, _ in built[2:]], 0, 24, sequence=1)
    return Assembled(config, built, [first_block(3)] + rest)


valid("voice-superframe-exact-fill", "Mono at 8 kHz in blocks of 120 bytes; the header of the first packet counts three superframes, and the two that it holds fill it to its last bit. A decoder goes on with the next packet without looking for the third, and the analysis reports no superframe running past the packet.",
      exact_fill)


def lead_superframe():
    # The first packet counts four superframes and holds two, which fill it. A media object of 70 bytes follows: a decoder takes it for the rest
    # of that packet and reads the third counted superframe from it, which is exactly the 560 bits of the third superframe.
    config, built, first_block = exact_setup()
    rest, _ = packetize(config, [b for b, _ in built[3:]], 0, 24, sequence=1)
    return asf_objects(config, [first_block(4), first_bytes(built[2][0], 560), *rest], sum(i["samples"] for _, i in built) / config.rate)


fixture("voice-object-superframe-in-lead", "Blocks of 120 bytes: the first packet counts four superframes and holds two, which fill it, and a media object of 70 bytes follows that holds the third superframe. A decoder takes the 70 bytes for the rest of the first packet and reads the third counted superframe from them.",
        lead_superframe,
        [finding("wma.packet-short", "fewer bytes than the block alignment")],
        {"ffprobe": True, "decode": "clean", "messages": [], "ffmpeg": {"samples": 2880, "superframes": 6}})


def lead_start():
    # The first packet counts three superframes and holds two, which fill it. In front of the next block are the first 80 bits of the third
    # superframe: a decoder takes them for the rest of the first packet, where the third counted superframe begins, and the spillover of
    # the block finishes it.
    config, built, first_block = exact_setup()
    third = built[2][0]
    rest, _ = packetize(config, [b for b, _ in built[3:]], 0, 24, pending=v.BitWriter().append(third, 80), sequence=1)
    return asf_objects(config, [first_block(3), first_bytes(third, 80) + rest[0], *rest[1:]], sum(i["samples"] for _, i in built) / config.rate)


fixture("voice-object-start-in-lead", "Blocks of 120 bytes: the first packet counts three superframes and holds two, which fill it, and 10 bytes in front of the next block hold the first bits of the third. A decoder takes the 10 bytes for the rest of the first packet, where the third counted superframe begins, and the spillover of the block finishes it.",
        lead_start,
        [finding("wma.packet-long", "10 bytes before its whole blocks")],
        {"ffprobe": True, "decode": "clean", "messages": [], "ffmpeg": {"samples": 2880, "superframes": 6}})


def skip_kept():
    # A packet ends in the start of the first superframe. The next media object has a block whose spillover (942 bits) runs past it, so
    # that the superframe's reading ends 3 bits into the next block's bytes, and a block whose header runs out of bits; a decoder rejects
    # the header and drops the object but keeps the 3 bits to skip. The media object after it starts with 3 zero bits, then the second
    # superframe, which a decoder skips them to find.
    config, built, _ = exact_setup()
    align = config.align
    header = lambda count, spill: v.BitWriter().write(1, 4).write(1, 1).write(count, 6).write(spill, config.spillover_bits)
    first = header(1, 0).append(built[0][0]).to_bytes(align)
    spilling = header(3, 942).to_bytes(align)
    rejected = bytes([0x0F]) + bytes([0xFF]) * (align - 1)
    lead = v.BitWriter().write(0, 3).append(built[1][0]).to_bytes((3 + len(built[1][0]) + 7) // 8)
    counts_none = header(0, 0).to_bytes(align)
    return asf_objects(config, [first, spilling + rejected, lead, counts_none], 2 * 480 / config.rate, 2)


fixture("voice-object-skip-kept", "Blocks of 120 bytes: a packet ends in the start of a superframe; the next media object has a block whose spillover runs 3 bits past the block, and a block whose header runs out of bits; the media object after it begins with 3 zero bits and the second superframe. A decoder rejects the header and drops the object but keeps the 3 bits to skip, which it skips at the start of the next object.",
        skip_kept,
        [finding("wma.voice-packet-header", "past the packet"), finding("wma.packet-short", "fewer bytes than the block alignment"), finding("wma.voice-music", "first bit is clear"),
         finding("wma.voice-packet-sequence", "follow the packet before"), finding("wma.voice-cache-overflow", "does not fit the 256 bytes"), finding("wma.voice-superframe-padding", "553 bits")],
        {"ffprobe": True, "decode": "errors", "messages": ["WMAPro-in-WMAVoice"], "ffmpeg": {"samples": 960, "superframes": 2}})


def cache_overflow():
    # Superframes that are followed by zero bits a decoder ignores, so that the span of two of them is more than the 256 bytes its cache
    # holds: for one the start fits (291 bits) and the 2,017 spillover bits added to it do not, though the superframe needs more than
    # the start; for the other the start alone does not fit (2,505 bits in its packet).
    config = v.Config(16000, 400, lsp16=True, denoise=3, dc_level=1)
    stream = Stream(config, all_types(20, 9), 9, pad={6: 1615, 11: 2298})
    return asf(config, stream.blocks, 1, stream.decoded_samples() / 16000)


fixture("voice-cache-overflow", "Voice blocks of 400 bytes in which two superframes are followed by zero bits, so that the part of one in its packet is more than the 256 bytes of a decoder's cache and the part of another with its spillover bits is: a decoder copies none of the bits that do not fit and reads what the cache held before.",
        cache_overflow,
        [finding("wma.voice-cache-overflow", "does not fit the 256 bytes"), finding("wma.voice-music", "first bit is clear"),
         finding("wma.voice-spillover-after-music", "lost to a superframe of music")],
        {"ffprobe": True, "decode": "errors", "messages": ["WMAPro-in-WMAVoice"], "ffmpeg": {"samples": 5280, "superframes": 11}})


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    count = 0
    for name, description, builder, findings, crosscheck in FIXTURES:
        data = builder()
        (OUT / f"{name}.asf").write_bytes(data)
        model = m.parse(data)
        manifest = {"description": description, "format": "ASF", **model, "findings": findings, "crosscheck": crosscheck}
        if name in VOICE:
            manifest["voice"] = VOICE[name]()
        (OUT / f"{name}.expected.json").write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        count += 1
    print(f"{count} fixtures written to {OUT}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
