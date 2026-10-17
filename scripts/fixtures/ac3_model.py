"""AC-3 and E-AC-3 syncframes for the fixture scripts: headers read as the analyzer's SyncFrame reads
them (ATSC A/52:2018 5.3.1 and 5.3.2, Annex E 2.2.2), the CRCs of 7.10.1, the channel layouts of
Table 5.8 and Annex E Table E2.5, and frames built bit by bit that decoders play as (near) silence.

A built frame sends every exponent once, in block 0, with all SNR offsets 0, so by the special case
of 7.2.2.1.1 no mantissa takes a bit; every other field is written as A/52 or Annex E lays it out,
so the frames exercise the bit stream information and the audio block syntax without a bit
allocation in the generator. liba52 and ffmpeg decode them.
"""

from mp4_boxes import Bits

SYNC = 0x0B77
RATES = [48000, 44100, 32000]
REDUCED = [24000, 22050, 16000]
WORDS = {
    0: [64, 64, 80, 80, 96, 96, 112, 112, 128, 128, 160, 160, 192, 192, 224, 224, 256, 256, 320, 320, 384, 384, 448, 448, 512, 512, 640, 640, 768, 768,
        896, 896, 1024, 1024, 1152, 1152, 1280, 1280],
    1: [69, 70, 87, 88, 104, 105, 121, 122, 139, 140, 174, 175, 208, 209, 243, 244, 278, 279, 348, 349, 417, 418, 487, 488, 557, 558, 696, 697, 835, 836,
        975, 976, 1114, 1115, 1253, 1254, 1393, 1394],
    2: [96, 96, 120, 120, 144, 144, 168, 168, 192, 192, 240, 240, 288, 288, 336, 336, 384, 384, 480, 480, 576, 576, 672, 672, 768, 768, 960, 960, 1152, 1152,
        1344, 1344, 1536, 1536, 1728, 1728, 1920, 1920],
}
NFCHANS = [2, 1, 2, 3, 3, 4, 4, 5]
DEFAULT_CPL_BANDS = [0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 0, 1, 1, 1, 1, 1]


def crc16(data):
    crc = 0
    for byte in data:
        crc ^= byte << 8
        for _ in range(8):
            crc = ((crc << 1) ^ 0x8005) & 0xFFFF if crc & 0x8000 else (crc << 1) & 0xFFFF
    return crc


def fix_crc2(frame):
    """Writes the last word so the CRC over the frame after the sync word comes to zero."""
    frame = bytearray(frame)
    frame[-2:] = crc16(bytes(frame[2:-2])).to_bytes(2, "big")
    return bytes(frame)


def fix_crc1(frame):
    """Writes crc1 so the CRC over the first 5/8 of an AC-3 frame after the sync word comes to zero:
    the CRC is linear, so crc1 solves 16 equations over GF(2)."""
    frame = bytearray(frame)
    words = len(frame) // 2
    end = ((words >> 1) + (words >> 3)) * 2
    frame[2:4] = b"\0\0"
    target = crc16(bytes(frame[2:end]))
    zeros = bytes(end - 4)
    columns = [crc16((1 << k).to_bytes(2, "big") + zeros) for k in range(16)]
    # Solve sum(c_k * columns[k]) == target by elimination.
    rows = [(columns, target)]
    pivots = []
    basis = []
    for k in range(16):
        vector, combination = columns[k], 1 << k
        for pivot_bit, (pv, pc) in zip(pivots, basis):
            if vector & pivot_bit:
                vector ^= pv
                combination ^= pc
        if vector:
            pivot = vector & -vector
            pivots.append(pivot)
            basis.append((vector, combination))
    value, solution = target, 0
    for pivot_bit, (pv, pc) in zip(pivots, basis):
        if value & pivot_bit:
            value ^= pv
            solution ^= pc
    assert value == 0 and rows
    frame[2:4] = solution.to_bytes(2, "big")
    assert crc16(bytes(frame[2:end])) == 0
    return bytes(frame)


def header(data, at=0):
    """The fields of the frame at data[at:], as the analyzer's SyncFrame gives them, or None."""
    if at + 8 > len(data) or int.from_bytes(data[at:at + 2], "big") != SYNC:
        return None
    raw = int.from_bytes(data[at:at + 8], "big")
    bsid = (raw >> 19) & 0x1F
    if bsid <= 10:
        fscod, code = (raw >> 30) & 3, (raw >> 24) & 0x3F
        if fscod == 3 or code >= 38:
            return None
        acmod = (raw >> 13) & 7
        bit = 51 + (2 if acmod & 1 and acmod != 1 else 0) + (2 if acmod & 4 else 0) + (2 if acmod == 2 else 0)
        return {"eac3": False, "bsid": bsid, "length": WORDS[fscod][code] * 2, "rate": RATES[fscod] >> max(0, bsid - 8), "blocks": 6,
                "acmod": acmod, "lfeon": (raw >> (63 - bit)) & 1, "dependent": False, "substream": 0, "bitrate_code": code >> 1, "fscod": fscod}
    strmtyp, substream, frmsiz = (raw >> 46) & 3, (raw >> 43) & 7, (raw >> 32) & 0x7FF
    fscod, code2 = (raw >> 30) & 3, (raw >> 28) & 3
    if bsid > 16 or strmtyp == 3 or (fscod == 3 and code2 == 3) or (frmsiz + 1) * 2 < 8:
        return None
    blocks = 6 if fscod == 3 else [1, 2, 3, 6][code2]
    rate = REDUCED[code2] if fscod == 3 else RATES[fscod]
    return {"eac3": True, "bsid": bsid, "length": (frmsiz + 1) * 2, "rate": rate, "blocks": blocks, "acmod": (raw >> 25) & 7, "lfeon": (raw >> 24) & 1,
            "dependent": strmtyp == 1, "substream": substream, "strmtyp": strmtyp, "fscod": fscod}


def walk(data):
    """The frames of a file that is frames from its first byte to its last: (offset, header)."""
    frames = []
    at = 0
    while at < len(data):
        h = header(data, at)
        assert h, f"no frame at {at}"
        frames.append((at, h))
        at += h["length"]
    return frames


def crc_verdicts(frame, eac3):
    """(crc1 holds or None for E-AC-3, crc2 holds)."""
    words = len(frame) // 2
    first = None if eac3 else crc16(frame[2:((words >> 1) + (words >> 3)) * 2]) == 0
    return first, crc16(frame[2:]) == 0


FL, FR, FC, LFE = "front left", "front right", "front center", "LFE"
BL, BR, BC, SL, SR = "back left", "back right", "back center", "side left", "side right"


def speakers(acmod, lfeon, rear_pair=False):
    surround = [SL, SR] if rear_pair else [BL, BR]
    front = [["discrete", "discrete"], [FC], [FL, FR], [FL, FC, FR], [FL, FR, BC], [FL, FC, FR, BC], [FL, FR] + surround, [FL, FC, FR] + surround][acmod]
    return front + ([LFE] if lfeon else [])


# The stream's channel mode as the report writes it: the arrangement alone, as for DTS; the speakers are in the channel layout.
CODING_MODES = ["1+1", "1/0", "2/0", "3/0", "2/1", "3/1", "2/2", "3/2"]


def exponents(bits, end, absolute=15, strategy=3, gain_range=True):
    """An exponent set of a full bandwidth channel with every difference 0 (each group 62)."""
    groups = {1: (end - 1) // 3, 2: (end - 1 + 3) // 6, 3: (end - 1 + 9) // 12}[strategy]
    bits.put(absolute, 4)
    for _ in range(groups):
        bits.put(62, 7)
    if gain_range:
        bits.put(0, 2)


def finish(bits, words, crc1, aux=None, tail=None):
    """Pads the frame with zero bits to its last word (auxdatae, crcrsv or encinfo 0) and sets the CRCs.
    aux (bytes) is the auxiliary data field: auxdatal and auxdatae (A/52 5.4.4) follow it, and the
    zero padding comes before it."""
    if tail is not None:
        # Bytes right after the last audio block, in bits no field claims; auxdatae stays 0.
        for byte in tail:
            bits.put(byte, 8)
    if aux is not None:
        assert bits.count <= words * 16 - 32 - len(aux) * 8, (bits.count, words * 16, len(aux))
        bits.put(0, words * 16 - 32 - len(aux) * 8 - bits.count)
        for byte in aux:
            bits.put(byte, 8)
        bits.put(len(aux) * 8, 14).put(1, 1).put(0, 1).put(0, 16)
        frame = bits.bytes()
        return fix_crc2(frame)
    assert bits.count <= words * 16 - 16, (bits.count, words * 16)
    bits.put(0, words * 16 - 16 - bits.count)
    bits.put(0, 16)
    frame = bits.bytes()
    if crc1:
        frame = fix_crc1(frame)
    return fix_crc2(frame)


def ac3_frame(acmod=2, lfeon=0, fscod=0, frmsizecod=20, bsid=8, bsmod=0, dialnorm=27, cmixlev=0, surmixlev=0, dsurmod=0, compr=None, langcod=None,
              audprod=None, timecod1=None, timecod2=None, xbsi1=None, xbsi2=None, addbsi=None, rematstr0=1, chbwcod=0, origbs=1, copyright=0, dynrng=None,
              ends=None):
    b = Bits()
    b.put(SYNC, 16).put(0, 16).put(fscod, 2).put(frmsizecod, 6)
    b.put(bsid, 5).put(bsmod, 3).put(acmod, 3)
    if acmod & 1 and acmod != 1:
        b.put(cmixlev, 2)
    if acmod & 4:
        b.put(surmixlev, 2)
    if acmod == 2:
        b.put(dsurmod, 2)
    b.put(lfeon, 1)
    for _ in range(2 if acmod == 0 else 1):
        b.put(dialnorm, 5)
        b.put(compr is not None, 1)
        if compr is not None:
            b.put(compr, 8)
        b.put(langcod is not None, 1)
        if langcod is not None:
            b.put(langcod, 8)
        b.put(audprod is not None, 1)
        if audprod is not None:
            b.put(audprod[0], 5).put(audprod[1], 2)
    b.put(copyright, 1).put(origbs, 1)
    if bsid == 6:
        b.put(xbsi1 is not None, 1)
        if xbsi1 is not None:
            b.put(xbsi1[0], 2).put(xbsi1[1], 3).put(xbsi1[2], 3).put(xbsi1[3], 3).put(xbsi1[4], 3)
        b.put(xbsi2 is not None, 1)
        if xbsi2 is not None:
            b.put(xbsi2[0], 2).put(xbsi2[1], 2).put(xbsi2[2], 1).put(xbsi2[3], 8).put(xbsi2[4], 1)
    else:
        for code in (timecod1, timecod2):
            b.put(code is not None, 1)
            if code is not None:
                b.put(code, 14)
    b.put(addbsi is not None, 1)
    if addbsi is not None:
        b.put(len(addbsi) - 1, 6)
        for byte in addbsi:
            b.put(byte, 8)
    nf = NFCHANS[acmod]
    end = (chbwcod + 12) * 3 + 37
    for block in range(6):
        b.put(0, nf).put(0, nf)
        for _ in range(2 if acmod == 0 else 1):
            b.put(dynrng is not None and block == 0, 1)
            if dynrng is not None and block == 0:
                b.put(dynrng, 8)
        b.put(block == 0, 1)
        if block == 0:
            b.put(0, 1)
        if acmod == 2:
            strategy = rematstr0 if block == 0 else 0
            b.put(strategy, 1)
            if strategy:
                b.put(0, 4)
        for _ in range(nf):
            b.put(3 if block == 0 else 0, 2)
        if lfeon:
            b.put(block == 0, 1)
        if block == 0:
            for _ in range(nf):
                b.put(chbwcod, 6)
            for _ in range(nf):
                exponents(b, end)
            if lfeon:
                b.put(15, 4).put(62, 7).put(62, 7)
            b.put(1, 1).put(2, 2).put(1, 2).put(1, 2).put(2, 2).put(7, 3)
            b.put(1, 1).put(0, 6)
            for _ in range(nf + lfeon):
                b.put(0, 4).put(4, 3)
        else:
            b.put(0, 1).put(0, 1)
        b.put(0, 1).put(0, 1)
        if ends is not None:
            ends.append(b.count)
    return finish(b, WORDS[fscod][frmsizecod], crc1=True)


def eac3_frame(strmtyp=0, substream=0, acmod=2, lfeon=0, numblkscod=3, fscod=0, fscod2=0, bsid=16, dialnorm=27, chanmap=None, bsmod=None, words=256,
               expstre=1, ahte=0, aht_channel=0, spx=False, cplinu=False, ecpl=False, ends=None, addbsi=None, aux=None, skips=None, tail=None):
    nf = NFCHANS[acmod]
    blocks = 6 if fscod == 3 else [1, 2, 3, 6][numblkscod]
    b = Bits()
    b.put(SYNC, 16).put(strmtyp, 2).put(substream, 3).put(words - 1, 11).put(fscod, 2)
    b.put(fscod2 if fscod == 3 else numblkscod, 2)
    b.put(acmod, 3).put(lfeon, 1).put(bsid, 5)
    for _ in range(2 if acmod == 0 else 1):
        b.put(dialnorm, 5).put(0, 1)
    if strmtyp == 1:
        b.put(chanmap is not None, 1)
        if chanmap is not None:
            b.put(chanmap, 16)
    b.put(0, 1)
    b.put(bsmod is not None, 1)
    if bsmod is not None:
        b.put(bsmod, 3).put(0, 1).put(1, 1)
        if acmod == 2:
            b.put(0, 2).put(0, 2)
        if acmod >= 6:
            b.put(0, 2)
        b.put(0, 1)
        if acmod == 0:
            b.put(0, 1)
        if fscod < 3:
            b.put(0, 1)
    if strmtyp == 0 and blocks != 6:
        b.put(1, 1)
    b.put(addbsi is not None, 1)
    if addbsi is not None:
        b.put(len(addbsi) - 1, 6)
        for byte in addbsi:
            b.put(byte, 8)
    # audfrm (Annex E 2.2.3): frame-wide SNR offsets of 0, no optional syntax.
    if blocks == 6:
        b.put(expstre, 1).put(ahte, 1)
    b.put(0, 2).put(0, 1).put(0, 1).put(0, 1).put(0, 1).put(0, 1).put(0, 1).put(skips is not None, 1).put(0, 1)
    if acmod > 1:
        b.put(cplinu, 1)
        for _ in range(1, blocks):
            b.put(0, 1)
    if expstre:
        for block in range(blocks):
            if cplinu:
                b.put(3 if block == 0 else 0, 2)
            for _ in range(nf):
                b.put(3 if block == 0 else 0, 2)
    else:
        if acmod > 1 and cplinu:
            b.put(0, 5)
        for _ in range(nf):
            b.put(0, 5)
    if lfeon:
        for block in range(blocks):
            b.put(block == 0, 1)
    if strmtyp == 0:
        if blocks != 6:
            b.put(0, 1)
        else:
            for _ in range(nf):
                b.put(0, 5)
    if ahte:
        for ch in range(nf):
            b.put(aht_channel if ch == 0 else 0, 1)
        if lfeon:
            b.put(0, 1)
    b.put(0, 6).put(0, 4)
    if blocks > 1:
        b.put(0, 1)
    strategy = 3 if expstre else 1
    cplbegf, cplendf = 3, 10
    for block in range(blocks):
        b.put(0, 1)
        if acmod == 0:
            b.put(0, 1)
        b.put(spx if block == 0 else 0, 1)
        if spx and block == 0:
            if acmod != 1:
                b.put((1 << nf) - 1, nf)
            b.put(0, 2).put(2, 3).put(2, 3).put(0, 1)
        if spx:
            for _ in range(nf):
                if block == 0:
                    b.put(0, 5).put(0, 2)
                    for _ in range(3):
                        b.put(0, 4).put(0, 2)
                else:
                    b.put(0, 1)
        if cplinu:
            if block == 0:
                b.put(ecpl, 1)
                if acmod != 2:
                    b.put((1 << nf) - 1, nf)
                if ecpl:
                    break
                if acmod == 2:
                    b.put(0, 1)
                b.put(cplbegf, 4)
                if not spx:
                    b.put(cplendf, 4)
                b.put(0, 1)
                bands = 3 + cplendf - cplbegf - sum(DEFAULT_CPL_BANDS[cplbegf + 1:cplendf + 3])
                for _ in range(nf):
                    b.put(0, 2)
                    for _ in range(bands):
                        b.put(0, 4).put(0, 4)
            else:
                for _ in range(nf):
                    b.put(0, 1)
        if acmod == 2:
            if block == 0:
                b.put(0, 4 if not cplinu else (2 if cplbegf == 0 else 3 if cplbegf < 3 else 4))
            else:
                b.put(0, 1)
        if block == 0:
            if not cplinu and not spx:
                for _ in range(nf):
                    b.put(0, 6)
            end = 37 + 12 * cplbegf if cplinu else 25 + 12 * 4 if spx else 73
            if cplinu:
                b.put(0, 4)
                for _ in range((37 + 12 * (cplendf + 3) - end) // ((3, 6, 12)[strategy - 1])):
                    b.put(62, 7)
            for _ in range(nf):
                exponents(b, end, strategy=strategy)
            if lfeon:
                b.put(15, 4).put(62, 7).put(62, 7)
        if strmtyp == 0:
            b.put(0, 1)
        if cplinu:
            if block == 0:
                b.put(0, 3).put(0, 3)
            else:
                b.put(0, 1)
        if skips is not None:
            # skiple, and skipl bytes of skip field (A/52 5.4.3.58 to 5.4.3.60).
            piece = skips.get(block)
            b.put(piece is not None, 1)
            if piece is not None:
                b.put(len(piece), 9)
                for byte in piece:
                    b.put(byte, 8)
        if ahte and aht_channel:
            break
        if ends is not None:
            ends.append(b.count)
    return finish(b, words, crc1=False, aux=aux, tail=tail)


def silent_dac3(fscod, bsid, bsmod, acmod, lfeon, bit_rate_code, reserved=0):
    return Bits().put(fscod, 2).put(bsid, 5).put(bsmod, 3).put(acmod, 3).put(lfeon, 1).put(bit_rate_code, 5).put(reserved, 5).bytes()


def dec3(data_rate, substreams, extension=None):
    """substreams: (fscod, bsid, asvc, bsmod, acmod, lfeon, num_dep_sub, chan_loc) per independent substream.
    extension: (flag_ec3_extension_type_a, complexity_index_type_a[, reserved]) after the substreams, as ETSI
    TS 103 420 C.3.1 appends them."""
    b = Bits().put(data_rate, 13).put(len(substreams) - 1, 3)
    for fscod, bsid, asvc, bsmod, acmod, lfeon, dependents, chan_loc in substreams:
        b.put(fscod, 2).put(bsid, 5).put(0, 1).put(asvc, 1).put(bsmod, 3).put(acmod, 3).put(lfeon, 1).put(0, 3).put(dependents, 4)
        if dependents:
            b.put(chan_loc, 9)
        else:
            b.put(0, 1)
    if extension is not None:
        b.put(extension[2] if len(extension) > 2 else 0, 7).put(extension[0], 1).put(extension[1], 8)
    return b.align().bytes()
