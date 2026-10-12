"""Ogg pages, Speex packets and Vorbis comments, written and read for the Ogg fixtures.

Writing: page() builds one page (RFC 3533, section 6) with its CRC-32 (polynomial 0x04C11DB7,
initial value 0, no reflection, over the page with the CRC field zeroed); pages_for() cuts a
stream's packets into pages by segment counts, so a packet may span pages; the Speex helpers build
headers (speex_header.h), Vorbis comments (Vorbis I specification, section 5) and frames whose
submode bits are real and whose coded data is zero bits, which libspeex decodes without complaint.

Reading: read_pages() lists the pages of a well-formed file, stream_packets() puts each logical
stream's packets together, and walk() follows a Speex packet's frames the way libspeex 1.2.1 does
(nb_celp.c nb_decode, sb_celp.c sb_decode, speex_callbacks.c). speex_expectations() turns a
stream's pages and packets into what the analyzer must report, and speexdec_samples() into what
speexdec outputs, which crosscheck_ogg_fixtures.py confirms with the real tools.

Standard library only, so the hand-built fixtures regenerate identically in CI.
"""

import struct

# --- Ogg pages ----------------------------------------------------------------------------------

CONTINUED, BOS, EOS = 0x01, 0x02, 0x04


def _crc_table():
    table = []
    for i in range(256):
        r = i << 24
        for _ in range(8):
            r = ((r << 1) ^ 0x04C11DB7) if r & 0x80000000 else (r << 1)
        table.append(r & 0xFFFFFFFF)
    return table


CRC_TABLE = _crc_table()


def ogg_crc(data):
    crc = 0
    for b in data:
        crc = ((crc << 8) & 0xFFFFFFFF) ^ CRC_TABLE[(crc >> 24) ^ b]
    return crc


def page(serial, sequence, granule, flags, lacing, body, crc=None, version=0):
    """One page; crc replaces the computed CRC when given."""
    header = b"OggS" + struct.pack("<BBqIIIB", version, flags, granule, serial, sequence, 0, len(lacing)) + bytes(lacing)
    data = header + bytes(body)
    computed = ogg_crc(data)
    value = computed if crc is None else crc
    return data[:22] + struct.pack("<I", value) + data[26:], computed


def lacing_of(length):
    return [255] * (length // 255) + [length % 255]


class PageSpec:
    """A page before it is written: the stream's serial and sequence, its granule position, flags,
    lacing values and body, and which of the stream's packets end on it."""

    def __init__(self, serial, sequence, granule, flags, lacing, body, ends):
        self.serial, self.sequence, self.granule, self.flags = serial, sequence, granule, flags
        self.lacing, self.body, self.ends = list(lacing), bytes(body), list(ends)
        self.crc = None


def pages_for(serial, packets, granules, cuts, first_sequence=0, bos=True, eos=True):
    """Cuts packets into pages of cuts[i] segments each (the last page takes the rest). A page's
    granule position is granules[k] of the last packet k that ends on it, or -1 where none does."""
    segments = []
    for index, packet in enumerate(packets):
        start = 0
        for value in lacing_of(len(packet)):
            segments.append((value, packet[start:start + value], index if value < 255 else None))
            start += value
    pages, position, sequence = [], 0, first_sequence
    cuts = list(cuts)
    while position < len(segments) or not pages:
        count = cuts.pop(0) if cuts else len(segments) - position
        chunk = segments[position:position + count]
        continued = position > 0 and segments[position - 1][0] == 255
        ends = [s[2] for s in chunk if s[2] is not None]
        flags = (CONTINUED if continued else 0) | (BOS if bos and not pages else 0)
        granule = granules[ends[-1]] if ends else -1
        pages.append(PageSpec(serial, sequence, granule, flags, [s[0] for s in chunk], b"".join(s[1] for s in chunk), ends))
        position += count
        sequence += 1
        if position >= len(segments):
            break
    if eos:
        pages[-1].flags |= EOS
    return pages


class Writer:
    """Appends pages and other bytes, and records each as the analyzer's page list must show it."""

    def __init__(self):
        self.data = bytearray()
        self.records = []

    def write(self, spec, crc=None, cut=None):
        data, computed = page(spec.serial, spec.sequence, spec.granule, spec.flags, spec.lacing, spec.body, crc if crc is not None else spec.crc)
        record = {
            "name": "Page", "offset": len(self.data), "length": len(data), "serial": spec.serial, "sequence": spec.sequence,
            "granule": spec.granule, "flags": spec.flags, "crc_ok": crc is None and spec.crc is None,
        }
        if cut is not None:
            data = data[:cut]
            record["length"] = cut
            record["truncated"] = True
        self.records.append(record)
        self.data += data
        return record

    def junk(self, data):
        self.records.append({"name": "Gap", "offset": len(self.data), "length": len(data)})
        self.data += data

    def raw(self, data):
        """Bytes that are not part of the page list, such as a tag."""
        self.data += data


def read_pages(data, start=0, end=None):
    """The pages of well-formed Ogg data, in file order."""
    end = len(data) if end is None else end
    pages, position = [], start
    while position + 27 <= end and data[position:position + 4] == b"OggS":
        version, flags, granule, serial, sequence, crc, count = struct.unpack_from("<BBqIIIB", data, position + 4)
        lacing = list(data[position + 27:position + 27 + count])
        length = 27 + count + sum(lacing)
        pages.append({"offset": position, "length": length, "flags": flags, "granule": granule, "serial": serial, "sequence": sequence,
                      "crc": crc, "lacing": lacing, "body": position + 27 + count})
        position += length
    return pages, position


def stream_packets(data, pages):
    """Per serial number in order of first appearance: its pages and its packets, each packet as
    (offset of its first byte, bytes, ranges, index of the page it ends on)."""
    streams = {}
    for index, p in enumerate(pages):
        s = streams.setdefault(p["serial"], {"pages": [], "packets": [], "pending": None})
        s["pages"].append(index)
        at = p["body"]
        for value in p["lacing"]:
            if s["pending"] is None:
                s["pending"] = (bytearray(), [])
            buffer, ranges = s["pending"]
            buffer += data[at:at + value]
            if value:
                if ranges and ranges[-1][0] + ranges[-1][1] == at:
                    ranges[-1] = (ranges[-1][0], ranges[-1][1] + value)
                else:
                    ranges.append((at, value))
            at += value
            if value < 255:
                offset = ranges[0][0] if ranges else at
                s["packets"].append({"offset": offset, "data": bytes(buffer), "ranges": ranges, "page": index})
                s["pending"] = None
    return streams


# --- Vorbis comments ---------------------------------------------------------------------------


def vorbis_comment(vendor, comments):
    out = struct.pack("<I", len(vendor)) + vendor + struct.pack("<I", len(comments))
    for comment in comments:
        out += struct.pack("<I", len(comment)) + comment
    return out


def read_comment(data):
    """The vendor string and comments of a Vorbis comment, as far as they fit, decoded as UTF-8 with
    replacement characters."""
    if len(data) < 4:
        return None
    length = struct.unpack_from("<I", data, 0)[0]
    vendor = data[4:4 + length].decode("utf-8", "replace")
    comments, position = [], 4 + length
    if position + 4 > len(data):
        return {"vendor": vendor, "comments": comments}
    count = struct.unpack_from("<I", data, position)[0]
    position += 4
    for _ in range(count):
        if position + 4 > len(data):
            break
        length = struct.unpack_from("<I", data, position)[0]
        position += 4
        comments.append(data[position:position + length].decode("utf-8", "replace"))
        position += length
        if position > len(data):
            break
    return {"vendor": vendor, "comments": comments}


# --- Speex -----------------------------------------------------------------------------------

NB_BITS = [5, 43, 119, 160, 220, 300, 364, 492, 79]
WB_BITS = [4, 36, 112, 192, 352, -1, -1, -1]
UWB_BITS = [4, 36, -1, -1, -1, -1, -1, -1]
INBAND_BITS = [1, 1, 4, 4, 4, 4, 4, 4, 8, 8, 16, 16, 32, 32, 64, 64]
MODE_NAMES = ["Narrowband", "Wideband", "Ultra-wideband"]
ENCODER_LOOKAHEAD = [40, 143, 349]
DECODER_LOOKAHEAD = [40, 80, 160]


def speex_header(rate, mode, channels=1, bitrate=-1, frame_size=None, vbr=0, frames_per_packet=1, extra_headers=0,
                 version=b"1.2.1", version_id=1, header_size=80, bitstream=4, reserved=(0, 0)):
    return (b"Speex   " + version.ljust(20, b"\0") + struct.pack(
        "<13i", version_id, header_size, rate, mode, bitstream, channels, bitrate,
        frame_size if frame_size is not None else 160 << mode, vbr, frames_per_packet, extra_headers, reserved[0], reserved[1]))


class BitWriter:
    def __init__(self):
        self.bits = []

    def put(self, value, count):
        self.bits.extend((value >> (count - 1 - i)) & 1 for i in range(count))

    def terminate(self):
        """speex_bits_insert_terminator: a 0 bit, then 1 bits up to the byte boundary."""
        if len(self.bits) % 8:
            self.put(0, 1)
            while len(self.bits) % 8:
                self.put(1, 1)

    def bytes(self):
        bits = self.bits + [0] * (-len(self.bits) % 8)
        return bytes(int("".join(map(str, bits[i:i + 8])), 2) for i in range(0, len(bits), 8))


def speex_frame(writer, nb, wb=None, uwb=None, inband=()):
    """One frame: in-band requests (ID, payload bits), the narrowband layer, then the wideband and
    ultra-wideband layers where given; coded data is zero bits."""
    for request in inband:
        writer.put(0, 1)
        writer.put(14, 4)
        writer.put(request, 4)
        writer.put(0, INBAND_BITS[request])
    writer.put(0, 1)
    writer.put(nb, 4)
    writer.put(0, NB_BITS[nb] - 5)
    for submode, table in ((wb, WB_BITS), (uwb, UWB_BITS)):
        if submode is not None:
            writer.put(1, 1)
            writer.put(submode, 3)
            writer.put(0, table[submode] - 4)


def speex_packet(frames, terminate=True):
    """frames: (nb, wb, uwb, inband) tuples, the last two optional."""
    writer = BitWriter()
    for frame in frames:
        frame = tuple(frame) + (None,) * (4 - len(frame))
        speex_frame(writer, frame[0], frame[1], frame[2], frame[3] or ())
    if terminate:
        writer.terminate()
    return writer.bytes()


def speex_header_fields(header):
    """The Speex header's fields as the report shows them (speex_header.h), or None for a packet
    shorter than 80 bytes."""
    if len(header) < 80:
        return None
    version = header[8:28].split(b"\0")[0].decode("latin-1")
    (version_id, size, rate, mode, bitstream, channels, bitrate, frame_size, vbr, nframes, extra, reserved1,
     reserved2) = struct.unpack_from("<13i", header, 28)
    return {
        "Speex String": "Speex   ", "Speex Version": version, "Version ID": str(version_id), "Header Size": plural(size, "byte", "bytes"),
        "Sample Rate": f"{rate} Hz", "Mode": MODE_NAMES[mode] if 0 <= mode <= 2 else f"{mode} (undefined)",
        "Mode Bitstream Version": str(bitstream), "Channels": str(channels),
        "Bitrate": "-1 (not given)" if bitrate == -1 else f"{bitrate} bps", "Frame Size": plural(frame_size, "sample", "samples"),
        "IsVbr": ("true" if vbr == 1 else "false") if vbr in (0, 1) else str(vbr), "Frames Per Packet": str(nframes),
        "Extra Headers": str(extra), "Reserved 1": str(reserved1), "Reserved 2": str(reserved2),
    }


def walk(packet, mode, max_frames):
    """The frames libspeex decodes from a packet: [(prefix bits, nb, wb, uwb)], wb and uwb None where
    the frame has no such layer, then why the walk stopped ("end", "terminator", "limit",
    "invalid", "overrun"), in-band requests and stereo requests."""
    total = len(packet) * 8
    position = 0

    def bit(p):
        return (packet[p >> 3] >> (7 - (p & 7))) & 1 if p < total else 0

    def read(count):
        nonlocal position
        value = 0
        for i in range(count):
            value = (value << 1) | bit(position + i)
        position += count
        return value

    frames, inband, stereo = [], 0, 0
    while True:
        start = position
        if len(frames) == max_frames:
            return frames, "limit", inband, stereo
        while True:
            if total - position < 5:
                return frames, "end", inband, stereo
            if read(1):
                for layer in range(2):
                    submode = read(3)
                    if WB_BITS[submode] < 0:
                        return frames, "invalid", inband, stereo
                    position += WB_BITS[submode] - 4
                    if layer == 0 and total - position < 5:
                        return frames, "end", inband, stereo
                    if not read(1):
                        break
                else:
                    return frames, "invalid", inband, stereo
            if total - position < 4:
                return frames, "end", inband, stereo
            m = read(4)
            if m == 15:
                return frames, "terminator", inband, stereo
            if m == 14:
                request = read(4)
                inband += 1
                stereo += request == 9
                position += INBAND_BITS[request]
            elif m == 13:
                size = read(4)
                inband += 1
                position += 5 + 8 * size
            elif m > 8:
                return frames, "invalid", inband, stereo
            else:
                break
            if position > total:
                return frames, "overrun", inband, stereo
        prefix = position - 5 - start
        position += NB_BITS[m] - 5
        layers = []
        for table, present in ((WB_BITS, mode >= 1), (UWB_BITS, mode >= 2)):
            submode = None
            if present and position <= total and total - position > 0 and bit(position):
                read(1)
                submode = read(3)
                if table[submode] < 0:
                    return frames, "invalid", inband, stereo
                position += table[submode] - 4
            layers.append(submode)
        if position > total:
            return frames, "overrun", inband, stereo
        frames.append((prefix, m, layers[0], layers[1]))


def frame_bits(frame):
    prefix, nb, wb, uwb = frame
    return prefix + NB_BITS[nb] + (WB_BITS[wb] if wb is not None else 0) + (UWB_BITS[uwb] if uwb is not None else 0)


def speexdec_bitrate(frame, mode, rate):
    """The bitrate speexdec -V prints for a frame (SPEEX_GET_BITRATE after decoding it): each layer's
    bits per frame over its own frame duration, an absent wideband layer counted as its 4 header
    bits, at the sampling rate the decoder was set to, halved for each lower band."""
    _, nb, wb, uwb = frame
    rates = [rate >> mode, rate >> (mode - 1) if mode >= 1 else 0, rate]
    total = rates[0] * NB_BITS[nb] // 160
    if mode >= 1:
        total += rates[1] * (WB_BITS[wb] if wb is not None else 4) // 320
    if mode >= 2:
        total += rate * (UWB_BITS[uwb] if uwb is not None else 4) // 640
    return total


def fmt_decimal(value, digits):
    """A decimal as the report shows it: rounded half to even."""
    from decimal import Decimal, ROUND_HALF_EVEN
    return str(Decimal(repr(value)).quantize(Decimal(1).scaleb(-digits), rounding=ROUND_HALF_EVEN)) if digits else str(round(value))


def plural(count, singular, plural_form):
    return f"{count} {singular if count == 1 else plural_form}"


def share(count, total, singular, plural_form):
    return f"{plural(count, singular, plural_form)} ({fmt_decimal(count * 100.0 / total, 1)}%)"


def speex_expectations(header, packets, positions, gaps=()):
    """What the analyzer reports for a Speex stream.

    header: the header packet's bytes; packets: the audio packets as (offset, bytes, ranges);
    positions: (audio packet index, granule, is end of stream) for each packet that carries a
    granule position; gaps: the indexes of audio packets that follow packets the container lost.
    """
    if len(header) >= 80:
        rate, mode, _, channels = struct.unpack_from("<4i", header, 36)
        nframes = struct.unpack_from("<i", header, 64)[0]
    else:
        rate, mode, channels, nframes = 0, -1, 0, 1
    valid_mode = 0 <= mode <= 2
    frame_size = 160 << mode if valid_mode else 0
    decoded_frames = walked = min(max(nframes, 1), 64)
    per_packet = frame_size * decoded_frames
    has_time = valid_mode and rate > 0

    frames, sizes, counts = [], {}, [{}, {}, {}]
    inband = stereo = total_frames = total_bytes = 0
    for offset, data, ranges in packets:
        sizes[len(data)] = sizes.get(len(data), 0) + 1
        total_bytes += len(data)
        if not data or not valid_mode:
            frames.append([])
            continue
        found, end, requests, stereo_requests = walk(data, mode, walked + 1)
        found = found[:walked]
        frames.append(found)
        inband += requests
        stereo += stereo_requests
        total_frames += len(found)
        for frame in found:
            for layer, value in enumerate(frame[1:]):
                key = value if value is not None else "absent"
                counts[layer][key] = counts[layer].get(key, 0) + 1

    coded = len(packets) * per_packet
    first_delay = delay = None
    after_gap = False
    last = None
    previous = -1
    for index, granule, is_end in (positions if has_time else []):
        after_gap |= any(previous < g <= index for g in gaps)
        previous = index
        at = (index + 1) * per_packet
        d = at - granule
        if first_delay is None:
            # Only the last position: libspeex's lookahead is assumed.
            first_delay = delay = ENCODER_LOOKAHEAD[mode] if is_end else d
        elif after_gap:
            delay = d
        after_gap = False
        last = (at, granule, is_end)
    trim = 0
    if last and delay is not None and last[2]:
        trim = max(0, last[0] - delay - last[1])
    samples = min(max(coded - max(0, first_delay or 0) - trim, 0), coded) if has_time else 0

    statistics = {"Packets": str(len(packets)), "Frames": str(total_frames)}
    if has_time and packets:
        bitrate = lambda size: size * 8.0 * rate / per_packet / 1000
        statistics["Coded Samples"] = plural(coded, "sample", "samples")
        if first_delay is not None:
            statistics["Encoder Delay"] = plural(first_delay, "sample", "samples")
            statistics["End Trimming"] = plural(trim, "sample", "samples")
        statistics["Samples"] = plural(samples, "sample", "samples")
        statistics["Duration"] = fmt_decimal(samples / rate, 3) + " s"
        statistics["IsVbr"] = "true" if len(sizes) > 1 else "false"
        statistics["Average Bitrate"] = fmt_decimal(total_bytes * 8.0 * rate / coded / 1000, 1) + " kbps"
        statistics["Minimum Bitrate"] = fmt_decimal(bitrate(min(sizes)), 1) + " kbps"
        statistics["Maximum Bitrate"] = fmt_decimal(bitrate(max(sizes)), 1) + " kbps"
    if packets:
        statistics["Packet Sizes"] = plural(len(sizes), "size", "sizes")
        for size in sorted(sizes):
            text = share(sizes[size], len(packets), "packet", "packets")
            if has_time:
                text += ", " + fmt_decimal(size * 8.0 * rate / per_packet / 1000, 1) + " kbps"
            statistics[f"Packet Sizes/{size} bytes"] = text
    if valid_mode and total_frames:
        names = [("Narrowband Submodes", NB_BITS, None), ("Wideband Submodes", WB_BITS, "No wideband layer"),
                 ("Ultra-Wideband Submodes", UWB_BITS, "No ultra-wideband layer")]
        for layer, (name, table, absent) in enumerate(names[:mode + 1]):
            statistics[name] = plural(len(counts[layer]), "submode", "submodes")
            for submode in sorted(k for k in counts[layer] if k != "absent"):
                statistics[f"{name}/Submode {submode} ({table[submode]} bits)"] = share(counts[layer][submode], total_frames, "frame", "frames")
            if absent and "absent" in counts[layer]:
                statistics[f"{name}/{absent}"] = share(counts[layer]["absent"], total_frames, "frame", "frames")
    if inband:
        statistics["In-band Requests"] = str(inband)
        statistics["Stereo Parameters"] = str(stereo)

    info = None if len(header) < 80 else {
        "codec": f"Speex {MODE_NAMES[mode].lower()}" if valid_mode else "Speex",
        "sample_rate": max(0, rate),
        "channels": min(max(channels, 1), 2),
        # Speex codes one channel, or two as intensity stereo, which decodes to left and right.
        "channel_layout": ["front center"] if channels <= 1 else ["front left", "front right"],
        "samples": samples,
        "duration": samples / rate if has_time else 0,
        "average_bitrate": total_bytes * 8.0 * rate / coded / 1000 if has_time and coded else None,
        "is_vbr": len(sizes) > 1,
    }
    return {
        "packets": [[offset, len(data)] + ([list(r) for r in ranges] if len(ranges) > 1 else []) for offset, data, ranges in packets],
        "frames": [[[f[1], f[2], f[3], f[0]] for f in found] for found in frames],
        "statistics": statistics if packets else None,
        "stream_info": info,
    }


def speexdec_samples(pages, packet_frames, header, packet_count_before=0):
    """The samples speexdec 1.2.1 writes for a stream, per channel (speexdec.c main): for each page,
    skip_samples = page_nb_packets * frame_size * nframes - (granule - last granule), negated on
    the end-of-stream page; the first audio packet of a page loses skip_samples + the decoder's
    lookahead where that is positive, and the last packet of the end-of-stream page is cut to
    nframes * frame_size + skip_samples + lookahead.

    pages: (granule, number of packets that end on the page, end of stream) of the stream's pages;
    packet_frames: per packet of the stream, header packets included, the frames speexdec decodes
    (None for header packets)."""
    mode = struct.unpack_from("<i", header, 40)[0]
    nframes = max(1, struct.unpack_from("<i", header, 64)[0])
    frame_size = 160 << mode
    lookahead = DECODER_LOOKAHEAD[mode]
    total, last_granule, index = 0, 0, 0
    for granule, count, eos in pages:
        skip = count * frame_size * nframes - (granule - last_granule) if granule > 0 else 0
        if granule > 0 and eos:
            skip = -skip
        last_granule = granule
        packet_no = 0
        for _ in range(count):
            frames = packet_frames[index]
            index += 1
            if frames is None:
                continue
            packet_no += 1
            for j in range(min(frames, nframes)):
                size = frame_size
                if packet_no == 1 and j == 0 and skip > 0:
                    size -= skip + lookahead
                if packet_no == count and skip < 0:
                    size = min(max(nframes * frame_size + skip + lookahead - j * frame_size, 0), frame_size)
                total += max(size, 0)
    return total
