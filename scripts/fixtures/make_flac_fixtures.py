#!/usr/bin/env python3
"""Writes the hand-built FLAC fixtures in tests/fixtures/flac (built-*.flac): damage to the files the encoders wrote (external-*.flac, which
this script reads, so it needs no encoder), and streams written here with flac_model.py's writer for the parts of the format no encoder
writes: escaped partitions, subframes that cannot be read, variable block sizes, the 33-bit side channel with wasted bits, every kind of bad
metadata block.

Each manifest has the same parts as an encoder-made one (make_external_flac_fixtures.py), worked out by flac_model.py from the file, plus a
description, the findings the model does not work out (the metadata's), and what flac -t must say (crosscheck_flac_fixtures.py).
"""

import json
import pathlib
import struct

import flac_model as m
import flac_signal as sig
from make_wavpack_fixtures import ape_tag, id3v1, id3v2

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "flac"


def external(name):
    return (OUT / f"external-{name}.flac").read_bytes()


def junk(length, seed=7):
    """Bytes of 0x80 to 0xFE: no sync code, no signature of a tag, no ASCII."""
    out = bytearray()
    state = seed
    for _ in range(length):
        state = (state * 1103515245 + 12345) & 0x7FFFFFFF
        out.append(0x80 + (state >> 16) % 0x7F)
    return bytes(out)


def frames_of(data):
    stream = m.Stream(data)
    return stream, [(f["offset"], f["length"]) for f in stream.frames]


def pieces(data):
    """(head, [frame bytes], tail): the bytes before the first frame, each frame and what follows the last."""
    stream, spans = frames_of(data)
    head = data[:spans[0][0]]
    return head, [data[o:o + l] for o, l in spans], data[spans[-1][0] + spans[-1][1]:]


def with_crc16(frame):
    body = frame[:-2]
    return body + m.crc16(body).to_bytes(2, "big")


def flip(data, bit):
    out = bytearray(data)
    out[bit >> 3] ^= 0x80 >> (bit & 7)
    return bytes(out)


def set_info(data, **fields):
    """The file with STREAMINFO fields changed (the frames are untouched, so their checksums hold)."""
    info = m.streaminfo(data[8:42])
    info.update(fields)
    body = m.streaminfo_body(info["min_block"], info["max_block"], info["min_frame"], info["max_frame"], info["sample_rate"], info["channels"], info["bits"], info["total"],
                             bytes.fromhex(info["md5"]))
    return data[:8] + body + data[42:]


def reheader(frame, **changes):
    """The frame with its header rewritten (CRC-8 and CRC-16 recomputed). The fields not named keep their values."""
    h, _ = m.header(frame, 0)
    fields = {"number": h["number"], "block": h["block_size"], "rate": h["sample_rate"], "channel_code": h["channel_code"], "bits": h["bits"], "variable": h["variable"],
              "rate_code": h["rate_code"], "block_code": h["block_size_code"], "size_code": h["size_code"]}
    fields.update(changes)
    header = m.frame_header(fields["number"], fields["block"], fields["rate"], fields["channel_code"], fields["bits"], fields["variable"], fields["rate_code"],
                            fields["block_code"], fields["size_code"])
    return with_crc16(header + frame[h["length"]:])


def regions_of(data, top=None):
    return top or [("FLAC", 0, len(data))]


CASES = {}


def case(name, description, data, extra=(), flac_test=None, top=None, decodes=True, ffmpeg=True):
    CASES[name] = {"description": description, "data": data, "extra": list(extra), "flac_test": flac_test, "top": top, "decodes": decodes, "ffmpeg": ffmpeg}


def damage():
    base = external("16-stereo-l5")
    stream, spans = frames_of(base)
    head, frames, tail = pieces(base)
    assert len(frames) == 6 and not tail
    zero = bytes(16)

    def md5_zero(data):
        return data[:26] + zero + data[42:]

    def join(*parts):
        return head + b"".join(parts)

    walk = lambda i: m.walk_frame(base, spans[i][0], m.Stream(base).resolve(m.header(base, spans[i][0])[0]))

    # A header's CRC-8 is the last byte of the header; the frame is otherwise intact.
    h2 = stream.frames[2]["header"]
    case("crc8-fails", "The CRC-8 of the third frame's header differs by one bit, which its CRC-16 over the header sees too; the rest of the frame reads and the next frame follows it.",
         flip(base, (spans[2][0] + h2["length"] - 1) * 8 + 3), flac_test=False)
    case("crc8-fails-crc16-holds", "The CRC-8 of the third frame's header differs by one bit and the frame's CRC-16 is recomputed, so that only the header's checksum fails.",
         join(*frames[:2], with_crc16(flip(frames[2], (h2["length"] - 1) * 8 + 3)), *frames[3:]), flac_test=False)
    case("crc16-fails", "The CRC-16 of the fourth frame differs by one bit.", flip(base, (spans[3][0] + spans[3][1] - 1) * 8 + 5), flac_test=False)
    sub = walk(1)["subframes"][0]
    case("residual-bit-flipped", "One bit of the second frame's residual is flipped, so the frame's walk ends elsewhere and its CRC-16 fails; the next frame is found by search.",
         flip(base, sub["residual_start"] + 40), flac_test=False)
    remainder = sub["remainder_lsb"]
    case("md5-mismatch", "One bit of a Rice remainder in the second frame is flipped and the frame's CRC-16 recomputed: every coded checksum holds, and only the MD5 of the decoded audio differs.",
         join(frames[0], with_crc16(flip(frames[1], remainder - spans[1][0] * 8)), *frames[2:]), flac_test=False)
    case("md5-mismatch-stored", "The MD5 in STREAMINFO differs from the audio's by one bit; every frame is intact.", base[:26] + bytes([base[26] ^ 1]) + base[27:], flac_test=False)
    case("md5-zero", "STREAMINFO's MD5 is 0, which means not known: the CRC-16 of every frame, with no frame missing, verifies the stream.", md5_zero(base), flac_test=True)
    cut_after = join(*frames[:4])
    case("cut-after-frame", "The file ends after the fourth frame, so STREAMINFO counts two frames more than it holds; every CRC-16 holds.", cut_after, flac_test=False)
    case("md5-zero-cut-after-frame", "As cut-after-frame, with an MD5 of 0: every CRC holds and nothing is compared, yet the stream is not verified, as frames are missing.", md5_zero(cut_after), flac_test=None)
    case("cut-mid-frame", "The file ends in the middle of the fourth frame.", join(*frames[:3]) + frames[3][:len(frames[3]) // 2], flac_test=False)
    case("cut-in-header", "The file ends three bytes into the fourth frame's header.", join(*frames[:3]) + frames[3][:3], flac_test=False)
    removed = join(frames[0], *frames[3:])
    case("gap-frames-removed", "The second and third frames are removed: the numbers jump and STREAMINFO counts samples the frames no longer hold.", removed, flac_test=False)
    case("gap-md5-zero", "As gap-frames-removed, with an MD5 of 0: every CRC holds, and the stream is not verified, as samples are missing.", md5_zero(removed), flac_test=None)
    swapped = join(frames[0], frames[2], frames[1], *frames[3:])
    case("frames-swapped", "The second and third frames change places; each frame is intact and the count is right, but the numbers are out of order.", swapped, flac_test=False)
    case("frames-swapped-md5-zero", "As frames-swapped, with an MD5 of 0: every CRC holds and the count is right, and the stream is not verified, as the frames are out of order.", md5_zero(swapped), flac_test=None)
    case("first-frame-removed-md5-zero", "The first frame is removed, and the MD5 is 0, as in a stream cut out of a longer one: every CRC holds; the stream is not verified.",
         md5_zero(join(*frames[1:])), flac_test=None)
    case("frame-repeated", "The second frame appears twice in a row.", join(frames[0], frames[1], frames[1], *frames[2:]), flac_test=False)
    case("junk-between-frames", "100 bytes without a sync code between the second and third frames; no sample is missing.", join(*frames[:2], junk(100), *frames[2:]), flac_test=False)
    decoy = frames[1][:stream.frames[1]["header"]["length"]] + junk(30, 11)
    case("decoy-header-between-frames", "20 junk bytes, a copy of a frame header with its CRC-8 and 30 more junk bytes between the second and third frames: a header found by search counts only when a chain of frames follows it.",
         join(*frames[:2], junk(20), decoy, *frames[2:]), flac_test=False)
    case("damaged-frame-after-frame", "A copy of a frame header with its CRC-8 and 30 junk bytes right after the second frame, where a frame should start: a frame with a header that reads and a subframe that does not.",
         join(*frames[:2], decoy, *frames[2:]), flac_test=False)
    case("junk-before-first-frame", "50 junk bytes between the last metadata block and the first frame.", join(junk(50), *frames), flac_test=False)
    case("junk-after-last-frame", "40 junk bytes after the last frame.", join(*frames) + junk(40), flac_test=False)
    case("no-marker", "A stream cut out of a longer one: it starts at a frame, with no marker and no metadata.", b"".join(frames), flac_test=None, decodes=False,
         extra=[{"code": "flac.no-streaminfo"}] if False else [])
    leading = id3v2()
    case("leading-id3v2", "An ID3v2 tag before the marker.", leading + base, flac_test=None,
         top=[("ID3v2", 0, len(leading)), ("FLAC", len(leading), len(base))])
    case("trailing-id3v1", "An ID3v1 tag after the last frame.", base + id3v1(), flac_test=None, top=[("FLAC", 0, len(base)), ("ID3v1", len(base), 128)])
    ape = ape_tag(("Title", "Hand-built title"), ("Artist", "Hand-built artist"))
    case("trailing-ape", "An APEv2 tag after the last frame.", base + ape, flac_test=None, top=[("FLAC", 0, len(base)), ("APE", len(base), len(ape))])
    case("total-samples-too-many", "STREAMINFO counts 1000 samples more than the frames hold.", set_info(base, total=132300 if False else m.streaminfo(base[8:42])["total"] + 1000), flac_test=False)
    case("total-samples-unknown", "STREAMINFO counts no samples (0); the frames are intact and the MD5 is the audio's.", set_info(base, total=0), flac_test=True)
    case("frame-size-out-of-range", "STREAMINFO gives a maximum frame size smaller than the frames.", set_info(base, min_frame=100, max_frame=1000), flac_test=True)
    case("block-size-out-of-range", "STREAMINFO gives a block size of 2048, below the frames' 4096; FFmpeg refuses the frames.", set_info(base, min_block=2048, max_block=2048), flac_test=True, ffmpeg=False)
    case("frame-differs-from-streaminfo", "The frames code 44,100 Hz, 16 bits and two channels in their headers; STREAMINFO says 48,000 Hz, 24 bits and one channel.",
         set_info(base, sample_rate=48000, bits=24, channels=1), flac_test=False, decodes=False)

    from make_wavpack_fixtures import id3v2_seek, id3v2_title
    seek = id3v2_seek(len(head) + len(frames[0]) + len(frames[1]))
    first = len(seek) + len(head) + len(frames[0]) + len(frames[1])
    title = id3v2_title()
    case("seek-tag-between-frames", "An ID3v2.4 tag whose SEEK frame points at a second ID3v2 tag after the second frame, then the other frames: the stream is read up to the second tag and the bytes after it are not read.",
         seek + head + frames[0] + frames[1] + title + b"".join(frames[2:]), extra=[{"code": "flac.data-after-tag"}], flac_test=None,
         top=[("ID3v2", 0, len(seek)), ("FLAC", len(seek), first - len(seek)), ("ID3v2", first, len(title)), ("Data", first + len(title), len(base) - (len(head) + len(frames[0]) + len(frames[1])))])

    # Padding bits before the CRC-16 that are not 0: the frame whose subframes end inside a byte.
    for i, f in enumerate(stream.frames):
        w = walk(i)
        end_bit = w["subframes"][-1]["end"]
        if end_bit & 7:
            padded = bytearray(frames[i][:-2])
            padded[(end_bit >> 3) - spans[i][0]] |= 1
            case("padding-bits-set", "The padding bits that bring a frame to a byte boundary are set; its CRC-16 is recomputed.",
                 join(*frames[:i], with_crc16(bytes(padded) + b"\0\0"), *frames[i + 1:]), flac_test=None)
            break

    # The strategy of the frame headers.
    case("blocking-strategy-changes", "The third frame's header says the stream has variable block sizes; the others say fixed.",
         join(*frames[:2], reheader(frames[2], variable=True, number=2 * 4096), *frames[3:]), flac_test=None)
    case("variable-block-strategy", "Every frame header counts samples instead of frames (variable block size); the blocks keep their sizes.",
         join(*[reheader(f, variable=True, number=i * 4096) for i, f in enumerate(frames)]), flac_test=None)


def stereo_pair(kind, left, right):
    if kind == "left-side":
        return 8, left, [a - b for a, b in zip(left, right)]
    if kind == "side-right":
        return 9, [a - b for a, b in zip(left, right)], right
    if kind == "mid-side":
        return 10, [(a + b) >> 1 for a, b in zip(left, right)], [a - b for a, b in zip(left, right)]
    return 1, left, right


def writer_frame(number, block, rate, bits, channel_code, channels, specs, variable=False, **header):
    """A frame of the given channels (already assigned) coded with the subframe specs, one dict of write_subframe's arguments for each."""
    head = m.frame_header(number, block, rate, channel_code, bits, variable, **header)
    writes = []
    for samples, spec in zip(channels, specs):
        spec = dict(spec)
        kind = spec.pop("kind")
        sample_bits = spec.pop("bits", bits)
        writes.append(lambda w, s=samples, k=kind, sp=spec, b=sample_bits: m.write_subframe(w, s, k, b, **sp))
    return m.write_frame(head, writes)


def md5_of(rate_bits, channels_samples):
    width = (rate_bits + 7) // 8
    out = bytearray()
    for frame in zip(*channels_samples):
        for v in frame:
            out += (v & ((1 << width * 8) - 1)).to_bytes(width, "little")
    return m.md5_of_pcm(bytes(out))


def stream_of(blocks, frames, rate, channels, bits, total, md5, min_block=None, max_block=None):
    """A stream: the marker, STREAMINFO first, the other metadata blocks (type, body) and the frames."""
    sizes = [len(f) for f in frames]
    info = m.streaminfo_body(min_block or 16, max_block or 4096, min(sizes), max(sizes), rate, channels, bits, total, md5)
    out = b"fLaC" + m.metadata_block(0, info, last=not blocks)
    for k, (kind, body) in enumerate(blocks):
        out += m.metadata_block(kind, body, last=k == len(blocks) - 1)
    return out + b"".join(frames)


def signal_channels(kind, rate, channels, count, bits, shift=0):
    cols = list(zip(*sig.samples(kind, rate, channels, count, bits, shift)))
    return [list(c) for c in cols]


def corners():
    # Escaped partitions, Rice parameters of five bits, every subframe type, wasted bits: 16-bit stereo, 4096 samples per frame.
    rate, bits, block = 44100, 16, 1024
    music = signal_channels("music", rate, 2, block * 6, bits)
    loud = signal_channels("loud", rate, 2, block * 2, bits)

    def chunk(c, i):
        return [ch[i * block:(i + 1) * block] for ch in c]

    frames = []
    # 0: a constant and a verbatim subframe; 1: fixed predictors of order 0 to 4 in the five first channels of mono-like stereo frames.
    constant = [[1234] * block, [-77] * block]
    frames.append(writer_frame(0, block, rate, bits, 1, constant, [{"kind": "constant"}, {"kind": "constant"}]))
    frames.append(writer_frame(1, block, rate, bits, 1, chunk(loud, 0), [{"kind": "verbatim"}, {"kind": "verbatim"}]))
    for order in range(5):
        c = chunk(music, order)
        frames.append(writer_frame(2 + order, block, rate, bits, 1, c, [{"kind": "fixed", "order": order, "partition_order": order}, {"kind": "fixed", "order": 4 - order, "partition_order": 4}]))
    # An LPC subframe with a precision of 4 bits and shift 2, and one of order 8 with a precision of 12 bits.
    c = chunk(music, 5)
    frames.append(writer_frame(7, block, rate, bits, 1, c, [{"kind": "lpc", "order": 3, "coefficients": [6, -5, 2], "precision": 4, "shift": 1, "partition_order": 3},
                                                             {"kind": "lpc", "order": 2, "coefficients": [2000, -1000], "precision": 12, "shift": 10, "partition_order": 2, "method": 1}]))
    # Escaped partitions: a verbatim-like partition of 9-bit samples, a partition of zeros and Rice partitions, in a fixed order 1 subframe of loud noise.
    c = [list(ch) for ch in chunk(loud, 1)]
    c[0][512:768] = [c[0][511]] * 256
    frames.append(writer_frame(8, block, rate, bits, 1, c, [{"kind": "fixed", "order": 1, "partition_order": 2, "escape": {1: 17, 2: 0}}, {"kind": "fixed", "order": 0, "partition_order": 3, "method": 1, "escape": {0: 20}}]))
    # Wasted bits: samples with three low zero bits in one channel, and one with two in the other.
    w = [[(v >> 3) << 3 for v in music[0][:block]], [(v >> 2) << 2 for v in music[1][:block]]]
    frames.append(writer_frame(9, block, rate, bits, 1, w, [{"kind": "fixed", "order": 2, "wasted": 3, "partition_order": 1}, {"kind": "lpc", "order": 2, "coefficients": [3, -2], "precision": 3, "shift": 0, "wasted": 2}]))
    pcm = [sum((chunk(constant, 0)[0:0] or [constant[0]]), []) for _ in range(0)]
    channels = [constant[0] + chunk(loud, 0)[0] + sum((chunk(music, o)[0] for o in range(5)), []) + chunk(music, 5)[0] + c[0] + w[0]]
    channels.append(constant[1] + chunk(loud, 0)[1] + sum((chunk(music, o)[1] for o in range(5)), []) + chunk(music, 5)[1] + c[1] + w[1])
    data = stream_of([], frames, rate, 2, bits, len(channels[0]), bytes.fromhex(md5_of(bits, channels)), 16, block)
    case("subframe-types", "Frames of 1,024 samples that between them use a constant, a verbatim, every fixed order, two linear predictors, partitions of every order up to 4, "
         "escaped partitions (of 17 bits, of 0 bits and of 20 bits), 5-bit Rice parameters and wasted bits.", data, flac_test=True)

    # 33-bit side channels in a 32-bit stream, with and without wasted bits, in all three decorrelated forms.
    rate, bits, block = 44100, 32, 512
    left = [int((2 ** 31 - 1) * (1 if (i // 40) % 2 == 0 else -1) * (0.5 + 0.5 * ((i * 7) % 13) / 13)) for i in range(block * 4)]
    right = [-v - (i & 1) for i, v in enumerate(left)]
    right = [max(-(2 ** 31), min(2 ** 31 - 1, v)) for v in right]
    kinds = ["left-side", "side-right", "mid-side", "left-side"]
    frames = []
    chans = [[], []]
    for i, kind in enumerate(kinds):
        l, r = left[i * block:(i + 1) * block], right[i * block:(i + 1) * block]
        if i in (2, 3):
            # A residual must fit 32 bits, so the predicted side channel has to be smooth.
            l = [2 ** 31 - 1 - (j * j) % 1000 for j in range(block)]
            r = [-2 ** 31 + j % 7 for j in range(block)]
        if i == 3:
            l = [(v >> 1) << 1 for v in l]
            r = [(v >> 1) << 1 for v in r]
        code, a, b = stereo_pair(kind, l, r)
        chans[0] += l
        chans[1] += r
        side = 1 if code in (8, 10) else 0
        specs = [{"kind": "verbatim"}, {"kind": "verbatim"}]
        specs[side] = dict(specs[side], bits=33)
        if i in (2, 3):
            specs = [{"kind": "fixed", "order": 1, "bits": 32, "method": 1}, {"kind": "fixed", "order": 2, "bits": 33, "method": 1}]
        if i == 3:
            specs[side] = {"kind": "fixed", "order": 1, "bits": 33, "wasted": 1, "method": 1}
        frames.append(writer_frame(i, block, rate, bits, code, [a, b], specs))
    data = stream_of([], frames, rate, 2, bits, len(chans[0]), bytes.fromhex(md5_of(bits, chans)), 16, block)
    case("side-33-bit", "A 32-bit stereo stream whose side channel needs 33 bits: left/side, side/right and mid/side frames, one with a wasted bit in the side channel.", data, flac_test=True)

    # Variable block sizes: the number is the first sample's, coded in 1 to 5 bytes; sizes 192, 576, 1000, 100, 4096, 300, 16.
    rate, bits = 48000, 16
    sizes = [192, 576, 1000, 100, 4096, 300, 16, 2048]
    total = sum(sizes)
    music = signal_channels("music", rate, 1, total, bits)[0]
    frames, position = [], 0
    for size in sizes:
        part = music[position:position + size]
        frames.append(writer_frame(position, size, rate, bits, 0, [part], [{"kind": "fixed", "order": 2, "partition_order": 0}], variable=True))
        position += size
    data = stream_of([], frames, rate, 1, bits, total, bytes.fromhex(md5_of(bits, [music])), 16, 4096)
    case("variable-block-sizes", "Mono frames of 192, 576, 1000, 100, 4096, 300, 16 and 2,048 samples with variable block size: each header counts samples; the sizes 1000 and 300 are stated in 16 and 8 bits... "
         "and 100 in 8.", data, flac_test=True)

    # Sample rates and bit depths the header states or leaves to STREAMINFO.
    frames, chans = [], []
    for k, (rate_value, bits_value, rate_code, size_code) in enumerate([(44100, 12, 0, 2), (12000, 12, 12, 0), (11025, 12, 13, 2), (640000, 12, 14, 0), (88200, 12, 0, 0)]):
        pass
    rate, bits, block = 44100, 12, 256
    music = signal_channels("music", rate, 1, block * 5, bits)[0]
    frames = []
    specs = [{"rate_code": 9, "size_code": 2}, {"rate_code": 0, "size_code": 0}, {"rate_code": 13, "size_code": 0, "rate": 44100}, {"rate_code": 12, "size_code": 2, "rate": 44000}, {"rate_code": 14, "size_code": 2, "rate": 64000}]
    for k, spec in enumerate(specs):
        kwargs = {"rate_code": spec["rate_code"], "size_code": spec["size_code"]}
        frames.append(writer_frame(k, block, spec.get("rate", 44100), 12, 0, [music[k * block:(k + 1) * block]], [{"kind": "fixed", "order": 1, "partition_order": 0}], **kwargs))
    data = stream_of([], frames, rate, 1, bits, block * 5, bytes.fromhex(md5_of(bits, [music])), block, block)
    case("header-codes", "12-bit mono frames whose headers name the sample rate by a table code, leave it to STREAMINFO, state it in Hz, in kHz and in tens of Hz, and name or leave the sample size.",
         data, flac_test=None)

    # Eight channels and one channel, verbatim and constant.
    rate, bits, block = 48000, 8, 128
    ch8 = signal_channels("music", rate, 8, block, bits)
    frame8 = writer_frame(0, block, rate, bits, 7, ch8, [{"kind": "verbatim"}] * 7 + [{"kind": "constant"}] if False else [{"kind": "verbatim"}] * 8)
    data = stream_of([], [frame8], rate, 8, bits, block, bytes.fromhex(md5_of(bits, ch8)), block, block)
    case("eight-channels", "One frame of eight verbatim channels of 8 bits.", data, flac_test=True)


def faults():
    """Frames whose headers read and whose subframes cannot be read, each in its own way, between intact frames."""
    rate, bits, block = 44100, 16, 256
    music = signal_channels("music", rate, 1, block * 20, bits)[0]
    good = lambda i: writer_frame(i, block, rate, bits, 0, [music[i * block:(i + 1) * block]], [{"kind": "fixed", "order": 2, "partition_order": 1}])
    frames = []
    bad = []

    def broken(i, write):
        head = m.frame_header(i, block, rate, 0, bits)
        w = m.BitWriter()
        write(w)
        w.align()
        body = head + w.bytes()
        return body + m.crc16(body).to_bytes(2, "big")

    def lead(w, type_code, wasted=None):
        w.write(0, 1)
        w.write(type_code, 6)
        if wasted is None:
            w.write(0, 1)
        else:
            w.write(1, 1)
            w.unary(wasted - 1)

    def padding_bit(w):
        w.write(1, 1)
        w.write(0, 6)
        w.write(0, 1)

    def reserved_type(w):
        lead(w, 2)

    def too_many_wasted(w):
        lead(w, 0, wasted=16)

    def order_above_block(w):
        w.write(0, 1)
        w.write(32 + 31, 6)  # lpc order 32
        w.write(0, 1)

    def precision(w):
        lead(w, 32 + 1)
        for _ in range(2):
            w.signed(0, 16)
        w.write(15, 4)

    def shift(w):
        lead(w, 32 + 1)
        for _ in range(2):
            w.signed(0, 16)
        w.write(3, 4)
        w.signed(-1, 5)

    def residual_method(w):
        lead(w, 8 + 1)
        w.signed(5, 16)
        w.write(2, 2)

    def partition_order(w):
        lead(w, 8 + 1)
        w.signed(5, 16)
        w.write(0, 2)
        w.write(9, 4)

    def overflow(w):
        lead(w, 8)
        w.write(0, 2)
        w.write(0, 4)
        w.write(30, 4) if False else None
        w.write(0, 4)
        w.unary(40)  # parameter 0, quotient 40 is fine; overflow needs more bits than one frame holds

    def rice_overflow(w):
        lead(w, 8)
        w.write(1, 2)  # method 1: five-bit parameters
        w.write(0, 4)
        w.write(30, 5)  # parameter 30
        w.bits.extend([0] * 4)  # a quotient of 4 or more shifts past 32 bits
        w.bits.append(1)

    faults_list = [reserved_type, padding_bit, too_many_wasted, precision, shift, residual_method, partition_order, rice_overflow]
    order = []
    for i, f in enumerate(faults_list):
        frames.append(good(2 * i))
        frames.append(broken(2 * i + 1, f))
    frames.append(good(2 * len(faults_list)))
    total = block * len(frames)
    data = stream_of([], frames, rate, 1, bits, total, bytes(16), block, block)
    case("subframe-faults", "Frames whose headers read and whose subframes cannot be read: a reserved type, a reserved first bit, as many wasted bits as the sample has, the forbidden "
         "precision, a negative shift, a reserved residual method, a partition order the block size does not divide into and a Rice code that does not fit 32 bits; each between intact frames.",
         data, flac_test=False, decodes=False)


def metadata():
    rate, bits, block = 44100, 16, 256
    music = signal_channels("music", rate, 1, block * 2, bits)[0]
    frames = [writer_frame(i, block, rate, bits, 0, [music[i * block:(i + 1) * block]], [{"kind": "fixed", "order": 1}]) for i in range(2)]
    md5 = bytes.fromhex(md5_of(bits, [music]))
    info = m.streaminfo_body(block, block, min(map(len, frames)), max(map(len, frames)), rate, 1, bits, block * 2, md5)

    def stream(blocks, info_body=info, first_last=None, frames_=frames):
        """STREAMINFO (unless info_body is None), then blocks given as (type, body); the last block has the flag."""
        items = ([(0, info_body)] if info_body is not None else []) + list(blocks)
        out = b"fLaC"
        for k, (kind, body) in enumerate(items):
            out += m.metadata_block(kind, body, last=k == len(items) - 1)
        return out + b"".join(frames_)

    comment = lambda vendor, fields=(): struct.pack("<I", len(vendor)) + vendor + struct.pack("<I", len(fields)) + b"".join(struct.pack("<I", len(f)) + f for f in fields)
    seek = lambda points: b"".join(struct.pack(">QQH", s, o, n) for s, o, n in points)
    full = (1 << 64) - 1

    def cuesheet(tracks, catalog=b"", is_cd=True, lead_in=88200, reserved=False):
        out = catalog.ljust(128, b"\0") + struct.pack(">Q", lead_in) + bytes([0x80 if is_cd else 0]) + bytes(258) + bytes([len(tracks)])
        for offset, number, isrc, flags, indexes in tracks:
            out += struct.pack(">QB", offset, number) + isrc.ljust(12, b"\0") + bytes([flags]) + bytes(13) + bytes([len(indexes)])
            for io, inum in indexes:
                out += struct.pack(">QB", io, inum) + bytes(3)
        return out

    def picture(kind=3, mime=b"image/png", description=b"Cover", width=1, height=1, depth=24, colors=0, data=b"PNGDATA", declared=None):
        return (struct.pack(">I", kind) + struct.pack(">I", len(mime)) + mime + struct.pack(">I", len(description)) + description + struct.pack(">IIII", width, height, depth, colors)
                + struct.pack(">I", len(data) if declared is None else declared) + data)

    case("metadata-all-types", "One block of every type: padding of zeros, an application block, a seek table with a placeholder, a Vorbis comment, a cue sheet with two tracks and a lead-out, a picture, and a block of a reserved type.",
         stream([(1, bytes(20)), (2, b"abcd" + b"application data"), (3, seek([(0, 0, 256), (256, 40, 256), (full, 0, 0)])), (4, comment(b"hand-built", [b"TITLE=Hand-built", b"ARTIST=A"])),
                 (5, cuesheet([(0, 1, b"USABC1234567", 0, [(0, 1), (128, 2)]), (256, 2, b"", 0, [(0, 1)]), (512, 170, b"", 0, [])], b"1234567890123")), (6, picture()), (99, b"reserved")]),
         flac_test=True)
    case("streaminfo-invalid", "A STREAMINFO block with a block size below 16, a sample rate of 0 Hz and 3 bits per sample.",
         stream([], m.streaminfo_body(8, 8, 3, 2, 0, 1, 3, block * 2, md5)), extra=[{"code": "flac.streaminfo-invalid"}] * 4, flac_test=None, decodes=False)
    case("streaminfo-length", "A STREAMINFO block of 40 bytes.", stream([], info + bytes(6)), extra=[{"code": "flac.streaminfo-length"}], flac_test=None)
    case("streaminfo-misplaced", "A padding block first and STREAMINFO second.", stream([], None) if False else b"fLaC" + m.metadata_block(1, bytes(4)) + m.metadata_block(0, info, last=True) + b"".join(frames),
         extra=[{"code": "flac.streaminfo-misplaced"}], flac_test=None, decodes=False)
    case("no-streaminfo", "The only metadata block is a padding block.", b"fLaC" + m.metadata_block(1, bytes(4), last=True) + b"".join(frames), flac_test=None, decodes=False)
    case("metadata-no-last-block", "No metadata block has the last-block flag; the first frame follows STREAMINFO.", b"fLaC" + m.metadata_block(0, info) + b"".join(frames), flac_test=None)
    case("metadata-forbidden-type", "A block header with the forbidden type 127 after STREAMINFO.", b"fLaC" + m.metadata_block(0, info) + bytes([127]) + b"\0\0\0" + b"".join(frames), flac_test=None)
    case("metadata-truncated", "A block states 1,000 bytes and the file ends after 20.", b"fLaC" + m.metadata_block(0, info) + bytes([2]) + (1000).to_bytes(3, "big") + bytes(20), flac_test=None)
    case("seek-table-bad", "A seek table of 40 bytes, which holds two points and 4 bytes over, whose points do not rise and one of which follows a placeholder.",
         stream([(3, seek([(512, 0, 256), (256, 0, 256)])[:36] + bytes(4))]), extra=[{"code": "flac.seek-table-length"}, {"code": "flac.seek-table-unordered"}], flac_test=None)
    case("seek-table-placeholder", "A seek point after a placeholder.", stream([(3, seek([(0, 0, 256), (full, 0, 0), (256, 40, 256)]))]), extra=[{"code": "flac.seek-table-placeholder"}], flac_test=None)
    case("blocks-repeated", "Two seek tables, two Vorbis comments and two cue sheets.",
         stream([(3, seek([(0, 0, 256)])), (3, seek([(0, 0, 256)])), (4, comment(b"a")), (4, comment(b"b")), (5, cuesheet([(0, 170, b"", 0, [])])), (5, cuesheet([(0, 170, b"", 0, [])]))]),
         extra=[{"code": "flac.metadata-block-repeated"}] * 3, flac_test=None)
    case("padding-not-zero", "A padding block with a byte that is not 0.", stream([(1, bytes(10) + b"\x01" + bytes(5))]), extra=[{"code": "flac.padding-not-zero"}], flac_test=None)
    case("comment-trailing-bytes", "A Vorbis comment followed by 5 bytes that belong to no comment.", stream([(4, comment(b"vendor", [b"A=b"]) + b"extra")]), extra=[{"code": "flac.comment-trailing-bytes"}], flac_test=None)
    case("cuesheet-invalid", "A cue sheet shorter than its fixed fields, one that counts three tracks and holds one, and one whose last track is not the lead-out.",
         stream([(5, bytes(100)), (5, cuesheet([(0, 1, b"", 0, [])])[:-0 or None].replace(b"\x01" + bytes(0), b"\x01", 1) if False else cuesheet([(0, 1, b"", 0, [])])[:395] + b"\x03" + cuesheet([(0, 1, b"", 0, [])])[396:]),
                 (5, cuesheet([(0, 1, b"", 0, [])]))]),
         extra=[{"code": "flac.cuesheet-invalid"}] * 3 + [{"code": "flac.metadata-block-repeated"}] * 2, flac_test=None)
    case("cuesheet-reserved", "A cue sheet with a reserved bit set in its first track's flags.", stream([(5, cuesheet([(0, 1, b"", 0x01, []), (256, 170, b"", 0, [])]))]), extra=[{"code": "flac.cuesheet-reserved"}], flac_test=None)
    case("picture-invalid", "A picture block that ends inside its MIME type, one that states more data than it holds, and one that ends before its length fields.",
         stream([(6, struct.pack(">II", 3, 100) + b"image"), (6, picture(declared=500)), (6, b"\0\0\0\3")]),
         extra=[{"code": "flac.picture-invalid"}] * 3, flac_test=None)
    many = [(1, b"")] * 1100
    case("metadata-blocks-not-listed", "1,100 empty padding blocks after STREAMINFO, more than the report lists.", stream(many), extra=[{"code": "flac.metadata-blocks-not-listed"}], flac_test=None)


def build():
    damage()
    corners()
    faults()
    metadata()
    return CASES


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    count = 0
    for name, c in build().items():
        data = c["data"]
        top = c["top"] or [("FLAC", 0, len(data))]
        flac = [t for t in top if t[0] == "FLAC"][0]
        stream = m.Stream(data, flac[1], flac[1] + flac[2])
        extra = {"regions": [{"name": n, "offset": o, "length": l} for n, o, l in top], "description": c["description"], "crosscheck": {"flac_test": c["flac_test"], "decodes": c["decodes"], "ffmpeg": c["ffmpeg"]}}
        result = m.manifest(data, flac[1], flac[1] + flac[2], extra)
        result["findings"] += c["extra"]
        (OUT / f"built-{name}.flac").write_bytes(data)
        (OUT / f"built-{name}.expected.json").write_text(json.dumps(result, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        count += 1
    print(f"Wrote {count} hand-built fixtures to {OUT}")


if __name__ == "__main__":
    main()
