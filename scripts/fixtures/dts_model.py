"""DTS core frames, core extensions and extension substreams for the fixture scripts, written as
ETSI TS 102 114 V1.6.1 lays them out (clauses 5.4 to 5.7 for the core, 6.2, 6.4 and 6.5 for X96, XCh and
XXCH, 7.5 for the extension substream, Annex B for the CRC) and in the four forms of the core bit
stream (16-bit and 14-bit words, big and little endian; the specification describes only the first, the
others are FFmpeg's dca_syncwords.h and libdca's parse.c).

A built core frame has silent subframes: no subband has bits allocated (every ABITS 0, so no scale
factor, no transient mode and no sample is sent), the VQ and LFE data are zeros, and the DSYNC
words (5.6) end each subframe, so decoders play silence and the frame's fields are all there is to
check. A frame is built in the bit-packed domain (the stream of 5.4 as the specification reads it),
FSIZE+1 bytes long, and put into a form afterwards: a 16-bit little endian frame swaps each pair of
bytes, a 14-bit frame packs 14 bits into each 16-bit word, with bit 13 repeated in the two top bits
(which is how 0x7FFE8001 becomes 0x1FFFE800 0x07F... as the 14-bit sync words show).

The extension substream header and its asset descriptor are built with the fields 7.5.2 and 7.5.3
list, so the DTS-HD profiles can be shown without a DTS-HD encoder: a lossless (XLL), a low bit
rate (LBR), an extended bit rate (XBR), a 96 kHz (X96) and an extra channels (XXCH) asset, with each
component's sync word at the place the descriptor's sizes give.
"""

from mp4_boxes import Bits

SYNC = 0x7FFE8001
SYNC_XCH = 0x5A5A5A5A
SYNC_X96 = 0x1D95F262
SYNC_XXCH = 0x47004A03
SYNC_XBR = 0x655E315E
SYNC_LBR = 0x0A801921
SYNC_XLL = 0x41A29547
SYNC_EXSS = 0x64582025
SYNC_EXSS_CORE = 0x02B09261

# Table 5-5: the core audio sampling frequencies by SFREQ.
SAMPLE_RATES = {1: 8000, 2: 16000, 3: 32000, 6: 11025, 7: 22050, 8: 44100, 11: 12000, 12: 24000, 13: 48000}

# Table 5-7: the targeted bit rates in kbps by RATE; 29 is open.
RATES_KBPS = [32, 56, 64, 96, 112, 128, 192, 224, 256, 320, 384, 448, 512, 576, 640, 768, 960, 1024, 1152, 1280, 1344, 1408, 1411.2, 1472, 1536]

# Table 5-4: the channel arrangements: number of channels and the speakers, by AMODE.
AMODES = {
    0: (1, ["C"]), 1: (2, ["A", "B"]), 2: (2, ["L", "R"]), 3: (2, ["L+R", "L-R"]), 4: (2, ["Lt", "Rt"]),
    5: (3, ["C", "L", "R"]), 6: (3, ["L", "R", "S"]), 7: (4, ["C", "L", "R", "S"]), 8: (4, ["L", "R", "Ls", "Rs"]),
    9: (5, ["C", "L", "R", "Ls", "Rs"]), 10: (6, ["Cl", "Cr", "L", "R", "Ls", "Rs"]), 11: (6, ["C", "L", "R", "Lr", "Rr", "Oh"]),
    12: (6, ["Cf", "Cr", "Lf", "Rf", "Lr", "Rr"]), 13: (7, ["Cl", "C", "Cr", "L", "R", "Ls", "Rs"]),
    14: (8, ["Cl", "Cr", "L", "R", "Ls1", "Ls2", "Rs1", "Rs2"]), 15: (8, ["Cl", "C", "Cr", "L", "R", "Ls", "S", "Rs"]),
}

SEL_BITS = [1, 2, 2, 2, 2, 3, 3, 3, 3, 3]
# A scale factor adjustment index follows a SEL below these (Table 5-21).
ADJ_GROUP = [1, 3, 3, 3, 3, 7, 7, 7, 7, 7]


def crc16(data, value=0xFFFF):
    """Annex B: CRC-CCITT, x^16 + x^12 + x^5 + 1, initialized to 0xFFFF, over the bytes most significant bit first."""
    for byte in data:
        value ^= byte << 8
        for _ in range(8):
            value = ((value << 1) ^ 0x1021) & 0xFFFF if value & 0x8000 else (value << 1) & 0xFFFF
    return value


def channels_of(amode):
    return AMODES[amode][0] if amode in AMODES else None


def primary_channels(amode):
    """nPCHS: the core codes up to five channels; more are extension channels (5.4.3)."""
    return min(5, channels_of(amode))


def kbps_code(rate_kbps):
    """The smallest RATE whose targeted rate is at least rate_kbps, or open (29) beyond Table 5-7."""
    for code, kbps in enumerate(RATES_KBPS):
        if kbps >= rate_kbps:
            return code
    return 29


def silent_subframes(bits, p, nch, subs, vqsub, joinx, bhuff, shuff, ssc, header="core"):
    """The silent subframes of the core (5.5 and 5.6) or of XCh (6.4.4): per subframe the side information
    (nothing allocated), the VQ addresses, the LFE samples of the core and the subsubframes' end markers."""
    for sf, count in enumerate(ssc):
        if header == "core":
            bits.put(count - 1, 2)
            bits.put(p.get("psc", 0), 3)
        for ch in range(nch):
            for _ in range(subs[ch]):
                bits.put(0, 1)
        for ch in range(nch):
            width = bhuff[ch] - 1
            for _ in range(vqsub[ch]):
                bits.put(0, width)
        # ABITS 0 everywhere: no transient mode, no scale factor below the VQ start.
        for ch in range(nch):
            for _ in range(max(0, subs[ch] - vqsub[ch])):
                bits.put(0, shuff[ch] + 1)
        for ch in range(nch):
            if joinx[ch]:
                bits.put(5, 3)
        for ch in range(nch):
            if joinx[ch]:
                source = joinx[ch] - 1
                for _ in range(subs[ch], subs[source]):
                    bits.put(0, 6)
        if header == "core" and p["dynf"]:
            bits.put(p.get("range", 0), 8)
        if p["cpf"]:
            bits.put(p.get("sicrc", 0), 16)
        # The audio data: VQ addresses, the LFE channel, the subsubframes.
        for ch in range(nch):
            for _ in range(max(0, subs[ch] - vqsub[ch])):
                bits.put(0, 10)
        if header == "core" and p["lff"]:
            for _ in range(2 * p["lff"] * count):
                bits.put(0, 8)
            bits.put(0, 8)
        for ssf in range(count):
            if ssf == count - 1 or p["aspf"]:
                bits.put(0xFFFF, 16)


def audio_header(bits, p, nch, header="core"):
    """The primary audio coding header (Table 5-21), or XCh's audio header (Table 6-18) without SUBFS."""
    subs, vqsub, joinx, thuff, shuff, bhuff, sel = (p[k] for k in ("subs", "vqsub", "joinx", "thuff", "shuff", "bhuff", "sel"))
    if header == "core":
        bits.put(p["subfs"], 4)
    bits.put(nch - 1, 3)
    for values, width in ((subs, 5), (vqsub, 5), (joinx, 3), (thuff, 2), (shuff, 3), (bhuff, 3)):
        for ch in range(nch):
            bits.put(values[ch], width)
    for n in range(10):
        for ch in range(nch):
            bits.put(sel[ch][n], SEL_BITS[n])
    for n in range(10):
        for ch in range(nch):
            if sel[ch][n] < ADJ_GROUP[n]:
                bits.put(p.get("adj", {}).get((ch, n), 0), 2)
    if p["cpf"]:
        bits.put(p.get("ahcrc", 0), 16)


def defaults(**o):
    """The fields of a core frame with their defaults; o overrides."""
    amode = o.get("amode", 2)
    nch = primary_channels(amode) if amode in AMODES else o.get("nch", 2)
    p = {
        "ftype": 1, "short": 31, "cpf": 0, "nblks": 15, "amode": amode, "sfreq": 13, "fixed": 0, "dynf": 0, "timef": 0, "auxf": 0, "hdcd": 0,
        "ext_id": 0, "ext": 0, "aspf": 0, "lff": 0, "hflag": 1, "hcrc": 0x1234, "filts": 1, "vernum": 7, "chist": 0, "pcmr": 0b110, "sumf": 0,
        "sums": 0, "dialnorm": 0, "subfs": None, "nch": nch, "subs": [30] * nch, "vqsub": [31] * nch, "joinx": [0] * nch, "thuff": [0] * nch,
        "shuff": [6] * nch, "bhuff": [5] * nch, "sel": [[1, 3, 3, 3, 3, 7, 7, 7, 7, 7] for _ in range(nch)], "ssc": None, "times": 0x01020304,
    }
    p.update(o)
    if p["ssc"] is None:
        blocks = (p["nblks"] + 1) // 8
        per = min(4, blocks)
        while blocks % per:
            per -= 1
        p["ssc"] = [per] * (blocks // per)
    if p["subfs"] is None:
        p["subfs"] = len(p["ssc"]) - 1
    return p


def core_body(p):
    """The core frame's bits up to the end of the optional information, in the bit-packed domain."""
    bits = Bits()
    bits.put(p.get("sync", SYNC), 32)
    bits.put(p["ftype"], 1).put(p["short"], 5).put(p["cpf"], 1).put(p["nblks"], 7)
    bits.put(0, 14)  # FSIZE, filled in once the length is known
    bits.put(p["amode"], 6).put(p["sfreq"], 4)
    bits.put(0, 5)  # RATE, filled in with FSIZE
    bits.put(p["fixed"], 1).put(p["dynf"], 1).put(p["timef"], 1).put(p["auxf"], 1).put(p["hdcd"], 1)
    bits.put(p["ext_id"], 3).put(p["ext"], 1).put(p["aspf"], 1).put(p["lff"], 2).put(p["hflag"], 1)
    if p["cpf"]:
        bits.put(p["hcrc"], 16)
    bits.put(p["filts"], 1).put(p["vernum"], 4).put(p["chist"], 2).put(p["pcmr"], 3).put(p["sumf"], 1).put(p["sums"], 1)
    bits.put(p["dialnorm"], 4)
    nch = p["nch"]
    audio_header(bits, p, nch)
    silent_subframes(bits, p, nch, [s + 2 for s in p["subs"]], [v + 1 for v in p["vqsub"]], p["joinx"], p["bhuff"], p["shuff"], p["ssc"])
    if p["timef"]:
        bits.put(p["times"], 32)
    return bits


def core_frame(size=None, rate=None, extensions=(), fsize=None, **o):
    """A core frame of size bytes (FSIZE+1) in the bit-packed domain: the fields, the silent subframes,
    zero padding and the extensions at the end, each already as long as its own size field says."""
    p = defaults(**o)
    bits = core_body(p)
    body = bits.align().bytes()
    tail = b"".join(extensions)
    length = size if size is not None else max(96, -(-(len(body) + len(tail)) // 4) * 4)
    assert length >= len(body) + len(tail), (length, len(body), len(tail))
    frame = bytearray(body + bytes(length - len(body) - len(tail)) + tail)
    samples = 32 * (p["nblks"] + 1)
    sfreq = SAMPLE_RATES.get(p["sfreq"], 48000)
    code = rate if rate is not None else kbps_code(length * 8 * sfreq / samples / 1000 - 0.001 - 32 * sfreq / samples / 1000)
    # After the sync word: FTYPE 1, SHORT 5, CPF 1, NBLKS 7, FSIZE 14 (bits 46 to 59), AMODE 6, SFREQ 4, RATE 5 (bits 70 to 74).
    value = int.from_bytes(frame[:12], "big")
    fsize_shift = 96 - 60
    rate_shift = 96 - 75
    value = (value & ~(0x3FFF << fsize_shift) & ~(0x1F << rate_shift)) | (((length - 1) if fsize is None else fsize) << fsize_shift) | (code << rate_shift)
    frame[:12] = value.to_bytes(12, "big")
    return bytes(frame)


def to_form(frame, form):
    """The frame in one of the four forms: 16be as it is, 16le with each pair of bytes swapped, 14be and 14le with 14 bits in each 16-bit word."""
    if form == "16be":
        return frame
    if form == "16le":
        out = bytearray(frame)
        if len(out) % 2:
            out.append(0)
        out[0::2], out[1::2] = out[1::2], out[0::2]
        return bytes(out)
    bits = "".join(f"{b:08b}" for b in frame)
    bits += "0" * (-len(bits) % 14)
    out = bytearray()
    for i in range(0, len(bits), 14):
        word = int(bits[i:i + 14], 2)
        if word & 0x2000:
            word |= 0xC000
        out += word.to_bytes(2, "big" if form == "14be" else "little")
    return bytes(out)






# --- core extensions ---------------------------------------------------------------------------

def xch(size=None, legacy=False, subs=30, vqsub=31, p=None, ssc=None, fsize_adjust=0):
    """XCh (6.4): the frame header (Table 6-17) and the audio header and subframes of the one extension channel, silent. XChFSIZE is
    the size less 1 (6.4.2); legacy writes the size itself, which the specification's note lets decoders accept."""
    p = p or defaults()
    ssc = ssc or p["ssc"]
    ext = dict(p, subs=[subs], vqsub=[vqsub], joinx=[0], thuff=[0], shuff=[6], bhuff=[5], sel=[[1, 3, 3, 3, 3, 7, 7, 7, 7, 7]])
    bits = Bits()
    bits.put(SYNC_XCH, 32)
    bits.put(0, 10)
    bits.put(1, 4)
    audio_header(bits, ext, 1, header="xch")
    silent_subframes(bits, ext, 1, [subs + 2], [vqsub + 1], [0], [5], [6], ssc, header="xch")
    body = bits.align().bytes()
    total = size or max(96, -(-len(body) // 4) * 4)
    assert total >= len(body) and total % 4 == 0 and 96 <= total <= 1024, (total, len(body))
    out = bytearray(body + bytes(total - len(body)))
    out[4:6] = ((int.from_bytes(out[4:6], "big") & 0x3F) | (((total - (1 if not legacy else 0)) + fsize_adjust) << 6)).to_bytes(2, "big")
    return bytes(out)


def x96(size=None, revno=1, p=None, ssc=None, highres=0, nch=None, fsize_adjust=0, start=27, sbe=31):
    """X96 in the core (6.2.4.2 and 6.2.4.4): the frame header, then the channel set header (Table 6-4) and silent subframes: every
    extension subband from start has bit allocation 0 (3 bits each, BHUFF96 7) and scale factor index 0 (6 bits, SHUFF96 5)."""
    p = p or defaults()
    ssc = ssc or p["ssc"]
    nch = nch or p["nch"]
    bits = Bits()
    bits.put(SYNC_X96, 32)
    bits.put(0, 12)
    bits.put(revno, 4)
    bits.put(highres, 1)
    bits.put(start, 5)
    for _ in range(nch):
        bits.put(sbe, 6)
    for _ in range(nch):
        bits.put(0, 3)
    for _ in range(nch):
        bits.put(5, 3)
    for _ in range(nch):
        bits.put(7, 3)
    for n in range(6 + 4 * highres):
        for _ in range(nch):
            bits.put(0 if n else 1, SEL_BITS[n])
    if p["cpf"]:
        bits.put(0, 16)
    low = start
    high = sbe + 1
    for _ in ssc:
        for _ in range(nch):
            for _ in range(low, high):
                bits.put(0, 1)
        for _ in range(nch):
            for _ in range(low, high):
                bits.put(0, 3 + highres)
        for _ in range(nch):
            for _ in range(low, high):
                bits.put(0, 6)
        if p["cpf"]:
            bits.put(0, 16)
        for ssf in range(ssc[0]):
            if ssf == ssc[0] - 1 or p["aspf"]:
                bits.put(0xFFFF, 16)
    body = bits.align().bytes()
    total = size or max(96, -(-len(body) // 4) * 4)
    assert total >= len(body) and total % 4 == 0 and 96 <= total <= 4096, (total, len(body))
    out = bytearray(body + bytes(total - len(body)))
    word = int.from_bytes(out[4:8], "big")
    word = (word & 0x000FFFFF) | ((total - 1 + fsize_adjust) << 20)
    out[4:8] = word.to_bytes(4, "big")
    return bytes(out)


def xxch(chsets=((1, 0x40),), core_mask=0x3F, mask_bits=16, crc_chset=False, bad_crc=False, filler=64, p=None):
    """XXCH (6.5): the frame header (Table 6-21) with its CRC and, per channel set, the start of its header (Table 6-23: size, channels,
    speaker mask) and its CRC where the header has one, then filler for the channel set's data. chsets lists (channels, speaker mask)."""
    sizes = []
    headers = []
    for channels, mask in chsets:
        bits = Bits()
        bits.put(0, 7)  # header size, filled in below
        bits.put(channels - 1, 3)
        bits.put(mask >> 6, max(0, mask_bits - 6))
        bits.put(0, 1)  # no embedded downmix coefficients
        for _ in range(channels):
            bits.put(30, 5)
        for _ in range(channels):
            bits.put(31, 5)
        for _ in range(channels):
            bits.put(0, 3)
        for _ in range(channels):
            bits.put(0, 2)
        for _ in range(channels):
            bits.put(6, 3)
        for _ in range(channels):
            bits.put(5, 3)
        for n in range(10):
            for _ in range(channels):
                bits.put([1, 3, 3, 3, 3, 7, 7, 7, 7, 7][n], SEL_BITS[n])
        bits.align()
        raw = bytearray(bits.bytes() + (b"\0\0" if crc_chset else b""))
        raw[0] = (raw[0] & 0x01) | ((len(raw) - 1) << 1)
        if crc_chset:
            raw[-2:] = crc16(bytes(raw[:-2])).to_bytes(2, "big")
        headers.append(bytes(raw))
        sizes.append(len(raw) + filler)
    head = Bits()
    head.put(SYNC_XXCH, 32)
    head.put(0, 6)  # header size, filled in below
    head.put(1 if crc_chset else 0, 1)
    head.put(mask_bits - 1, 5)
    head.put(len(chsets) - 1, 2)
    for size in sizes:
        head.put(size - 1, 14)
    head.put(core_mask, mask_bits)
    head.align()
    raw = bytearray(head.bytes() + b"\0\0")
    size = len(raw)
    raw[4] = (raw[4] & 0x03) | ((size - 1) << 2)
    raw[-2:] = crc16(bytes(raw[4:-2])).to_bytes(2, "big")
    if bad_crc:
        raw[-1] ^= 0xFF
    out = bytes(raw)
    for header, total in zip(headers, sizes):
        out += header + bytes(total - len(header))
    return out + bytes(-len(out) % 4)


def xxch_overrun(size=12, sets=4, mask_bits=32):
    """An XXCH extension (6.5.2) of size bytes whose header CRC-16 holds but whose fields (the number of channel sets and the speaker
    mask) need more bits than the extension has, so the frame ends inside its header."""
    bits = Bits()
    bits.put(SYNC_XXCH, 32)
    bits.put(size - 1, 6)
    bits.put(0, 1)
    bits.put(mask_bits - 1, 5)
    bits.put(sets - 1, 2)
    bits.align()
    raw = bytearray(bits.bytes())
    raw = raw[:size - 2] + bytearray(size - 2 - len(raw[:size - 2]))
    raw += crc16(bytes(raw[4:size - 2])).to_bytes(2, "big")
    assert len(raw) == size and size % 4 == 0
    return bytes(raw)


# --- extension substream -----------------------------------------------------------------------

COMPONENT_BITS = {"core": 0x10, "xbr": 0x20, "xxch": 0x40, "x96": 0x80, "lbr": 0x100, "xll": 0x200}
COMPONENT_ORDER = ("core", "xbr", "xxch", "x96", "lbr", "xll")
# Table 7-9: nuMaxSampleRate.
MAX_SAMPLE_RATE_CODES = {8000: 0, 16000: 1, 32000: 2, 64000: 3, 128000: 4, 22050: 5, 44100: 6, 88200: 7, 176400: 8, 352800: 9, 12000: 10, 24000: 11,
                         48000: 12, 96000: 13, 192000: 14, 384000: 15}


def asset_descriptor(a, wide, static, mix_enabled=False):
    """An audio asset descriptor (Tables 7-5 to 7-7) as bits, a whole number of bytes long (nuAssetDescriptFsize), with the fields a gives."""
    bits = Bits()
    bits.put(0, 9)  # nuAssetDescriptFsize - 1, filled in below
    bits.put(a.get("index", 0), 3)
    if static:
        bits.put(1 if "asset_type" in a else 0, 1)
        if "asset_type" in a:
            bits.put(a["asset_type"], 4)
        bits.put(1 if "language" in a else 0, 1)
        if "language" in a:
            bits.put(int.from_bytes(a["language"].encode("latin-1"), "big"), 24)
        text = a.get("text")
        bits.put(1 if text else 0, 1)
        if text:
            bits.put(len(text) - 1, 10)
            for char in text.encode("latin-1"):
                bits.put(char, 8)
        bits.put(a.get("bit_resolution", 24) - 1, 5)
        bits.put(MAX_SAMPLE_RATE_CODES[a.get("max_rate", 48000)], 4)
        channels = a.get("channels", 6)
        bits.put(channels - 1, 8)
        one_to_one = a.get("one_to_one", True)
        bits.put(1 if one_to_one else 0, 1)
        if one_to_one:
            if channels > 2:
                bits.put(1 if a.get("embedded_stereo") else 0, 1)
            if channels > 6:
                bits.put(1 if a.get("embedded_six") else 0, 1)
            mask = a.get("mask", 0x0F)
            bits.put(1 if mask is not None else 0, 1)
            if mask is not None:
                mask_bits = max(4, -(-mask.bit_length() // 4) * 4)
                bits.put(mask_bits // 4 - 1, 2)
                bits.put(mask, mask_bits)
            remap = a.get("remap_sets", 0)
            bits.put(remap, 3)  # speaker remapping sets, which need the mask's width for their layouts
            for _ in range(remap):
                bits.put(0, 5)
        else:
            bits.put(a.get("representation", 0), 3)
    # Dynamic metadata (Table 7-6).
    bits.put(1 if "drc" in a else 0, 1)
    if "drc" in a:
        bits.put(a["drc"], 8)
    bits.put(1 if "dialnorm" in a else 0, 1)
    if "dialnorm" in a:
        bits.put(a["dialnorm"], 5)
    # The embedded stereo flag is a static field, which a header without static fields leaves as the last one that had it.
    if "drc" in a and a.get("embedded_stereo") and a.get("one_to_one", True):
        bits.put(a.get("drc_stereo", 0), 8)
    if mix_enabled:
        bits.put(0, 1)  # bMixMetadataPresent
    # Decoder navigation data (Table 7-7).
    mode = a.get("mode", 0)
    bits.put(mode, 2)
    sizes = a.get("sizes", {})
    fsize_bits = 20 if wide else 16
    if mode == 0:
        mask = 0
        for name in a.get("components", []):
            mask |= COMPONENT_BITS[name]
        mask |= a.get("reserved_bits", 0)
        bits.put(mask, 12)
        if mask & 0x10:
            bits.put(sizes.get("core", 0) - 1, 14)
            bits.put(1, 1)  # core sync word present
            bits.put(0, 2)
        if mask & 0x20:
            bits.put(sizes.get("xbr", 0) - 1, 14)
        if mask & 0x40:
            bits.put(sizes.get("xxch", 0) - 1, 14)
        if mask & 0x80:
            bits.put(sizes.get("x96", 0) - 1, 12)
        if mask & 0x100:
            bits.put(sizes.get("lbr", 0) - 1, 14)
            bits.put(1, 1)
            bits.put(0, 2)
        if mask & 0x200:
            bits.put(sizes.get("xll", 0) - 1, fsize_bits)
            bits.put(0, 1)  # no XLL sync offset
        if mask & 0x400:
            bits.put(0, 16)
        if mask & 0x800:
            bits.put(0, 16)
        if mask & 0x200:
            bits.put(a.get("hd_stream_id", 0), 3)
    elif mode == 1:
        bits.put(sizes.get("xll", 0) - 1, fsize_bits)
        bits.put(0, 1)
        bits.put(a.get("hd_stream_id", 0), 3)
    elif mode == 2:
        bits.put(sizes.get("lbr", 0) - 1, 14)
        bits.put(1, 1)
        bits.put(0, 2)
    else:
        bits.put(sizes.get("aux", 0) - 1, 14)
        bits.put(0, 8)
        bits.put(0, 1)
    if a.get("one_to_one", True) and mix_enabled:
        bits.put(0, 1)  # bOnetoOneMixingFlag
    bits.put(0, 1)  # bDecodeAssetInSecondaryDecoder
    bits.put(0, 1)  # bDRCMetadataRev2Present
    bits.put(0, a.get("descriptor_pad", 0) * 8)
    bits.align()
    # The size: the whole descriptor in bytes, less one.
    size = a.get("size_field", bits.count // 8)
    assert 1 <= bits.count // 8 <= 512 and 1 <= size <= 512, size
    value = bits.value & ((1 << (bits.count - 9)) - 1)
    bits.value = ((size - 1) << (bits.count - 9)) | value
    return bits


def finish_header(bits, crc_start):
    """Pads the header bits to a byte, appends the CRC-16 over the bytes from crc_start (the bytes after the sync word) to the end, and returns the bytes."""
    bits.align()
    raw = bytearray(bits.bytes() + b"\0\0")
    raw[-2:] = crc16(bytes(raw[crc_start:-2])).to_bytes(2, "big")
    return raw


# Table 7-10's bits (nuSpkrActivityMask) as the bits of Table 6-22, the order of one bit for each speaker that the channel masks of XXCH and
# of XLL's channel sets use (FFmpeg's DCA_SPEAKER_*): each pair of Table 7-10 is two bits there.
MASK_7_10_TO_6_22 = [(0,), (1, 2), (3, 4), (5,), (6,), (13, 15), (7, 8), (14,), (19,), (11, 12), (17, 18), (9, 10), (16,), (20, 21), (22,), (23, 24)]

# FFmpeg's ff_dca_dmix_primary_nch: the channels each primary downmix type (Table 8-8) mixes to.
DMIX_PRIMARY_CHANNELS = [1, 2, 2, 3, 3, 4, 4, 0]


def mask_to_speakers(mask):
    """Table 7-10's activity mask as the mask of Table 6-22."""
    result = 0
    for bit, targets in enumerate(MASK_7_10_TO_6_22):
        if mask & (1 << bit):
            for t in targets:
                result |= 1 << t
    return result


def xll_set(channels=2, mask=None, bit_res=24, storage=24, rate=48000, interp=0, replacement=0, active=True, primary=True, dmix=None, hier=True,
            residual=None, one_to_one=True, polar=False, extra_bands=False, bad_crc=False, header_pad=0, size_override=None):
    """The options of one channel set sub-header (8.3.2, Table 8-4)."""
    if mask is None:
        mask = {1: 0x1, 2: 0x6, 6: 0x3F, 8: 0x63F}.get(channels, (1 << channels) - 1)
    return dict(channels=channels, mask=mask, bit_res=bit_res, storage=storage, rate=rate, interp=interp, replacement=replacement, active=active,
                primary=primary, dmix=dmix, hier=hier, residual=(1 << channels) - 1 if residual is None else residual, one_to_one=one_to_one,
                polar=polar, extra_bands=extra_bands, bad_crc=bad_crc, header_pad=header_pad, size_override=size_override)


def _bands_of(s):
    if s["rate"] > 96000:
        return 4 if s["extra_bands"] else 2
    return 1


def _set_header(s, hier_ofs, mask_bits, size_bits, scalable):
    """A channel set sub-header as bytes, with its size and CRC-16 (Table 8-4); fields are written as FFmpeg's chs_parse_header reads them."""
    b = Bits()
    b.put(0, 10)  # nChSetHeaderSize - 1, filled in below
    n = s["channels"]
    b.put(n - 1, 4)
    b.put(s["residual"], n)
    b.put(s["bit_res"] - 1, 5)
    b.put(s["storage"] - 1, 5)
    b.put(MAX_SAMPLE_RATE_CODES[s["rate"]], 4)
    b.put(s["interp"], 2)
    b.put(s["replacement"], 2)
    if s["replacement"]:
        b.put(1 if s["active"] else 0, 1)
    if s["one_to_one"]:
        b.put(1 if s["primary"] else 0, 1)
        dmix = s["dmix"]
        b.put(1 if dmix else 0, 1)
        if dmix:
            b.put(1 if dmix.get("embedded") else 0, 1)
            if s["primary"]:
                b.put(dmix.get("type", 1), 3)
        b.put(1 if s["hier"] else 0, 1)
        if dmix:
            if s["primary"]:
                for _ in range(DMIX_PRIMARY_CHANNELS[dmix.get("type", 1)] * n):
                    b.put(0, 9)
            else:
                for _ in range(hier_ofs):
                    b.put(0x40, 9)  # the scale: a code FFmpeg's table accepts
                    for _ in range(n):
                        b.put(0, 9)
        b.put(0 if s["polar"] else 1, 1)
        if s["polar"]:
            for _ in range(n):
                b.put(0, 25)
        else:
            b.put(s["mask"], mask_bits)
    else:
        b.put(0, 1)  # bMappingCoeffsPresent
    bands = _bands_of(s)
    if s["rate"] > 96000:
        b.put(1 if s["extra_bands"] else 0, 1)
    for band in range(bands):
        b.put(0, 1)  # no pairwise channel decorrelation
        for _ in range(n):
            b.put(0, 4)  # adaptive predictor order
        for _ in range(n):
            b.put(0, 2)  # fixed predictor order
        if band == 0:
            if scalable:
                b.put(0, size_bits)
                b.put(0, 4 * n)
                b.put(0, 4 * n)
        else:
            if s["one_to_one"] and s["dmix"] and s["dmix"].get("embedded"):
                b.put(0, 1)
            b.put(0, 1)  # no MSB/LSB split
            b.put(0, 1)  # no scalable resolution
    b.put(0, s["header_pad"] * 8)
    b.align()
    b.put(0, 16)
    raw = bytearray(b.bytes())
    size = len(raw)
    assert size <= 1024, size
    stated = s["size_override"] or size
    raw[0] = (stated - 1) >> 2
    raw[1] = (raw[1] & 0x3F) | (((stated - 1) & 3) << 6)
    raw[-2:] = crc16(bytes(raw[:-2])).to_bytes(2, "big")
    if s["bad_crc"]:
        raw[-1] ^= 0xFF
    return bytes(raw)


def xll_frame(size, sets=None, core_rate=48000, core_samples=512, segments_log=None, samples_log=None, scalable=False, size_bits=8, band_crc=0,
              x_sync=None, x_payload=8, bad_navi_crc=False, bad_sync=False, version=0, header_pad=0, mask_bits=None, frame_size=None):
    """The lossless extension (8.3) as a decoder reads it: the common header (Table 8-1), a channel set sub-header for each set (Table 8-4),
    the navigation index (8.4.2) and silent band data: every residual is zero, coded with linear codes of 0 bits, so a decoder plays
    silence. x_sync adds the 4-byte word FFmpeg looks for after the band data, and x_payload bytes after it (dca_xll.c)."""
    sets = sets or [xll_set()]
    mask_bits = mask_bits or max(8, max(s["mask"].bit_length() for s in sets))
    band_max = max(_bands_of(s) for s in sets)
    first = sets[0]
    total_samples = core_samples * first["rate"] // core_rate // _bands_of(first)
    if samples_log is None:
        # 8.3.1: at most 256 samples in a segment at 48 kHz or below, 512 above.
        samples_log = min(8 if first["rate"] <= 48000 else 9, max(1, total_samples.bit_length() - 1))
    if segments_log is None:
        segments_log = max(0, (total_samples >> samples_log).bit_length() - 1)
    head = Bits()
    head.put(0 if bad_sync else SYNC_XLL, 32)
    head.put(version, 4)
    head.put(0, 8)
    head.put(15, 5)
    head.put((frame_size or size) - 1, 16)
    head.put(len(sets) - 1, 4)
    head.put(segments_log, 4)
    head.put(samples_log, 4)
    head.put(size_bits - 1, 5)
    head.put(band_crc, 2)
    head.put(1 if scalable else 0, 1)
    head.put(mask_bits - 1, 5)
    if scalable:
        head.put(0, 4)
    head.put(0, header_pad * 8)
    raw = finish_header(head, 4)
    raw[4] = (raw[4] & 0xF0) | ((len(raw) - 1) >> 4)
    raw[5] = (raw[5] & 0x0F) | (((len(raw) - 1) & 15) << 4)
    raw[-2:] = crc16(bytes(raw[4:-2])).to_bytes(2, "big")
    out = bytes(raw)
    hier_ofs = 0
    for s in sets:
        out += _set_header(s, hier_ofs, mask_bits, size_bits, scalable)
        if s["hier"]:
            hier_ofs += s["channels"]
    # Band data: for each band, segment and set that has the band.
    nsegs = 1 << segments_log
    entries = []
    data = b""
    for band in range(band_max):
        for seg in range(nsegs):
            for s in sets:
                if _bands_of(s) <= band:
                    continue
                nabits = 5 if s["storage"] > 16 else 4 if s["storage"] > 8 else 3
                if (len(sets) > 1 or _bands_of(s) > 1) and nabits < 5:
                    nabits += 1
                bits = Bits()
                if seg == 0:
                    bits.put(1, 1)  # common coding parameters
                    bits.put(0, 1)  # linear code
                    bits.put(0, nabits)
                    bits.put(0, nabits)
                    if band == 1:
                        bits.put(0, 5)
                        bits.put(0, 7 * s["channels"])
                else:
                    bits.put(1, 1)  # the parameters of the segment before
                chunk = bits.bytes()
                entries.append(len(chunk))
                data += chunk
    navi = Bits()
    for e in entries:
        navi.put(e - 1, size_bits)
    navi.align()
    navi_bytes = bytearray(navi.bytes() + b"\0\0")
    navi_bytes[-2:] = crc16(bytes(navi_bytes[:-2])).to_bytes(2, "big")
    if bad_navi_crc:
        navi_bytes[-1] ^= 0xFF
    out += bytes(navi_bytes) + data
    out += bytes(-len(out) % 4)
    if x_sync is not None:
        out += x_sync.to_bytes(4, "big") + bytes(x_payload)
    assert len(out) <= size, (len(out), size)
    return out + bytes(size - len(out))


def sets_of(asset):
    """The channel sets an asset descriptor's fields imply: one set with all of its channels."""
    channels = asset.get("channels", 6)
    mask = asset.get("mask", 0x0F)
    speakers = mask_to_speakers(mask) if mask else None
    if speakers is not None and bin(speakers).count("1") != channels:
        speakers = None
    return [xll_set(channels=channels, mask=speakers, bit_res=asset.get("bit_resolution", 24), storage=24, rate=asset.get("max_rate", 48000))]


def xll_component(size, bad_sync=False, scalable=False, asset=None, sets=None, **options):
    """The lossless extension of an asset: a decodable silent frame with the channel sets the asset's descriptor implies (or sets)."""
    return xll_frame(size, sets=sets or sets_of(asset or {}), scalable=scalable, bad_sync=bad_sync, **options)


def xbr_component(size, sets=1, channels=(2,), bad_crc=False, bad_sync=False):
    """The extended bit rate extension (6.3.5, 6.3.6): the frame header with the channel sets' sub-headers and the CRC, then filler."""
    bits = Bits()
    bits.put(0 if bad_sync else SYNC_XBR, 32)
    bits.put(0, 6)  # nHeaderSizeXBR - 1
    bits.put(sets - 1, 2)
    for _ in range(sets):
        bits.put(31, 14)
    bits.put(1, 1)  # XBRTMODEFlag
    for k in range(sets):
        bits.put(channels[k] - 1, 3)
        bits.put(1, 2)  # nBits4MaxSubbands = 6
        for _ in range(channels[k]):
            bits.put(31, 6)
    raw = finish_header(bits, 4)
    raw[4] = (raw[4] & 0x03) | ((len(raw) - 1) << 2)
    raw[-2:] = crc16(bytes(raw[4:-2])).to_bytes(2, "big")
    if bad_crc:
        raw[-1] ^= 0xFF
    assert len(raw) <= size
    return bytes(raw) + bytes(size - len(raw))


def x96_component(size, sets=1, channels=(6,), bad_sync=False):
    """The X96 extension in an extension substream (6.2.4.3, Table 6-3): the frame header with its CRC, then filler."""
    bits = Bits()
    bits.put(0 if bad_sync else SYNC_X96, 32)
    bits.put(0, 6)  # nHeaderSizeX96 - 1
    bits.put(1, 4)  # REVNO
    bits.put(0, 1)  # no channel set header CRC
    bits.put(sets - 1, 2)
    for _ in range(sets):
        bits.put(63, 12)
    for k in range(sets):
        bits.put(channels[k] - 1, 3)
    raw = finish_header(bits, 4)
    raw[4] = (raw[4] & 0x03) | ((len(raw) - 1) << 2)
    raw[-2:] = crc16(bytes(raw[4:-2])).to_bytes(2, "big")
    assert len(raw) <= size
    return bytes(raw) + bytes(size - len(raw))


def lbr_component(size, rate_code=12, mask=0x0003, flags=0x1B, bit_rate=64000, header_type=2, bad_sync=False):
    """The LBR component (9.3, Table 9-1): the sync word, the header type and the decoder initialization data, little endian fields."""
    data = bytearray((0 if bad_sync else SYNC_LBR).to_bytes(4, "big"))
    data.append(header_type)
    if header_type == 2:
        data.append(rate_code)
        data += mask.to_bytes(2, "little")
        data += (0x0100).to_bytes(2, "little")
        data.append(flags)
        data.append(((bit_rate >> 12) & 0xF0) | ((bit_rate >> 16) & 0x0F))
        data += (bit_rate & 0xFFFF).to_bytes(2, "little")
        data += (bit_rate & 0xFFFF).to_bytes(2, "little")
    assert len(data) <= size
    return bytes(data) + bytes(size - len(data))


def core_component(size, **options):
    """A core frame for the extension substream: its sync word is 0x02B09261 (7.4.1), and it is size bytes long."""
    return core_frame(size=size, sync=SYNC_EXSS_CORE, **options)


def component_bytes(name, size, sync_ok=True, **options):
    """A component of an asset, as its builder writes it."""
    if name == "core":
        frame = core_component(size, **options.get("core", {}))
        return frame if sync_ok else bytes(4) + frame[4:]
    if name == "xbr":
        return xbr_component(size, bad_sync=not sync_ok, **options.get("xbr", {}))
    if name == "xxch":
        raw = xxch(**options.get("xxch", {}))
        raw = raw[:size] + bytes(max(0, size - len(raw)))
        return raw if sync_ok else bytes(4) + raw[4:]
    if name == "x96":
        return x96_component(size, bad_sync=not sync_ok, **options.get("x96", {}))
    if name == "lbr":
        return lbr_component(size, bad_sync=not sync_ok, **options.get("lbr", {}))
    return xll_component(size, bad_sync=not sync_ok, asset=options.get("_asset"), **options.get("xll", {}))


def exss(assets, index=0, wide=False, static=True, ref_clock=2, duration_code=0, presentations=1, mix=False, bc_core=False, size=None,
         bad_crc=False, user_bits=0, timestamp=None, header_pad=None):
    """An extension substream (7.5.2): header, asset descriptors, then each asset's components with their sync words at the offsets
    the descriptors' sizes give. An asset is a dict: "components" (names of Table 7-15), "sizes", the static fields and the options of
    asset_descriptor. duration_code is the 3-bit field: the frame lasts 512 * (code + 1) reference clock cycles."""
    for a in assets:
        names = list(a.get("components", []))
        if a.get("mode") == 1:
            names = ["xll"]
        if a.get("mode") == 2:
            names = ["lbr"]
        a["_names"] = [n for n in COMPONENT_ORDER if n in names]
        for name in a["_names"]:
            a.setdefault("sizes", {}).setdefault(name, 64 if name == "xll" else 32)
    payloads = []
    for a in assets:
        payload = b"".join(a["raw"][n] if n in a.get("raw", {}) else component_bytes(n, a["sizes"][n], sync_ok=n not in a.get("bad_sync", []), _asset=a, **a.get("component_options", {}))
                           for n in a["_names"])
        payloads.append(payload + bytes(a.get("data_pad", 0)))
    size_bits = 20 if wide else 16
    head = Bits()
    head.put(SYNC_EXSS, 32)
    head.put(user_bits, 8)
    head.put(index, 2)
    head.put(1 if wide else 0, 1)
    head.put(0, 8 + 4 * wide)  # nuExtSSHeaderSize - 1
    head.put(0, size_bits)  # nuExtSSFsize - 1
    size_field = head.count - size_bits
    head.put(1 if static else 0, 1)
    if static:
        head.put(ref_clock, 2)
        head.put(duration_code, 3)
        head.put(1 if timestamp is not None else 0, 1)
        if timestamp is not None:
            head.put(timestamp >> 4, 32)
            head.put(timestamp & 15, 4)
        head.put(presentations - 1, 3)
        head.put(len(assets) - 1, 3)
        for _ in range(presentations):
            head.put((1 << (index + 1)) - 1, index + 1)
        for _ in range(presentations):
            for _ in range(index + 1):
                head.put(1, 8)
        head.put(1 if mix else 0, 1)
        if mix:
            head.put(0, 2)  # nuMixMetadataAdjLevel
            head.put(0, 2)  # 4 bits per mixer output mask
            head.put(0, 2)  # one mixing configuration
            head.put(0x3, 4)
    for a, payload in zip(assets, payloads):
        head.put(a.get("asset_size", len(payload)) - 1, size_bits)
    for a in assets:
        descriptor = asset_descriptor(a, wide, static, mix_enabled=mix)
        head.put(descriptor.value, descriptor.count)
    if not static:
        presentations = 1
    for _ in range(presentations):
        head.put(1 if bc_core else 0, 1)
    if bc_core:
        for _ in range(presentations):
            head.put(0, 2)
            head.put(0, 3)
    # 7.5.1: the components' sync words are DWORD aligned in the substream, so the header, with its CRC-16, ends on a 32-bit boundary.
    if header_pad is None:
        header_pad = (-((head.count + 7) // 8 + 2)) % 4
    head.put(0, header_pad * 8)
    head.align()
    head.put(0, 16)  # nCRC16ExtSSHeader
    raw = bytearray(head.bytes())
    header_size = len(raw)
    assert header_size <= (1 << (12 if wide else 8)), header_size
    body = b"".join(payloads)
    total = size if size is not None else header_size + len(body)
    # nuExtSSHeaderSize - 1 and nuExtSSFsize - 1, written over the placeholders.
    value = int.from_bytes(raw, "big")
    total_bits = len(raw) * 8
    def overwrite(position, width, field):
        nonlocal value
        shift = total_bits - position - width
        value = (value & ~(((1 << width) - 1) << shift)) | (field << shift)
    overwrite(32 + 8 + 2 + 1, 8 + 4 * wide, header_size - 1)
    overwrite(size_field, size_bits, total - 1)
    raw = bytearray(value.to_bytes(len(raw), "big"))
    raw[-2:] = crc16(bytes(raw[5:-2])).to_bytes(2, "big")
    if bad_crc:
        raw[-1] ^= 0xFF
    return bytes(raw) + body + bytes(max(0, total - header_size - len(body)))


# --- MP4 -----------------------------------------------------------------------------------------

def ddts(rate, max_bitrate, avg_bitrate, depth=24, duration=0, construction=1, core_lfe=0, core_layout=2, core_size=0, downmix=0,
         representation=0, layout=0x0003, multi=0, lbr_modifier=0, reserved_box=0, reserved=0):
    """The DTSSpecificBox payload (E.2.2.3.2, 20 bytes): duration is the 2-bit FrameDuration code."""
    bits = Bits()
    bits.put(rate, 32).put(max_bitrate, 32).put(avg_bitrate, 32).put(depth, 8).put(duration, 2).put(construction, 5).put(core_lfe, 1)
    bits.put(core_layout, 6).put(core_size, 14).put(downmix, 1).put(representation, 3).put(layout, 16).put(multi, 1)
    bits.put(lbr_modifier, 1).put(reserved_box, 1).put(reserved, 5)
    return bits.bytes()


# --- DTS-HD files (ATSC A/103:2014 Annex E) -------------------------------------------------------

def dtshd_chunk(chunk_id, payload, size=None, pad=True):
    """A chunk: its 8-character ID, the size (of the payload and the zero bytes that bring the next chunk to a 32-bit boundary, E.2.1) as 64 bits, the payload."""
    payload = bytes(payload)
    if pad:
        payload += bytes(-len(payload) % 4)
    return chunk_id.encode("ascii").ljust(8, b" ") + (len(payload) if size is None else size).to_bytes(8, "big") + payload


def dtshd_header(version=1, clock_code=2, stamp=0, tc_rate=0, vbr=False, smoothing=False, navigation=False, core=True, extension=False, presentations=1,
                 substreams=0, reserved_flags=0, extra=b""):
    """DTSHDHDR (E.2.4, Table E.2): Hdr_Version, the 40-bit time code, TC_Frame_Rate, Bitw_Stream_Metadata, Num_Audio_Presentations, Number_Of_Ext_Sub_Streams."""
    flags = (1 if vbr else 0) | (2 if smoothing else 0) | (4 if navigation else 0) | (8 if core else 0) | (16 if extension else 0) | reserved_flags
    return (version.to_bytes(4, "big") + ((clock_code << 38) | stamp).to_bytes(5, "big") + bytes([tc_rate]) + flags.to_bytes(2, "big")
            + bytes([presentations, substreams]) + extra)


def dtshd_presentation(rate, frames, frame_samples, original_samples, mask, delay=0, index=0, core=False, core_in_extension=False, lossless=False, lbr=False,
                       bc=None, lsb_trim=None, reserved_flags=0, extra=b""):
    """AUPR-HDR (E.2.8, Table E.11); bc is (rate, kbps, mask) of the backward compatible core component, present with core and core_in_extension."""
    flags = (1 if core else 0) | (2 if core_in_extension else 0) | (4 if lossless else 0) | (8 if lbr else 0) | reserved_flags
    out = bytes([index]) + flags.to_bytes(2, "big") + rate.to_bytes(3, "big") + frames.to_bytes(4, "big") + frame_samples.to_bytes(2, "big")
    out += original_samples.to_bytes(5, "big") + mask.to_bytes(2, "big") + delay.to_bytes(2, "big")
    if (flags & 3) == 3:
        bc_rate, bc_kbps, bc_mask = bc
        out += bc_rate.to_bytes(3, "big") + bc_kbps.to_bytes(2, "big") + bc_mask.to_bytes(2, "big")
    if flags & 4:
        out += bytes([lsb_trim or 0])
    return out + extra


def dtshd_core_metadata(rate, kbps, mask, payload):
    """CORESSMD (E.2.6, Table E.8)."""
    return rate.to_bytes(3, "big") + kbps.to_bytes(2, "big") + mask.to_bytes(2, "big") + payload.to_bytes(4, "big")


def dtshd_extension_metadata(avg, vbr, peak=0, buffer=0, payload=0):
    """EXTSS_MD (E.2.7, Table E.10): a variable bit rate adds the peak rate and the smoothing buffer, a constant one the frame payload."""
    out = avg.to_bytes(3, "big")
    return out + (peak.to_bytes(3, "big") + buffer.to_bytes(2, "big") if vbr else payload.to_bytes(4, "big"))


def dtshd_timecode(clock, rate, start, start_residual, reference, reference_residual):
    """TIMECODE (E.2.13, Table E.17)."""
    return (clock.to_bytes(4, "big") + bytes([rate]) + start.to_bytes(8, "big") + start_residual.to_bytes(4, "big") + reference.to_bytes(8, "big")
            + reference_residual.to_bytes(4, "big"))


def dtshd_table(entries, entry_size, interval=None, data=b""):
    """NAVI-TBL (E.2.10, with an interval) or BRANCHPT (E.2.16, without): the entries' count, the interval in frames, the size of an entry, the entries."""
    out = entries.to_bytes(4, "big") + (interval.to_bytes(2, "big") if interval is not None else b"") + bytes([entry_size])
    return out + (data or bytes(entries * entry_size))


def dtshd_text(text):
    """A null-terminated ASCII string (FILEINFO, BUILDVER)."""
    return text.encode("ascii") + b"\0"
