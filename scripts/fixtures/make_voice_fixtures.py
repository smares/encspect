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


def packetize(config, superframes, orphan, seed):
    import random
    junk = v.BitWriter().random(random.Random(seed ^ 0xBEEF), orphan)
    capacity = config.align * 8
    packets = []
    details = []
    pending = junk
    index = 0
    sequence = 0
    while index < len(superframes) or len(pending):
        spill = len(pending)
        room = capacity - (5 + 6 + config.spillover_bits) - spill
        body = v.BitWriter().append(pending)
        pending = v.BitWriter()
        count = 0
        cut = None
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
                room = 0
            index += 1
        header = v.BitWriter().write(sequence & 15, 4).write(1 if config.residual else 0, 1).write(count, 6).write(spill, config.spillover_bits)
        packets.append(header.append(body).to_bytes(config.align))
        details.append({"spill": spill, "count": count, "cut": cut, "first": index - count})
        sequence += 1
    return packets, details


def asf(config, blocks, per_object=1, seconds=None, tail=0, rate=None, align=None, extra=None):
    """The ASF file of a stream's blocks: each media object (per_object blocks) is the payload of a packet, in the layout of
    Windows Media's audio spread error correction with one chunk. rate, align and extra replace the WAVEFORMATEX's fields."""
    objects = [b"".join(blocks[i:i + per_object]) for i in range(0, len(blocks), per_object)]
    if tail:
        objects[-1] += bytes(range(1, tail + 1))
    size = max(len(o) for o in objects) + 48
    chunk = config.align * per_object
    stream = m.stream_properties(1, config.wave_format(rate, align, extra), ec_type=m.AUDIO_SPREAD, ec_data=m.spread_audio(1, chunk, chunk, b"\0"))
    per_object_seconds = (seconds if seconds is not None else len(blocks) * config.align * 8 / 6000) / len(objects)
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
valid("voice-object-not-whole", "Voice blocks of 100 bytes, the last media object 10 bytes longer than a whole number of blocks.", eleven_khz, per_object=2, tail=10,
      findings=[finding("wma.packet-long", "10 of them")],
      crosscheck={"ffprobe": True, "decode": "errors", "messages": [],
                  "samples_differ": "ffmpeg cuts a media object that is not a whole number of blocks by the remainder (wmavoice.c: size = ((n - 1) % block_align) + 1), so it reads the 10 bytes as a packet of their own and loses the whole blocks before them; the analysis reads the whole blocks and ignores the rest."})


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
damaged_config("voice-config-rate", "A sample rate of 100 Hz, below the 322 Hz FFmpeg's decoder supports.",
               {"denoise": 5, "dc_level": 9}, "wma.voice-sample-rate", "100 Hz", "Unsupported samplerate 100", rate=100)


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
        {"ffprobe": False, "reason": "the file is cut inside a payload, which ffmpeg returns as a short packet.", "decode": "errors", "messages": []})


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
        [finding("wma.voice-spillover-orphan", "the first has 9"), finding("wma.voice-superframe-padding", "16 bits"), finding("wma.voice-superframe-overrun", "more bits than the packets give"),
         finding("wma.voice-music", "first bit is clear")],
        {"ffprobe": True, "decode": "errors", "messages": ["WMAPro-in-WMAVoice"]})


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
         finding("wma.voice-packet-sequence", "follow the packet before"), finding("wma.voice-spillover-orphan", "state spillover bits"),
         finding("wma.voice-superframe-padding", "ends before the bits the packets give it")],
        {"ffprobe": True, "decode": "errors", "messages": ["WMAPro-in-WMAVoice", "Superframe encodes > 480 samples", "Invalid frame type VLC code"]})


def header_invalid():
    config = v.Config(8000, 2, denoise=5, dc_level=9)
    return asf(config, [bytes([0x07, 0xE0])] * 3, 1, 0.5)


fixture("voice-packet-header", "Blocks of 2 bytes whose header counts superframes in a 6-bit value of 63 with no room for the value that must follow.",
        header_invalid,
        [finding("wma.voice-packet-header", "run past the packet"), finding("wma.voice-packet-sequence", "follow the packet before")],
        {"ffprobe": True, "decode": "errors", "messages": []})


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
