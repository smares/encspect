#!/usr/bin/env python3
"""Writes the hand-built ALAC fixtures in tests/fixtures/alac (built-*.m4a and built-*.mka): streams written here with alac_model.py's writer, for each
case the format and the damage allow.

- The streams that are valid are written from the deterministic signals of flac_signal.py in every shape the format has: 1 to 8 channels, 16, 20, 24 and
  32 bits, plain and mixed pairs, shifted bytes, uncompressed (escape) elements, partial frames, zero runs and long codes, data stream and fill elements,
  and the older forms of the cookie. Their frames decode to the signal: crosscheck_alac_fixtures.py decodes each with Apple's alacconvert (built from
  the pinned source) and with FFmpeg, and compares the samples with the manifest's hashes.
- The damaged streams give every finding of the ALAC parser its case: a cookie that is too short, has a field out of range or is missing, and a frame
  that ends early, has bytes after its end tag, has no end tag, holds an element with no syntax, too many or too few channels, a header the decoder refuses,
  or a run of zeros past its samples.

Each manifest is worked out by alac_model.py from the file (a reading of Apple's source apart from the analyzer's), plus a description.

    python3 scripts/fixtures/make_alac_fixtures.py [name ...]
"""

import hashlib
import json
import pathlib
import sys

import alac_model as m
import flac_signal as sig
import matroska_model as mkv
import mp4_boxes as b

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "tests" / "fixtures" / "alac"

CASES = {}
KEEP = object()

O8 = [1166, -755, -9, 37, 16, 86, 43, 25]
O4 = [1099, -651, -43, 117]
O12 = [800 - 40 * i for i in range(12)]
O16 = [300 + 7 * i for i in range(16)]
STEREO = [m.Channel(O8), m.Channel(O4)]
MONO = [m.Channel(O4)]


def planes(channels, bits, rate, count, kind="music"):
    if kind == "sparse":
        # Zeros but for the first and the last sample: the residuals are one long run of zeros.
        return [[5] + [0] * (count - 2) + [-7] for _ in range(channels)]
    if kind == "spikes":
        # Faint noise with a full-scale spike every 37 samples: the residual of a spike is far above the running mean, so it is written with the long prefix.
        state = 987
        out = [[] for _ in range(channels)]
        for i in range(count):
            for c in range(channels):
                state = (state * 1103515245 + 12345) & 0x7FFFFFFF
                value = ((state >> 10) & 7) - 3
                if i % 37 == 5 + c:
                    value = (24000 if i % 2 else -24000) << (bits - 16)
                out[c].append(value)
        return out
    frames = list(sig.samples(kind, rate, channels, count, bits))
    return [[f[c] for f in frames] for c in range(channels)]


def split(data, length):
    return [[plane[i:i + length] for plane in data] for i in range(0, len(data[0]), length)]


def element_options(layout, parameters, **common):
    """The options of each element of a layout: the parameters of its channels and what the elements share."""
    out = {}
    for index, tag in enumerate(layout):
        out[index] = dict(parameters=parameters[:2 if tag == m.CPE else 1] if parameters else None, **common)
        if not parameters:
            del out[index]["parameters"]
    return out


def stream(channels, bits, rate, count, parameters=None, frame_length=4096, kind="music", layout=None, options=None, per_frame=None, **common):
    """(packets, planes): frames of the planes in the layout's elements. options (by element) and per_frame (by frame index, a dict of options by
    element) replace what the common options say."""
    data = planes(channels, bits, rate, count, kind)
    layout = layout or m.LAYOUTS[channels]
    packets = []
    for index, part in enumerate(split(data, frame_length)):
        base = element_options(layout, parameters or ([m.Channel(O4)] * 2), **common)
        for element, extra in (options or {}).items():
            base[element] = {**base.get(element, {}), **extra}
        for element, extra in ((per_frame or {}).get(index) or {}).items():
            base[element] = {**base.get(element, {}), **extra}
        packets.append(m.frame(part, bits, frame_length, layout, base))
    return packets, data


def entry(channels, rate, bits, cookie_bytes, extra=()):
    fields = bytes(6) + b.u16(1) + b.u16(0) + b.u16(0) + b.u32(0) + b.u16(channels) + b.u16(bits) + b.u16(0) + b.u16(0) + b.u32((rate << 16) & 0xFFFFFFFF)
    children = [b.fullbox("alac", 0, 0, cookie_bytes)] if cookie_bytes is not None else []
    return b.box("alac", fields, *children, *extra)


def m4a_file(packets, cookie_bytes, channels, bits, rate, frame_length, count, chunk=4):
    last = count - frame_length * (len(packets) - 1)
    durations = [frame_length] * (len(packets) - 1) + [max(last, 0)]
    track = b.Track(1, packets, rate, channels, entry(channels, rate, bits, cookie_bytes), chunk=chunk, durations=durations)
    data, _, _ = b.movie([track])
    return data


def mka_file(packets, cookie_bytes, channels, bits, rate, frame_length, count):
    clusters = []
    blocks = []
    cluster_start = 0
    for index, packet in enumerate(packets):
        time = index * frame_length * 1000 // rate
        if time - cluster_start > 20000:
            clusters.append(mkv.cluster(cluster_start, *blocks))
            blocks = []
            cluster_start = time
        blocks.append(mkv.simple_block(1, time - cluster_start, [packet], key=True))
    clusters.append(mkv.cluster(cluster_start, *blocks))
    track = mkv.audio_track(1, "A_ALAC", channels, float(rate), bits, private=cookie_bytes, lacing=False)
    return mkv.file_of(mkv.ebml_header(), mkv.segment(mkv.info(duration=count * 1000.0 / rate), mkv.tracks(track), *clusters))


def md5_of(plane, width, shift=0):
    return hashlib.md5(b"".join(((v << shift) if shift else v).to_bytes(width, "little", signed=True) for v in plane)).hexdigest()


def case(name, description, packets, cookie_bytes, channels, bits, rate, count, frame_length=4096, container="mp4", data=None, input_=None, decoders=("ffmpeg", "alacconvert"),
         probes=True):
    """A fixture: the file of the packets and the cookie, and for a stream that is valid the hashes of its input channels (data: the planes)."""
    file = (m4a_file if container == "mp4" else mka_file)(packets, cookie_bytes, channels, bits, rate, frame_length, count)
    extra = {"description": description}
    if data is not None:
        width = {16: 2, 20: 4, 24: 3, 32: 4}[bits]
        shift = 12 if bits == 20 else 0
        extra["input"] = {"channels": channels, "bits": bits, "rate": rate, "samples": count, "width": width, "left_shift": shift, "decoders": list(decoders),
                          "channel_md5": [md5_of(plane, width, shift) for plane in data]}
    if input_:
        extra.setdefault("input", {}).update(input_)
    if not probes:
        extra["crosscheck"] = {"ffprobe": False, "mediainfo": False}
    CASES[name] = {"data": file, "container": container, "extra": extra}


def valid(name, description, channels, bits, rate, count, parameters=None, frame_length=4096, cookie_fields=None, container="mp4", kind="music", layout=None, options=None,
          per_frame=None, decoders=("ffmpeg", "alacconvert"), cookie_bytes=None, **common):
    packets, data = stream(channels, bits, rate, count, parameters, frame_length, kind, layout, options, per_frame, **common)
    # The cookie states the largest packet, as every encoder's does: alacconvert sizes its read buffer by it.
    fields = {"max_frame_bytes": max(len(p) for p in packets), **(cookie_fields or {})}
    cookie_bytes = cookie_bytes if cookie_bytes is not None else m.cookie(channels, frame_length=frame_length, bit_depth=bits, rate=rate, **fields)
    case(name, description, packets, cookie_bytes, channels, bits, rate, count, frame_length, container, data, decoders=decoders)


def stereo_base():
    packets, data = stream(2, 16, 44100, 10000, STEREO, mix_res=2)
    return packets, m.cookie(2, bit_depth=16, rate=44100), data


def build():
    # --- Valid streams
    valid("mono-16", "One channel of 16-bit audio at 44,100 Hz: two whole frames and a partial one of 1,808 samples, each a single channel element with a filter of 4 coefficients.",
          1, 16, 44100, 10000, MONO)
    valid("stereo-16-mixed", "A channel pair of 16-bit audio with the channels mixed (mix bits 2, mix resolution 2: mid/side stereo), filters of 8 and 4 coefficients.",
          2, 16, 44100, 10000, STEREO, mix_res=2)
    valid("stereo-16-plain", "A channel pair coded as left and right (mix resolution 0) with filters of 8 and 4 coefficients.", 2, 16, 44100, 10000, STEREO, mix_res=0)
    valid("stereo-16-mixed-weights", "A channel pair mixed with the weights 1/4 and 3/4 (mix bits 2, mix resolution 1), whose frames alternate with plain ones.", 2, 16, 44100, 14000, STEREO,
          mix_res=1, per_frame={1: {0: {"mix_res": 0}}, 3: {0: {"mix_res": 0}}})
    valid("stereo-16-long-filters", "A channel pair with filters of 16 and 12 coefficients and a history weight factor of 2 in the second channel.", 2, 16, 44100, 10000,
          [m.Channel(O16), m.Channel(O12, weight=2)], mix_res=2)
    valid("stereo-16-order-0", "A channel pair with no filter (0 coefficients), so the residuals are the samples.", 2, 16, 44100, 10000, [m.Channel([]), m.Channel([])], mix_res=2)
    valid("stereo-16-prediction-type-1", "A channel pair whose channels state prediction type 1, the first-order pass Apple's decoder runs before the filter; FFmpeg 6.1.1 does not decode it as Apple's decoder does.",
          2, 16, 44100, 10000, [m.Channel([1166, -755, -9, 37], mode=1), m.Channel([1099, -651], mode=1)], mix_res=2, decoders=("alacconvert",))
    valid("stereo-24-shifted", "24-bit audio as Apple's encoder writes it: one byte of every sample shifted out and stored apart, mid/side mixing.", 2, 24, 44100, 10000, STEREO,
          mix_res=2, shifted=1)
    valid("stereo-24-unshifted", "24-bit audio with no byte shifted out, which the format allows.", 2, 24, 44100, 10000, STEREO, mix_res=2)
    valid("stereo-32-shifted", "32-bit audio as Apple's encoder writes it: two bytes of every sample shifted out.", 2, 32, 44100, 10000, STEREO, mix_res=2, shifted=2)
    valid("stereo-20", "20-bit audio, a bit depth Apple's encoder takes; Apple's alacconvert cannot decode it, FFmpeg gives the samples left-aligned in 32 bits.", 2, 20, 44100, 10000, STEREO,
          mix_res=2, decoders=("ffmpeg",))
    valid("mono-16-silence-long-run", "A single frame of 70,000 samples (the cookie's frame length) that are silent but for the first and the last: one run of zero residuals longer than 65,535, which "
          "the code splits. FFmpeg 8.0.1 decodes one sample of it other than Apple's decoder does (sample 65,551 comes out as -1, not 0), so only alacconvert decodes it back to the input.",
          1, 16, 44100, 70000, [m.Channel([])], frame_length=70000, kind="sparse", decoders=("alacconvert",))
    valid("stereo-16-spikes", "Faint noise in a channel pair with a full-scale spike every 37 samples: the spikes' residuals are written with the long prefix, nine ones and then the value as it is.",
          2, 16, 44100, 10000, STEREO, mix_res=2, kind="spikes")
    valid("stereo-16-uncompressed-only", "A stream of uncompressed frames only, with a cookie whose tuning parameters (pb, mb and kb) are all 0, as FFmpeg's encoder writes it at compression level 0.", 2, 16, 44100, 10000, STEREO,
          mix_res=0, escape=True, cookie_fields={"pb": 0, "mb": 0, "kb": 0})
    valid("stereo-16-whole-frames", "Exactly 8,192 samples: two whole frames, no partial one.", 2, 16, 44100, 8192, STEREO, mix_res=2)
    valid("stereo-16-one-sample-last-frame", "8,193 samples: the last frame holds one sample.", 2, 16, 44100, 8193, STEREO, mix_res=2)
    valid("stereo-16-short", "1,000 samples, less than a frame: the only frame is a partial one.", 2, 16, 44100, 1000, STEREO, mix_res=2)
    valid("stereo-16-escape", "A channel pair whose second frame and last partial frame hold their samples uncompressed (the escape flag), between compressed frames.", 2, 16, 44100, 14000, STEREO,
          mix_res=2, per_frame={1: {0: {"escape": True}}, 3: {0: {"escape": True}}})
    valid("stereo-24-escape", "24-bit audio with an uncompressed second frame, whose samples are 24 bits each.", 2, 24, 48000, 10000, STEREO, mix_res=2, shifted=1,
          per_frame={1: {0: {"escape": True, "shifted": 0}}})
    valid("stereo-32-escape", "32-bit audio with an uncompressed second frame, whose samples are 32 bits each; FFmpeg 8.0.1 refuses it (\"bps 33 is not implemented\"), so only alacconvert decodes it.",
          2, 32, 48000, 10000, STEREO, mix_res=2, shifted=2, per_frame={1: {0: {"escape": True, "shifted": 0}}}, decoders=("alacconvert",))
    valid("mono-24-escape", "24-bit mono audio with an uncompressed second frame.", 1, 24, 48000, 10000, MONO, shifted=1, per_frame={1: {0: {"escape": True, "shifted": 0}}})
    valid("three-channels-16", "Three channels (center, left, right) as Apple's encoder writes them: a single channel element and a channel pair element, with the channel layout info in the cookie.",
          3, 16, 48000, 10000, STEREO)
    valid("four-channels-16", "Four channels (center, left, right, back center): a single channel element, a channel pair element and a single channel element.", 4, 16, 48000, 10000, STEREO)
    valid("five-one-16", "5.1 audio (center, left, right, left surround, right surround, LFE) as Apple's encoder writes it: SCE, CPE, CPE and a fourth element.", 6, 16, 48000, 10000, STEREO,
          mix_res=2)
    valid("five-one-16-lfe-tag", "5.1 audio whose sixth channel is an LFE element, tag 3, which the decoder reads as a single channel element.", 6, 16, 48000, 10000, STEREO, mix_res=2,
          layout=[m.SCE, m.CPE, m.CPE, m.LFE])
    valid("seven-one-16", "7.1 audio (center, left center, right center, left, right, left surround, right surround, LFE): SCE, CPE, CPE, CPE and SCE.", 8, 16, 48000, 6000, STEREO,
          mix_res=2, frame_length=4096)
    valid("seven-one-24", "7.1 audio at 24 bits with one byte shifted out of every sample.", 8, 24, 48000, 5000, STEREO, mix_res=2, shifted=1)

    # Elements that carry no audio.
    stereo = planes(2, 16, 44100, 10000)
    packets = []
    for index, part in enumerate(split(stereo, 4096)):
        w = m.BitWriter()
        m.write_fill(w, 3)
        m.write_audio(w, m.CPE, 0, part, 16, 4096, parameters=STEREO, mix_res=2)
        m.write_data_stream(w, 0, bytes(range(7)), aligned=False)
        m.write_data_stream(w, 1, bytes(range(40)) * 7, aligned=True)
        m.write_fill(w, 20, 0xAA)
        m.write_end(w)
        packets.append(w.data())
    case("stereo-16-fill-and-data-stream", "Fill and data stream elements around the audio: a fill element of 3 bytes before the channel pair element, then a data stream element of 7 bytes, an aligned one of 280 bytes "
         "(a count of 255 and 25 more) and a fill element of 20 bytes (a count of 15 and 6 more). FFmpeg 8.0.1 does not decode the element types (\"Not yet implemented\"), so only alacconvert does.",
         packets, m.cookie(2, bit_depth=16, rate=44100, max_frame_bytes=max(len(p) for p in packets)), 2, 16, 44100, 10000, data=stereo, decoders=("alacconvert",))

    # The older forms of the cookie, in Matroska, whose CodecPrivate holds the cookie as the container stores it.
    packets, data = stream(2, 16, 44100, 10000, STEREO, mix_res=2)
    plain = m.cookie(2, bit_depth=16, rate=44100)
    case("mka-cookie-plain", "A Matroska file whose CodecPrivate is the 24-byte ALACSpecificConfig alone.", packets, plain, 2, 16, 44100, 10000, container="mka", data=data)
    case("mka-cookie-atom", "A Matroska file whose CodecPrivate is the cookie in an alac atom of 36 bytes, the form FFmpeg keeps for an MP4 file's cookie; FFmpeg 8.0.1 reads a Matroska track's CodecPrivate as the configuration alone and refuses this one, so only alacconvert decodes it.",
         packets, m.atom_cookie(plain), 2, 16, 44100, 10000, container="mka", data=data, decoders=("alacconvert",), probes=False)
    case("mka-cookie-old-form", "A Matroska file whose CodecPrivate is the older encoders' cookie: the format atom, the alac atom, the config and the terminator atom, 56 bytes; FFmpeg 8.0.1 refuses it, as the 36-byte form.", packets,
         m.atom_cookie(plain, frma=True, terminator=True), 2, 16, 44100, 10000, container="mka", data=data, decoders=("alacconvert",), probes=False)
    five = stream(6, 16, 48000, 10000, STEREO, mix_res=2)
    case("mka-cookie-five-one", "A Matroska file of 5.1 audio whose CodecPrivate holds the config and the channel layout info (48 bytes), as Apple's encoder writes it.", five[0], m.cookie(6, rate=48000), 6, 16, 48000,
         10000, container="mka", data=five[1])

    # --- Damaged streams
    packets, cookie, data = stereo_base()
    count = 10000

    def damaged(name, description, packets_, cookie_=KEEP, channels=2, bits=16, rate=44100, frame_length=4096, container="mp4"):
        case(name, description, packets_, cookie if cookie_ is KEEP else cookie_, channels, bits, rate, count, frame_length, container)

    damaged("cookie-too-short", "The alac box holds 20 bytes, fewer than the 24 of the ALACSpecificConfig: the stream's figures are unknown and its frames are not read.", packets, cookie[:20])
    damaged("cookie-extra", "The cookie is followed by six bytes the description does not define.", packets, cookie + bytes(range(1, 7)))
    damaged("compatible-version", "A cookie that states the compatible version 1, which Apple's decoder refuses.", packets, m.cookie(2, bit_depth=16, rate=44100, version=1))
    damaged("frame-length-zero", "A cookie that states a frame length of 0; every element states its own sample count, which is more than that, so the frames read and each is a partial frame too long.",
            stream(2, 16, 44100, count, STEREO, mix_res=2, per_frame={i: {0: {"partial": True}} for i in range(3)})[0], m.cookie(2, bit_depth=16, rate=44100, frame_length=0))
    damaged("frame-length-too-large", "A cookie that states a frame length of 1,048,577, one more than 2^20; every element states its own sample count.",
            stream(2, 16, 44100, count, STEREO, mix_res=2, per_frame={i: {0: {"partial": True}} for i in range(3)})[0], m.cookie(2, bit_depth=16, rate=44100, frame_length=(1 << 20) + 1))
    twelve, twelve_data = stream(2, 12, 44100, count, STEREO, mix_res=2)
    damaged("bit-depth-12", "A cookie of 12-bit audio, which Apple's encoder does not write; the frames are written to that width and read to their end.", twelve, m.cookie(2, bit_depth=12, rate=44100), bits=12)
    damaged("channels-zero", "A cookie that states 0 channels: the stream's channels are unknown and its frames are not read.", packets, m.cookie(0, layout=False, bit_depth=16, rate=44100))
    damaged("tuning-kb-zero", "A cookie whose maximum Golomb parameter (kb) is 0, which the reference code leaves undefined, over compressed frames: their first elements are not read.", packets,
            m.cookie(2, bit_depth=16, rate=44100, kb=0))
    damaged("layout-differs", "A cookie of two channels whose channel layout info names the six channels of 5.1.", packets, cookie + m.layout_info(6))
    damaged("tuning-nonstandard", "A cookie with the tuning parameters 30, 12 and 10 in place of Apple's 40, 10 and 14; the frames are coded with them and read to their end.",
            stream(2, 16, 44100, count, STEREO, mix_res=2, pb=30, mb=12, kb=10)[0], m.cookie(2, bit_depth=16, rate=44100, pb=30, mb=12, kb=10))

    first = packets[1]

    def custom_fill():
        w = m.BitWriter()
        m.write_fill(w, 0)
        return w.data()

    damaged("frame-overrun-residuals", "The second packet is cut 200 bytes short, inside the residuals of its first channel's successor.", [packets[0], first[:-200], packets[2]])
    damaged("frame-overrun-header", "The second packet is cut to 5 bytes, inside its first element's header.", [packets[0], first[:5], packets[2]])
    damaged("frame-overrun-no-audio", "The second packet holds one fill element and then ends, before any of the cookie's two channels is there.", [packets[0], custom_fill(), packets[2]])
    damaged("frame-underrun", "Nine bytes follow the end tag of the second packet.", [packets[0], first + bytes(range(1, 10)), packets[2]])

    def without_end(index):
        w = m.BitWriter()
        part = split(data, 4096)[index]
        m.write_audio(w, m.CPE, 0, part, 16, 4096, parameters=STEREO, mix_res=2)
        pad = 0
        while w.position % 8 not in (0, 6, 7):
            m.write_fill(w, 0)
            pad += 1
        return w.data()

    damaged("end-missing", "The second packet ends after its channel pair element, and the fill elements that bring its length to a byte boundary, without an end tag.", [packets[0], without_end(1), packets[2]])

    def custom(*writers):
        w = m.BitWriter()
        for write in writers:
            write(w)
        return w.data()

    cce = custom(lambda w: w.write(m.CCE, 3), lambda w: w.write(0x5A5A, 16))
    pce = custom(lambda w: w.write(m.PCE, 3), lambda w: w.write(0xA5A5, 16))
    damaged("element-unknown-cce", "The second packet begins with a coupling channel element, tag 2, which ALAC defines no syntax for.", [packets[0], cce, packets[2]])
    damaged("element-unknown-pce", "The second packet begins with a program config element, tag 5, which ALAC defines no syntax for.", [packets[0], pce, packets[2]])
    part = split(data, 4096)[1]
    mono = [part[0]]
    exceeding = custom(lambda w: m.write_audio(w, m.CPE, 0, part, 16, 4096, parameters=STEREO, mix_res=2), lambda w: m.write_audio(w, m.SCE, 0, mono, 16, 4096, parameters=MONO), m.write_end)
    damaged("channels-exceed", "The second packet holds a channel pair element and a single channel element, three channels, where the cookie states two.", [packets[0], exceeding, packets[2]])
    missing = custom(lambda w: m.write_audio(w, m.SCE, 0, mono, 16, 4096, parameters=MONO), m.write_end)
    damaged("channels-missing", "The second packet holds one single channel element, where the cookie states two channels.", [packets[0], missing, packets[2]])
    longer = planes(2, 16, 44100, 5000)
    too_long = custom(lambda w: m.write_audio(w, m.CPE, 0, longer, 16, 4096, parameters=STEREO, mix_res=2), m.write_end)
    damaged("partial-frame-too-long", "The second packet states 5,000 samples, more than the frame length of 4,096 in the cookie.", [packets[0], too_long, packets[2]])
    reserved = custom(lambda w: m.write_audio(w, m.CPE, 0, part, 16, 4096, parameters=STEREO, mix_res=2, reserved=1), m.write_end)
    damaged("element-reserved-bits", "The 12 reserved bits of the first element of the second packet hold 1, which the reference decoder refuses: the frame is not read further.", [packets[0], reserved, packets[2]])
    invalid = custom(lambda w: m.write_audio(w, m.CPE, 0, part, 16, 4096, parameters=STEREO, mix_res=2, shift_flags=3), m.write_end)
    damaged("element-invalid-shift", "The first element of the second packet states 3 bytes shifted, which the reference decoder refuses.", [packets[0], invalid, packets[2]])
    mono24, mono24_data = stream(1, 24, 48000, 10000, MONO, shifted=1, per_frame={1: {0: {"escape": True, "shifted": 1}}})
    case("escape-shifted", "A 24-bit mono stream whose second element is uncompressed and states 1 byte shifted; the reference decoder reads its samples 16 bits wide.", mono24, m.cookie(1, bit_depth=24, rate=48000),
         1, 24, 48000, 10000)
    different = custom(lambda w: m.write_audio(w, m.SCE, 0, [part[0]], 16, 4096, parameters=MONO),
                       lambda w: m.write_audio(w, m.SCE, 1, [part[1][:3000]], 16, 4096, parameters=MONO), m.write_end)
    damaged("element-samples-differ", "The second packet holds two single channel elements of 4,096 and 3,000 samples.", [packets[0], different, packets[2]])
    overlong = custom(lambda w: m.write_audio(w, m.SCE, 0, [[0] * 1000], 16, 4096, parameters=[m.Channel([])], count=100), m.write_end)
    damaged("residual-run-too-long", "The first element of the second packet states 100 samples, but its residuals code a run of 999 zeros after the first.", [packets[0], overlong, packets[2]])
    damaged("cookie-missing-mka", "A Matroska file of ALAC frames with no CodecPrivate: the frames cannot be read.", packets, None, container="mka")


def main():
    chosen = [a for a in sys.argv[1:] if not a.startswith("--")]
    build()
    OUT.mkdir(parents=True, exist_ok=True)
    count = 0
    for name, item in CASES.items():
        if chosen and name not in chosen:
            continue
        path = OUT / f"built-{name}.{'m4a' if item['container'] == 'mp4' else 'mka'}"
        path.write_bytes(item["data"])
        result = m.manifest(item["data"], "mp4" if item["container"] == "mp4" else "matroska", item["extra"])
        path.with_suffix(".expected.json").write_text(m.dump(result), encoding="utf-8", newline="\n")
        count += 1
    print(f"Wrote {count} fixtures to {OUT}")


if __name__ == "__main__":
    main()
