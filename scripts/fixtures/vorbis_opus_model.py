"""Vorbis and Opus packets, written and read for the Ogg fixtures.

Writing: BitWriter packs fields least significant bit first, as Vorbis does (Vorbis I specification,
section 2.1.4); the Vorbis helpers build identification, comment and setup headers and audio packets
of a minimal codec setup (one codebook, a floor 1 without partitions, a residue without passes) whose
packets code silence, so libvorbis decodes them; the Opus helpers build OpusHead and OpusTags
(RFC 7845, section 5) and packets of any TOC configuration and frame packing (RFC 6716, section 3),
with self-delimited framing for multistream (appendix B).

Reading: vorbis_setup() walks a setup header as section 4.2.4 lays it out, vorbis_expectations() and
opus_expectations() turn a stream's packets and granule positions into what the analyzer must
report, written independently of the analyzer from the specifications. Standard library only.
"""

import math
import struct

import ogg_model as m

# The speakers of Vorbis I section 4.3.9's channel order, which Opus's mapping family 1 shares (RFC
# 7845, section 5.1.1.2), as the analyzer names them: the lone surround pair of 4 to 6 channels is
# the back pair, and with a rear pair as well the surround pair is the side pair.
VORBIS_SPEAKERS = {
    1: ["front center"],
    2: ["front left", "front right"],
    3: ["front left", "front center", "front right"],
    4: ["front left", "front right", "back left", "back right"],
    5: ["front left", "front center", "front right", "back left", "back right"],
    6: ["front left", "front center", "front right", "back left", "back right", "LFE"],
    7: ["front left", "front center", "front right", "side left", "side right", "back center", "LFE"],
    8: ["front left", "front center", "front right", "side left", "side right", "back left", "back right", "LFE"],
}


def opus_speakers(head):
    """The speakers of families 0 and 1; none for ambisonics and family 255, which name none."""
    family, channels = head[18], head[9]
    if (family == 0 and channels in (1, 2)) or (family == 1 and 1 <= channels <= 8):
        return VORBIS_SPEAKERS[channels]
    return []

# --- Bits -----------------------------------------------------------------------------------------


class BitWriter:
    """Least significant bit first: a value's low bit goes to the lowest free bit of the byte."""

    def __init__(self):
        self.bits = []

    def put(self, value, count):
        self.bits.extend((value >> i) & 1 for i in range(count))

    def bytes(self):
        bits = self.bits + [0] * (-len(self.bits) % 8)
        return bytes(sum(bits[i + j] << j for j in range(8)) for i in range(0, len(bits), 8))


class BitReader:
    def __init__(self, data, position=0):
        self.data, self.position = data, position

    @property
    def length(self):
        return len(self.data) * 8

    def read(self, count):
        if self.position + count > self.length:
            raise EOFError
        value = 0
        for i in range(count):
            p = self.position + i
            value |= ((self.data[p >> 3] >> (p & 7)) & 1) << i
        self.position += count
        return value


def fmt(value, digits):
    """A decimal as NodeValue.Decimal shows it: .NET's fixed-point format rounds the double's exact
    value, ties to even."""
    from decimal import Decimal, ROUND_HALF_EVEN
    return str(Decimal(value).quantize(Decimal(1).scaleb(-digits), rounding=ROUND_HALF_EVEN))


def ilog(value):
    return 0 if value <= 0 else value.bit_length()


# --- Vorbis headers -------------------------------------------------------------------------------


def vorbis_identification(channels, rate, blocksizes=(8, 11), bitrates=(0, 0, 0), version=0, framing=1):
    """Section 4.2.2; blocksizes as exponents of two."""
    return (b"\x01vorbis" + struct.pack("<IBIiii", version, channels, rate, *bitrates)
            + bytes([blocksizes[0] | (blocksizes[1] << 4), framing]))


def vorbis_comment_header(vendor, comments, framing=True):
    return b"\x03vorbis" + m.vorbis_comment(vendor, comments) + (b"\x01" if framing else b"")


def float32_pack(value):
    """The inverse of float32_unpack (section 9.2.2) for a value with an exact 21-bit mantissa."""
    if value == 0:
        return 0
    sign = 0x80000000 if value < 0 else 0
    mantissa, exponent = abs(value), 0
    while mantissa != int(mantissa) and exponent > -700:
        mantissa *= 2
        exponent -= 1
    return sign | ((exponent + 788) << 21) | int(mantissa)


def codebook(w, dimensions, lengths, lookup=None, ordered=False, sparse=None, sync=0x564342):
    """Section 3.2.1: lengths per entry (0 for an unused entry of a sparse book); lookup as
    (type, minimum, delta, value bits, sequence, values)."""
    w.put(sync, 24)
    w.put(dimensions, 16)
    w.put(len(lengths), 24)
    w.put(1 if ordered else 0, 1)
    if ordered:
        w.put(lengths[0] - 1, 5)
        current, length = 0, lengths[0]
        while current < len(lengths):
            number = sum(1 for l in lengths if l == length)
            w.put(number, ilog(len(lengths) - current))
            current += number
            length += 1
    else:
        sparse = any(l == 0 for l in lengths) if sparse is None else sparse
        w.put(1 if sparse else 0, 1)
        for l in lengths:
            if sparse:
                w.put(1 if l else 0, 1)
                if not l:
                    continue
            w.put(l - 1, 5)
    if lookup is None:
        w.put(0, 4)
    else:
        kind, minimum, delta, bits, sequence, values = lookup
        w.put(kind, 4)
        w.put(float32_pack(minimum), 32)
        w.put(float32_pack(delta), 32)
        w.put(bits - 1, 4)
        w.put(sequence, 1)
        for v in values:
            w.put(v, bits)


def vorbis_setup(channels, modes=((0, 0), (1, 0)), coupling=None, books=None, floors=None, residues=None, mappings=None, framing=1, extra=b""):
    """A setup header (section 4.2.4) of the minimal codec: codebook 0 of two entries of length 1
    with a lookup of type 1, floor 0 of type 1 without partitions, residue 0 of type 0 without
    passes, one mapping, and the modes as (blockflag, mapping). Any part may be replaced by a
    function that writes it."""
    w = BitWriter()
    for byte in b"\x05vorbis":
        w.put(byte, 8)
    if books:
        books(w)
    else:
        w.put(0, 8)
        codebook(w, 1, [1, 1], lookup=(1, 0.0, 1.0, 1, 0, [0, 1]))
    w.put(0, 6)
    w.put(0, 16)
    if floors:
        floors(w)
    else:
        w.put(0, 6)
        w.put(1, 16)       # floor type 1
        w.put(0, 5)        # partitions
        w.put(0, 2)        # multiplier 1
        w.put(4, 4)        # range bits
    if residues:
        residues(w)
    else:
        w.put(0, 6)
        w.put(0, 16)       # residue type 0
        w.put(0, 24)
        w.put(0, 24)
        w.put(0, 24)       # partition size 1
        w.put(0, 6)        # one classification
        w.put(0, 8)        # classbook 0
        w.put(0, 3)        # cascade 0
        w.put(0, 1)
    if mappings:
        mappings(w)
    else:
        w.put(0, 6)
        w.put(0, 16)
        w.put(0, 1)        # one submap
        if coupling:
            w.put(1, 1)
            w.put(len(coupling) - 1, 8)
            for magnitude, angle in coupling:
                w.put(magnitude, ilog(channels - 1))
                w.put(angle, ilog(channels - 1))
        else:
            w.put(0, 1)
        w.put(0, 2)
        w.put(0, 8)
        w.put(0, 8)
        w.put(0, 8)
    w.put(len(modes) - 1, 6)
    for blockflag, mapping in modes:
        w.put(blockflag, 1)
        w.put(0, 16)
        w.put(0, 16)
        w.put(mapping, 8)
    w.put(framing, 1)
    return w.bytes() + extra


def vorbis_audio(mode, mode_count, long_block, previous=1, following=1, channels=1, extra=b""):
    """An audio packet (section 4.3.1) of the minimal codec: type 0, the mode, the window flags of a
    long block, then each channel's floor 1 'nonzero' bit 0, which codes silence."""
    w = BitWriter()
    w.put(0, 1)
    w.put(mode, ilog(mode_count - 1))
    if long_block:
        w.put(previous, 1)
        w.put(following, 1)
    for _ in range(channels):
        w.put(0, 1)
    return w.bytes() + extra


# --- Vorbis reading -------------------------------------------------------------------------------

VORBIS_LIBRARIES = {
    "Xiph.Org libVorbis I 20200704 (Reducing Environment)": "1.3.7",
}

# The channel order of Vorbis I, section 4.3.9, which Opus mapping family 1 uses too (RFC 7845, section 5.1.1.2), with the
# speakers named as the analyzer names them (ChannelLayouts.Name).
VORBIS_LAYOUTS = {
    1: ("Mono", ["front center"]), 2: ("Stereo", ["front left", "front right"]), 3: ("3.0", ["front left", "front center", "front right"]),
    4: ("Quadraphonic", ["front left", "front right", "back left", "back right"]),
    5: ("5.0", ["front left", "front center", "front right", "back left", "back right"]),
    6: ("5.1", ["front left", "front center", "front right", "back left", "back right", "LFE"]),
    7: ("6.1", ["front left", "front center", "front right", "side left", "side right", "back center", "LFE"]),
    8: ("7.1", ["front left", "front center", "front right", "side left", "side right", "back left", "back right", "LFE"]),
}


def vorbis_identification_fields(packet):
    """The identification header's fields as the report shows them, or None for a short one."""
    if len(packet) < 30:
        return None
    version, channels, rate, maximum, nominal, minimum = struct.unpack_from("<IBIiii", packet, 7)
    b0, b1 = packet[28] & 15, packet[28] >> 4

    def bitrate(v):
        return f"{v} bps" if v > 0 else f"{v} (not set)"

    if maximum > 0 and nominal > 0 and minimum > 0 and maximum == nominal == minimum:
        hint = "All three equal: a fixed or tightly bounded, nearly fixed bitrate"
    elif maximum > 0 or minimum > 0:
        hint = "A maximum or minimum: VBR within those limits"
    else:
        hint = "Only the nominal: VBR or ABR that averages it" if nominal > 0 else "None set: the encoder gives no hint"
    fields = {"Packet Type": "1", "Vorbis String": "vorbis", "Vorbis Version": str(version), "Channels": str(channels)}
    if channels in VORBIS_LAYOUTS:
        name, speakers = VORBIS_LAYOUTS[channels]
        fields["Channel Layout"] = f"{name}: {', '.join(speakers)}"
    fields.update({
        "Sample Rate": f"{rate} Hz", "Bitrate Maximum": bitrate(maximum), "Bitrate Nominal": bitrate(nominal), "Bitrate Minimum": bitrate(minimum),
        "Bitrate Hint": hint, "Blocksize 0": f"{1 << b0} samples", "Blocksize 1": f"{1 << b1} samples",
        "Framing Bit": "true" if packet[29] & 1 else "false",
    })
    return fields


def plural(count, singular, plural_form):
    return m.plural(count, singular, plural_form)


def read_setup(packet, channels):
    """Walks a setup header (section 4.2.4) and returns the values the report gives its groups,
    the modes' block flags (None when the walk stops before them) and the finding codes it raises."""
    r = BitReader(packet, 56)
    groups, problems, modes = {}, [], None
    books, floors, residues, mappings = [], 0, 0, 0
    try:
        count = r.read(8) + 1
        for index in range(count):
            if r.read(24) != 0x564342:
                problems.append("vorbis.setup-invalid")
                raise StopIteration
            dimensions, entries = r.read(16), r.read(24)
            if ilog(dimensions) + ilog(entries) > 24:
                problems.append("vorbis.codebook-too-large")
            lengths = []
            if r.read(1) == 0:
                sparse = r.read(1)
                for _ in range(entries):
                    if sparse and not r.read(1):
                        continue
                    lengths.append(r.read(5) + 1)
            else:
                current, length = 0, r.read(5) + 1
                while current < entries:
                    number = r.read(ilog(entries - current))
                    current += number
                    if current > entries:
                        problems.append("vorbis.setup-invalid")
                        raise StopIteration
                    lengths += [length] * number
                    length += 1
            if any(l > 32 for l in lengths):
                problems.append("vorbis.codebook-invalid")
            else:
                kraft = sum(1 << (32 - l) for l in lengths)
                if kraft > 1 << 32:
                    problems.append("vorbis.codebook-overpopulated")
                elif kraft < 1 << 32 and lengths and not (len(lengths) == 1 and lengths[0] == 1):
                    problems.append("vorbis.codebook-underpopulated")
            lookup = r.read(4)
            if lookup in (1, 2):
                r.read(32)
                r.read(32)
                bits = r.read(4) + 1
                r.read(1)
                if lookup == 1:
                    values = 0
                    if dimensions > 0:
                        values = int(math.floor(entries ** (1.0 / dimensions)))
                        while values > 0 and values ** dimensions > entries:
                            values -= 1
                        while (values + 1) ** dimensions <= entries:
                            values += 1
                    else:
                        problems.append("vorbis.setup-invalid")
                else:
                    values = entries * dimensions
                r.read(0)
                if r.position + values * bits > r.length:
                    raise EOFError
                r.position += values * bits
            elif lookup:
                problems.append("vorbis.setup-invalid")
                raise StopIteration
            books.append((dimensions, entries, lookup))
        groups["Codebooks"] = plural(count, "codebook", "codebooks")

        times = r.read(6) + 1
        for _ in range(times):
            if r.read(16):
                problems.append("vorbis.setup-invalid")
        groups["Time Domain Transforms"] = plural(times, "placeholder", "placeholders")

        floors = r.read(6) + 1
        types = set()
        for _ in range(floors):
            kind = r.read(16)
            if kind == 0:
                order, rate, bark = r.read(8), r.read(16), r.read(16)
                r.read(6)
                r.read(8)
                problems += ["vorbis.setup-invalid"] * sum(1 for v in (order, rate, bark) if v == 0)
                for _ in range(r.read(4) + 1):
                    book = r.read(8)
                    if book >= len(books) or books[book][2] == 0 or books[book][0] < 1:
                        problems.append("vorbis.setup-invalid")
            elif kind == 1:
                partitions = r.read(5)
                classes = [r.read(4) for _ in range(partitions)]
                dims = []
                for _ in range(max(classes, default=-1) + 1):
                    dims.append(r.read(3) + 1)
                    subclasses = r.read(2)
                    if subclasses and r.read(8) >= len(books):
                        problems.append("vorbis.setup-invalid")
                    for _ in range(1 << subclasses):
                        if r.read(8) - 1 >= len(books):
                            problems.append("vorbis.setup-invalid")
                r.read(2)
                bits = r.read(4)
                xs = [0, 1 << bits] + [r.read(bits) for c in classes for _ in range(dims[c])]
                if len(xs) - 2 > 63 or len(set(xs)) != len(xs):
                    problems.append("vorbis.setup-invalid")
            else:
                problems.append("vorbis.setup-invalid")
                raise StopIteration
            types.add(kind)
        groups["Floors"] = f"{plural(floors, 'floor', 'floors')}, type {' and '.join(map(str, sorted(types)))}"

        residues = r.read(6) + 1
        types = set()
        for _ in range(residues):
            kind = r.read(16)
            if kind > 2:
                problems.append("vorbis.setup-invalid")
                raise StopIteration
            types.add(kind)
            r.read(24)
            r.read(24)
            r.read(24)
            classifications = r.read(6) + 1
            classbook = r.read(8)
            if classbook >= len(books):
                problems.append("vorbis.setup-invalid")
            elif books[classbook][0] < 1 or classifications ** books[classbook][0] > books[classbook][1]:
                problems.append("vorbis.setup-invalid")
            cascades = []
            for _ in range(classifications):
                low = r.read(3)
                cascades.append(low | (r.read(5) << 3 if r.read(1) else 0))
            for cascade in cascades:
                for bit in range(8):
                    if cascade >> bit & 1:
                        book = r.read(8)
                        if book >= len(books) or books[book][2] == 0:
                            problems.append("vorbis.setup-invalid")
        groups["Residues"] = f"{plural(residues, 'residue', 'residues')}, type {' and '.join(map(str, sorted(types)))}"

        mappings = r.read(6) + 1
        steps_total = 0
        for _ in range(mappings):
            if r.read(16) != 0:
                problems.append("vorbis.setup-invalid")
                raise StopIteration
            submaps = r.read(4) + 1 if r.read(1) else 1
            if r.read(1):
                if channels < 1:
                    problems.append("vorbis.setup-invalid")
                    raise StopIteration
                steps = r.read(8) + 1
                steps_total += steps
                for _ in range(steps):
                    magnitude, angle = r.read(ilog(channels - 1)), r.read(ilog(channels - 1))
                    if magnitude == angle or magnitude >= channels or angle >= channels:
                        problems.append("vorbis.setup-invalid")
            if r.read(2):
                problems.append("vorbis.setup-invalid")
            if submaps > 1:
                for _ in range(channels):
                    if r.read(4) >= submaps:
                        problems.append("vorbis.setup-invalid")
            for _ in range(submaps):
                r.read(8)
                if r.read(8) >= floors:
                    problems.append("vorbis.setup-invalid")
                if r.read(8) >= residues:
                    problems.append("vorbis.setup-invalid")
        groups["Mappings"] = f"{plural(mappings, 'mapping', 'mappings')}, {'no coupling' if not steps_total else plural(steps_total, 'coupling step', 'coupling steps')}"

        count = r.read(6) + 1
        flags = []
        for _ in range(count):
            flags.append(r.read(1))
            window, transform, mapping = r.read(16), r.read(16), r.read(8)
            if window or transform:
                problems.append("vorbis.setup-invalid")
            if mapping >= mappings:
                problems.append("vorbis.setup-invalid")
        modes = flags
        groups["Modes"] = f"{plural(count, 'mode', 'modes')}: {sum(flags)} long, {count - sum(flags)} short"
        if r.read(1) != 1:
            problems.append("vorbis.framing-missing")
        end = (r.position + 7) // 8
        if end < len(packet):
            problems.append("vorbis.header-extra-data")
        value = f"{plural(len(books), 'codebook', 'codebooks')}, {plural(floors, 'floor', 'floors')}, {plural(residues, 'residue', 'residues')}, {plural(mappings, 'mapping', 'mappings')}, {plural(count, 'mode', 'modes')}"
    except EOFError:
        problems.append("vorbis.setup-truncated")
        value = "cut short"
    except StopIteration:
        value = "not readable to the end"
    return {"value": value, "groups": groups, "modes": modes, "problems": problems}


def vorbis_expectations(ident, setup, packets, positions, gaps=(), layout_ok=True):
    """What the analyzer reports for a Vorbis stream's audio packets.

    ident: the identification header's bytes; setup: read_setup()'s result, or None; packets: the
    audio packets as (offset, bytes, ranges); positions: (audio packet index, granule, is end of
    stream) for each packet that carries a granule position; gaps: the indexes of audio packets
    that follow packets the container lost."""
    fields = struct.unpack_from("<IBI", ident, 7) if len(ident) >= 30 else None
    version, channels, rate = fields if fields else (1, 0, 0)
    b0, b1 = (1 << (ident[28] & 15), 1 << (ident[28] >> 4)) if fields else (0, 0)
    usable = fields is not None and version == 0 and 6 <= ident[28] & 15 <= 13 and 6 <= ident[28] >> 4 <= 13 and (ident[28] & 15) <= (ident[28] >> 4)
    modes = setup["modes"] if setup and setup["modes"] else None
    has_time = usable and 0 < rate <= 2 ** 31 - 1 and modes is not None
    mode_bits = ilog(len(modes) - 1) if modes else 0

    records, findings = [], []
    decoded, previous, blocks, first_returned, second_returned, last_returned = 0, None, 0, 0, 0, 0
    sizes, long_bytes, short_bytes, long_sizes, short_sizes = set(), 0, 0, [], []
    mode_counts = {}
    after_gap = False
    first_offset = offset = None
    first_position_packet = None
    last = None
    for index, (offset_in_file, data, ranges) in enumerate(packets):
        sizes.add(len(data))
        after_gap |= index in gaps
        kind = None
        if not data:
            kind = "empty"
            findings.append("vorbis.empty-packet")
        elif data[0] & 1:
            kind = "not-audio"
            findings.append("vorbis.not-audio")
        elif not modes or not usable:
            kind = "unread"
        else:
            r = BitReader(data, 1)
            try:
                mode = r.read(mode_bits)
                if mode >= len(modes):
                    raise EOFError
                is_long = modes[mode] == 1
                flags = (r.read(1), r.read(1)) if is_long else (0, 0)
            except EOFError:
                kind = "bad-mode"
                findings.append("vorbis.mode-invalid")
        if kind:
            records.append(kind)
        else:
            records.append(f"{'L' if is_long else 'S'}{mode}")
            size = b1 if is_long else b0
            mode_counts[mode] = mode_counts.get(mode, 0) + 1
            (long_sizes if is_long else short_sizes).append(len(data))
            returned = 0
            if previous is not None:
                p_long, p_flags, p_index = previous
                returned = (b1 if p_long else b0) // 4 + size // 4
                if not after_gap:
                    if is_long and flags[0] != p_long:
                        findings.append("vorbis.window-flag-mismatch")
                    if p_long and p_flags[1] != is_long:
                        findings.append("vorbis.window-flag-mismatch")
            blocks += 1
            if blocks == 1:
                first_returned = returned
            if blocks == 2:
                second_returned = returned
            last_returned = returned
            decoded += returned
            previous = (is_long, flags, index)
        for at, granule, is_end in positions:
            if at != index or not has_time:
                continue
            o = granule - decoded
            if first_offset is None:
                first_offset = offset = max(0, o) if is_end else o
                first_position_packet = index
            elif after_gap:
                offset = o
            elif o != offset and not is_end:
                findings.append("vorbis.granule-inconsistent")
            after_gap = False
            last = (decoded, granule, is_end, last_returned)

    start_trim = 0
    if first_offset:
        if first_position_packet != 1:
            findings.append("vorbis.start-page-layout")
        if first_offset > 0:
            findings.append("vorbis.start-offset")
        else:
            if -first_offset > first_returned + second_returned:
                findings.append("vorbis.start-trimming-large")
            start_trim = min(-first_offset, first_returned + second_returned, decoded)
    trim = 0
    if last and offset is not None and last[2]:
        trim = last[0] + offset - last[1]
        if trim < 0:
            findings.append("vorbis.granule-beyond-packets")
            trim = 0
        elif trim > last[3]:
            # Section A.2 allows trimming within the last packet's samples, and libvorbis strips no more than it has buffered for it.
            findings.append("vorbis.end-trimming-large")
            trim = last[3]
    samples = min(max(decoded - start_trim - trim, 0), decoded) if has_time else 0

    statistics = None
    long_count, short_count = len(long_sizes), len(short_sizes)
    block_count = long_count + short_count
    if packets:
        statistics = {"Packets": str(len(packets))}
        if block_count:
            statistics["Long Blocks"] = m.share(long_count, block_count, "packet", "packets")
            statistics["Short Blocks"] = m.share(short_count, block_count, "packet", "packets")
        if has_time and block_count:
            statistics["Decoded Samples"] = plural(decoded, "sample", "samples")
            if first_offset is not None:
                statistics["Start Offset"] = plural(first_offset, "sample", "samples")
                statistics["Start Trimming"] = plural(start_trim, "sample", "samples")
                statistics["End Trimming"] = plural(trim, "sample", "samples")
            statistics["Samples"] = plural(samples, "sample", "samples")
            statistics["Duration"] = fmt(samples / rate, 3) + " s"
            statistics["IsVbr"] = "true" if len(sizes) > 1 else "false"
            total = sum(len(d) for _, d, _ in packets)
            if decoded:
                statistics["Average Bitrate"] = fmt(total * 8.0 * rate / decoded / 1000, 1) + " kbps"
            rates = [s * 8.0 * rate / (b1 // 2) / 1000 for s in (min(long_sizes), max(long_sizes))] if long_sizes else []
            rates += [s * 8.0 * rate / (b0 // 2) / 1000 for s in (min(short_sizes), max(short_sizes))] if short_sizes else []
            statistics["Minimum Bitrate"] = fmt(min(rates), 1) + " kbps"
            statistics["Maximum Bitrate"] = fmt(max(rates), 1) + " kbps"
        statistics["Packet Sizes"] = plural(len(sizes), "size", "sizes")
        for name, group in (("Long Blocks", long_sizes), ("Short Blocks", short_sizes)):
            if group:
                statistics[f"Packet Sizes/{name}"] = f"{min(group)} to {max(group)} bytes, {m.fmt_decimal(sum(group) / len(group), 1)} on average"
        if modes and block_count:
            statistics["Modes"] = plural(len(mode_counts), "mode used", "modes used")
            for mode in sorted(mode_counts):
                statistics[f"Modes/Mode {mode} ({'long' if modes[mode] else 'short'} block)"] = m.share(mode_counts[mode], block_count, "packet", "packets")

    info = None if fields is None else {
        "codec": "Vorbis", "sample_rate": min(rate, 2 ** 31 - 1), "channels": channels, "channel_layout": VORBIS_SPEAKERS.get(channels, []),
        "samples": samples,
        "duration": samples / rate if has_time else 0,
        "average_bitrate": sum(len(d) for _, d, _ in packets) * 8.0 * rate / decoded / 1000 if has_time and decoded else None,
        "is_vbr": len(sizes) > 1,
    }
    return {
        "packets": [[o, len(d)] + ([list(x) for x in ranges] if len(ranges) > 1 else []) for o, d, ranges in packets],
        "blocks": records,
        "statistics": statistics,
        "stream_info": info,
        "findings": findings,
    }


# --- Opus -----------------------------------------------------------------------------------------

OPUS_MODES = ["SILK-only", "Hybrid", "CELT-only"]
OPUS_BANDWIDTHS = ["Narrowband", "Mediumband", "Wideband", "Super-wideband", "Fullband"]
OPUS_BANDWIDTH_RANGES = ["4 kHz", "6 kHz", "8 kHz", "12 kHz", "20 kHz"]
OPUS_LAYOUTS = VORBIS_LAYOUTS


def toc_info(toc):
    """RFC 6716, section 3.1, table 2: (mode, bandwidth, frame samples at 48 kHz)."""
    config = toc >> 3
    if config < 12:
        return 0, config // 4, [480, 960, 1920, 2880][config & 3]
    if config < 16:
        return 1, 3 if config < 14 else 4, 480 if config % 2 == 0 else 960
    return 2, [0, 2, 3, 4][(config - 16) // 4], 120 << (config & 3)


def frame_duration(samples):
    return "2.5 ms" if samples == 120 else f"{samples // 48} ms"


def toc_text(toc):
    mode, bandwidth, samples = toc_info(toc)
    return f"{OPUS_MODES[mode]}, {OPUS_BANDWIDTHS[bandwidth].lower()}, {frame_duration(samples)}"


def frame_length(length):
    """RFC 6716, section 3.2.1."""
    if length < 252:
        return bytes([length])
    return bytes([252 + (length & 3), (length - 252) >> 2])


def opus_packet(config, stereo, frames, code=None, vbr=None, padding=0, self_delimited=False, fill=0):
    """An Opus packet of the given frame lengths (frames filled with fill bytes), packed with the
    smallest code that holds them unless code says otherwise; padding adds code 3 padding."""
    lengths = list(frames)
    if code is None:
        code = 0 if len(lengths) == 1 and not padding else 1 if len(lengths) == 2 and lengths[0] == lengths[1] and not padding else 2 if len(lengths) == 2 and not padding else 3
    toc = bytes([(config << 3) | (4 if stereo else 0) | code])
    body = b"".join(bytes([fill]) * l for l in lengths)
    delimiter = b""
    if code == 0:
        delimiter = frame_length(lengths[0]) if self_delimited else b""
        return toc + delimiter + body
    if code == 1:
        delimiter = frame_length(lengths[0]) if self_delimited else b""
        return toc + delimiter + body
    if code == 2:
        return toc + frame_length(lengths[0]) + (frame_length(lengths[1]) if self_delimited else b"") + body
    vbr = (len(set(lengths)) > 1) if vbr is None else vbr
    count = bytes([(0x80 if vbr else 0) | (0x40 if padding else 0) | len(lengths)])
    pad = b""
    if padding:
        left = padding
        while left >= 255:
            pad += b"\xff"
            left -= 254
        pad += bytes([left])
    sizes = b""
    if vbr:
        sizes = b"".join(frame_length(l) for l in lengths[:-1]) + (frame_length(lengths[-1]) if self_delimited else b"")
    elif self_delimited:
        sizes = frame_length(lengths[0])
    return toc + count + pad + sizes + body + bytes(padding)


def opus_head(channels, pre_skip=312, rate=48000, gain=0, family=None, streams=None, coupled=None, mapping=None, version=1, extra=b""):
    family = (0 if channels <= 2 else 1) if family is None else family
    data = b"OpusHead" + struct.pack("<BBHIhB", version, channels, pre_skip, rate, gain, family)
    if family != 0:
        data += bytes([streams, coupled]) + bytes(mapping)
    return data + extra


def opus_tags(vendor, comments, extra=b""):
    return b"OpusTags" + m.vorbis_comment(vendor, comments) + extra


def read_length(data, position):
    if position >= len(data):
        return None, position
    b = data[position]
    if b < 252:
        return b, position + 1
    if position + 1 >= len(data):
        return None, len(data)
    return data[position + 1] * 4 + b, position + 2


def read_part(data, self_delimited):
    """One stream's Opus packet (RFC 6716, sections 3.2 and 3.4, appendix B): (length taken, toc,
    frame lengths or None, rule broken or None, header bytes, padding bytes)."""
    if not data:
        return 0, None, None, "R1", 0, 0
    toc, code, position = data[0], data[0] & 3, 1
    samples = toc_info(toc)[2]

    def fail(rule):
        return len(data), toc, None, rule, position, 0

    if code in (0, 1):
        if self_delimited:
            length, position = read_length(data, position)
            if length is None:
                return fail("SD")
        else:
            rest = len(data) - 1
            if code == 1 and rest % 2:
                return fail("R3")
            length = rest if code == 0 else rest // 2
        if length > 1275:
            return fail("R2")
        lengths = [length] * (code + 1)
        padding = 0
    elif code == 2:
        first, position = read_length(data, position)
        if first is None or first > len(data) - position:
            position = min(position, len(data))
            return fail("R4")
        if self_delimited:
            second, position = read_length(data, position)
            if second is None:
                return fail("SD")
        else:
            second = len(data) - position - first
        if second > 1275:
            return fail("R2")
        lengths, padding = [first, second], 0
    else:
        if len(data) < 2:
            return fail("R6")
        count = data[1]
        position = 2
        frames, vbr = count & 0x3F, count & 0x80
        if frames == 0 or frames * samples > 5760:
            return fail("R5")
        rule = "R7" if vbr else "R6"
        padding = 0
        if count & 0x40:
            while True:
                if position >= len(data):
                    return fail(rule)
                b = data[position]
                position += 1
                padding += 254 if b == 255 else b
                if b != 255:
                    break
        if vbr:
            lengths = []
            for _ in range(frames - 1):
                length, position = read_length(data, position)
                if length is None:
                    position = min(position, len(data))
                    return fail("R7")
                lengths.append(length)
            if self_delimited:
                length, position = read_length(data, position)
                if length is None:
                    return fail("SD")
                lengths.append(length)
            else:
                last = len(data) - position - padding - sum(lengths)
                if last < 0:
                    return fail("R7")
                if last > 1275:
                    return fail("R2")
                lengths.append(last)
        else:
            if self_delimited:
                length, position = read_length(data, position)
                if length is None:
                    return fail("SD")
            else:
                rest = len(data) - position - padding
                if rest < 0 or rest % frames:
                    return fail("R6")
                # The equal frames of a CBR packet are implicit, so one over 1275 bytes breaks R2.
                if rest // frames > 1275:
                    return fail("R2")
                length = rest // frames
            lengths = [length] * frames
    total = position + sum(lengths) + padding
    if total > len(data):
        return len(data), toc, None, "SD" if self_delimited else "R7", position, 0
    return (total if self_delimited else len(data)), toc, lengths, None, position, padding


def declared_samples(data):
    """The samples opus_packet_get_nb_samples gives from the TOC and frame count byte."""
    if not data:
        return 0
    code = data[0] & 3
    frames = 1 if code == 0 else 2 if code in (1, 2) else (data[1] & 0x3F if len(data) >= 2 else 0)
    samples = frames * toc_info(data[0])[2]
    return 0 if samples > 5760 else samples


def opus_head_fields(packet):
    if len(packet) < 19:
        return None
    version, channels, pre_skip, rate, gain, family = struct.unpack_from("<BBHIhB", packet, 8)
    fields = {"Magic Signature": "OpusHead", "Version": str(version), "Channels": str(channels)}
    layout = None
    if (family == 0 and channels in (1, 2)) or (family == 1 and 1 <= channels <= 8):
        layout = OPUS_LAYOUTS[channels]
        fields["Channel Layout"] = f"{layout[0]}: {', '.join(layout[1])}"
    fields.update({"Pre-skip": plural(pre_skip, "sample", "samples"), "Input Sample Rate": "0 (not given)" if rate == 0 else f"{rate} Hz",
                   "Output Gain": ("+" if gain >= 0 else "") + fmt(gain / 256.0, 2) + " dB",
                   "Mapping Family": {0: "0 (mono or stereo)", 1: "1 (Vorbis channel order)", 2: "2 (ambisonics, RFC 8486)",
                                      3: "3 (ambisonics with a demixing matrix, RFC 8486)", 255: "255 (no defined layout)"}.get(family, f"{family} (reserved)")})
    if family != 0 and len(packet) >= 21:
        fields["Stream Count"] = str(packet[19])
        fields["Coupled Count"] = str(packet[20])
    return fields


def ambisonics(channels):
    for order in range(15):
        full = (order + 1) ** 2
        if channels in (full, full + 2):
            return order, channels == full + 2
    return None


def opus_channel_mode(packet):
    channels, family = packet[9], packet[18]
    if (family == 0 and channels in (1, 2)) or (family == 1 and 1 <= channels <= 8):
        return OPUS_LAYOUTS[channels][0]
    if family in (2, 3) and ambisonics(channels):
        order, stereo = ambisonics(channels)
        return f"Ambisonics, order {order}" + (", with non-diegetic stereo" if stereo else "")
    return f"{channels} {'channel' if channels == 1 else 'channels'}"


def opus_expectations(head, packets, positions, gaps=()):
    """What the analyzer reports for an Opus stream's audio packets: head the OpusHead packet,
    packets (offset, bytes, ranges), positions (audio packet index, granule, is end of stream)."""
    has_head = len(head) >= 19 and (head[18] == 0 or len(head) >= 21)
    streams = max(1, head[19]) if has_head and head[18] != 0 else 1
    pre_skip = struct.unpack_from("<H", head, 10)[0] if has_head else 0

    tocs, findings = [], []
    stereo = [0, 0]
    codes = [0, 0, 0, 0]
    modes, bandwidths, frame_sizes = [0, 0, 0], [0] * 5, {}
    frames = empty = padding_total = parts = 0
    decoded = 0
    sizes = [len(d) for _, d, _ in packets]
    first_offset = offset = last = None
    after_gap = False
    bitrates = []
    last_samples = 0
    for index, (_, data, _) in enumerate(packets):
        after_gap |= index in gaps
        position, lost, first_samples, entry = 0, False, None, []
        for s in range(streams):
            if lost:
                continue
            taken, toc, lengths, rule, header, padding = read_part(data[position:], s < streams - 1)
            part_data = data[position:position + taken]
            position += taken
            if rule:
                lost = True
                findings.append("opus.self-delimiting" if rule == "SD" else f"opus.packet-{rule.lower()}")
            if toc is None:
                entry.append(None)
                continue
            samples = declared_samples(part_data)
            if first_samples is None:
                first_samples = samples
            elif samples != first_samples:
                findings.append("opus.stream-durations-differ")
            parts += 1
            stereo[1 if toc & 4 else 0] += 1
            codes[toc & 3] += 1
            entry.append([toc >> 3, 1 if toc & 4 else 0, toc & 3, len(lengths) if lengths else 0])
            if lengths is None:
                continue
            mode, bandwidth, size = toc_info(toc)
            frames += len(lengths)
            modes[mode] += len(lengths)
            bandwidths[bandwidth] += len(lengths)
            frame_sizes[size] = frame_sizes.get(size, 0) + len(lengths)
            empty += sum(1 for l in lengths if l == 0)
            padding_total += taken - header - sum(lengths)
        tocs.append(entry)
        samples = first_samples or 0
        last_samples = samples
        if samples:
            bitrates.append(len(data) * 8.0 * 48000 / samples / 1000)
        decoded += samples
        for at, granule, is_end in positions:
            if at != index or not has_head:
                continue
            o = granule - decoded
            if first_offset is None:
                first_offset = offset = max(0, o) if is_end else o
            elif after_gap:
                offset = o
            elif o != offset and not is_end:
                findings.append("opus.end-trimming-early" if o < offset else "opus.granule-inconsistent")
            after_gap = False
            last = (decoded, granule, is_end)

    if first_offset:
        findings.append("opus.start-offset" if first_offset > 0 else "opus.start-negative")
    trim = 0
    if last and offset is not None and last[2]:
        trim = last[0] + offset - last[1]
        if trim < 0:
            findings.append("opus.granule-beyond-packets")
            trim = 0
        elif trim > last_samples:
            findings.append("opus.end-trimming-large")
    samples = min(max(decoded - pre_skip - trim, 0), decoded)
    if has_head and packets and pre_skip + trim > decoded:
        findings.append("opus.pre-skip-beyond")

    varies = len(set(sizes)) > 1
    statistics = None
    if packets:
        statistics = {"Packets": str(len(packets)), "Frames": str(frames), "Decoded Samples": plural(decoded, "sample", "samples")}
        if has_head:
            statistics["Pre-skip"] = plural(pre_skip, "sample", "samples")
        if first_offset is not None:
            statistics["Start Offset"] = plural(first_offset, "sample", "samples")
            statistics["End Trimming"] = plural(trim, "sample", "samples")
        statistics["Samples"] = plural(samples, "sample", "samples")
        statistics["Duration"] = fmt(samples / 48000, 3) + " s"
        statistics["IsVbr"] = "true" if varies else "false"
        if decoded:
            statistics["Average Bitrate"] = fmt(sum(sizes) * 8.0 * 48000 / decoded / 1000, 1) + " kbps"
        if bitrates:
            statistics["Minimum Bitrate"] = fmt(min(bitrates), 1) + " kbps"
            statistics["Maximum Bitrate"] = fmt(max(bitrates), 1) + " kbps"
        statistics["Packet Sizes"] = (f"{min(sizes)} to {max(sizes)} bytes, {m.fmt_decimal(sum(sizes) / len(sizes), 1)} on average" if varies
                                      else f"all {plural(sizes[0], 'byte', 'bytes')}, as CBR encoding writes them")
        if frames:
            statistics["Modes"] = plural(sum(1 for c in modes if c), "mode", "modes")
            for i, c in enumerate(modes):
                if c:
                    statistics[f"Modes/{OPUS_MODES[i]}"] = m.share(c, frames, "frame", "frames")
            statistics["Bandwidths"] = plural(sum(1 for c in bandwidths if c), "bandwidth", "bandwidths")
            for i, c in enumerate(bandwidths):
                if c:
                    statistics[f"Bandwidths/{OPUS_BANDWIDTHS[i]} ({OPUS_BANDWIDTH_RANGES[i]})"] = m.share(c, frames, "frame", "frames")
            statistics["Frame Sizes"] = plural(len(frame_sizes), "size", "sizes")
            for size in sorted(frame_sizes):
                statistics[f"Frame Sizes/{frame_duration(size)}"] = m.share(frame_sizes[size], frames, "frame", "frames")
        if parts:
            unit = "stream packet" if streams > 1 else "packet"
            statistics["Stereo Coding"] = plural(stereo[1], f"stereo {unit}", f"stereo {unit}s")
            statistics["Stereo Coding/Stereo"] = m.share(stereo[1], parts, unit, unit + "s")
            statistics["Stereo Coding/Mono"] = m.share(stereo[0], parts, unit, unit + "s")
            statistics["Frame Packing"] = plural(sum(1 for c in codes if c), "code", "codes")
            names = ["Code 0 (one frame)", "Code 1 (two frames of equal size)", "Code 2 (two frames of different sizes)", "Code 3 (any number of frames)"]
            for i, c in enumerate(codes):
                if c:
                    statistics[f"Frame Packing/{names[i]}"] = m.share(c, parts, unit, unit + "s")
        if empty:
            statistics["Frames Without Data"] = str(empty)
        if padding_total:
            statistics["Padding"] = plural(padding_total, "byte", "bytes")

    info = None if len(head) < 19 or not has_head else {
        "codec": "Opus", "sample_rate": 48000, "channels": head[9], "samples": samples, "duration": samples / 48000,
        "average_bitrate": sum(sizes) * 8.0 * 48000 / decoded / 1000 if decoded else None, "is_vbr": varies,
        "channel_mode": opus_channel_mode(head) if head[9] else None, "channel_layout": opus_speakers(head),
    }
    return {
        "packets": [[o, len(d)] + ([list(x) for x in ranges] if len(ranges) > 1 else []) for o, d, ranges in packets],
        "tocs": tocs,
        "statistics": statistics,
        "stream_info": info,
        "findings": findings,
    }
