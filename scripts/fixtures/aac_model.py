"""A model of the AAC raw_data_block writer (ISO/IEC 14496-3 4.4.2): it writes syntax elements bit by bit from a description of what
they hold, Huffman-codes scalefactors and spectral values with the codebooks of Annex 4.A (aac_tables.py), and records where every
element lies, which the ADTS CRC (1.A.4.3) and the manifests need. make_mp4_fixtures.py builds the hand-built fixtures with it.

It follows the specification's syntax tables, not the analyzer's C# reader. The streams it writes are valid where the description is
(decoders read them: ffmpeg and faad2 decode the fixtures, and faad2's trace must agree with the analyzer on every block), and break the
syntax exactly where a description asks for it, for the corrupt fixtures.

Standard library only; deterministic.
"""

import math

import aac_tables as T
from mp4_boxes import Bits

ONLY_LONG, LONG_START, EIGHT_SHORT, LONG_STOP = 0, 1, 2, 3
SCE, CPE, CCE, LFE, DSE, PCE, FIL, END = range(8)
EXT_FILL, EXT_FILL_DATA, EXT_DATA_ELEMENT, EXT_DYNAMIC_RANGE, EXT_SBR_DATA, EXT_SBR_DATA_CRC = 0, 1, 2, 11, 13, 14


class Writer(Bits):
    """Bits with a position and Huffman codewords."""

    def code(self, codes_bits, index):
        codes, bits = codes_bits
        return self.put(codes[index], bits[index])


def bands(rate_index, sequence, short_frame=False):
    """The scalefactor band offsets of a window: long (1024 or 960 lines) or short (128 or 120)."""
    if sequence == EIGHT_SHORT:
        table, count = (T.SWB_SHORT_120, T.NUM_SWB_SHORT_120) if short_frame else (T.SWB_SHORT, T.NUM_SWB_SHORT)
    else:
        table, count = (T.SWB_LONG_960, T.NUM_SWB_LONG_960) if short_frame else (T.SWB_LONG, T.NUM_SWB_LONG)
    return table[rate_index][:count[rate_index] + 1]


class Ics:
    """One individual_channel_stream. groups: the lengths of the window groups (short windows), sections: per group a list of
    (codebook, bands); gains: per coded band of a group, in order, the scalefactor (codebooks 1 to 11), intensity position (14, 15) or
    noise energy (13); values: per band of codebooks 1 to 11, its quantized values (band width times group length of them), zeros by
    default; pulse: (start band, [(offset, amplitude)]); tns: per window a list of filters (length, order, direction, compress, coef_res,
    coefficients); gain_control: SSR's (max_band, [per band and window: [(level, location)]])."""

    def __init__(self, sequence=ONLY_LONG, shape=1, max_sfb=0, groups=(1,), sections=None, global_gain=100, gains=None, values=None,
                 pulse=None, tns=None, gain_control=None, predictor=None, reserved=0, raw_sections=None):
        self.sequence = sequence
        self.shape = shape
        self.max_sfb = max_sfb
        self.groups = list(groups) if sequence == EIGHT_SHORT else [1]
        assert sequence != EIGHT_SHORT or sum(self.groups) == 8, groups
        self.sections = sections if sections is not None else [[(0, max_sfb)] if max_sfb else [] for _ in self.groups]
        self.global_gain = global_gain
        self.gains = gains or {}
        self.values = values or {}
        self.pulse = pulse
        self.tns = tns
        self.gain_control = gain_control
        self.predictor = predictor
        self.reserved = reserved
        self.raw_sections = raw_sections

    def codebooks(self):
        """Per group, the codebook of every band below max_sfb."""
        out = []
        for group in self.sections:
            books = []
            for book, length in group:
                books += [book] * length
            out.append(books)
        return out

    def grouping(self):
        bits = 0
        for length in self.groups:
            for i in range(length):
                bits = (bits << 1) | (1 if i > 0 else 0)
        return bits & 0x7F if len(self.groups) else 0


def ics_info(w, ics, object_type, rate_index, common=False):
    w.put(ics.reserved, 1).put(ics.sequence, 2).put(ics.shape, 1)
    if ics.sequence == EIGHT_SHORT:
        w.put(ics.max_sfb, 4)
        # scale_factor_grouping: a bit per window after the first, set where it joins the previous window's group.
        grouping = 0
        windows = []
        for length in ics.groups:
            windows += [0] + [1] * (length - 1)
        for bit in windows[1:]:
            grouping = (grouping << 1) | bit
        w.put(grouping, 7)
        return
    w.put(ics.max_sfb, 6)
    if ics.predictor is None:
        w.put(0, 1)
        return
    w.put(1, 1)
    if object_type == 1:
        reset, group, used = ics.predictor
        w.put(1 if reset else 0, 1)
        if reset:
            w.put(group, 5)
        for sfb in range(min(ics.max_sfb, T.PRED_SFB_MAX[rate_index])):
            w.put(used[sfb] if sfb < len(used) else 0, 1)
    else:
        for ltp in ics.predictor[:2 if common else 1]:
            if ltp is None:
                w.put(0, 1)
                continue
            lag, coef, used = ltp
            w.put(1, 1).put(lag, 11).put(coef, 3)
            for sfb in range(min(ics.max_sfb, 40)):
                w.put(used[sfb] if sfb < len(used) else 0, 1)


def section_data(w, ics):
    if ics.raw_sections is not None:
        for value, width in ics.raw_sections:
            w.put(value, width)
        return
    width = 3 if ics.sequence == EIGHT_SHORT else 5
    escape = (1 << width) - 1
    for group in ics.sections:
        for book, length in group:
            w.put(book, 4)
            while length >= escape:
                w.put(escape, width)
                length -= escape
            w.put(length, width)


def scale_factor_data(w, ics):
    """Differences to the previous value of each kind, Huffman-coded; the first noise energy as 9 bits."""
    scalefactor = ics.global_gain
    position = 0
    noise = ics.global_gain - 90
    first_noise = True
    for g, books in enumerate(ics.codebooks()):
        for sfb, book in enumerate(books):
            if book == 0:
                continue
            value = ics.gains.get((g, sfb))
            if book in (14, 15):
                value = position if value is None else value
                w.code(T.SCALEFACTOR, value - position + 60)
                position = value
            elif book == 13:
                value = noise if value is None else value
                if first_noise:
                    w.put(value - noise + 256, 9)
                    first_noise = False
                else:
                    w.code(T.SCALEFACTOR, value - noise + 60)
                noise = value
            else:
                value = scalefactor if value is None else value
                w.code(T.SCALEFACTOR, value - scalefactor + 60)
                scalefactor = value


def pulse_data(w, pulse):
    start, pulses = pulse
    w.put(len(pulses) - 1, 2).put(start, 6)
    for offset, amplitude in pulses:
        w.put(offset, 5).put(amplitude, 4)


def tns_data(w, ics):
    short = ics.sequence == EIGHT_SHORT
    windows = 8 if short else 1
    for window in range(windows):
        filters = ics.tns[window] if window < len(ics.tns) else []
        w.put(len(filters), 1 if short else 2)
        if not filters:
            continue
        resolution = filters[0][4]                     # coef_res is the window's, not the filter's (4.4.2.7, Table 4.48)
        w.put(resolution, 1)
        for length, order, direction, compress, _, coefficients in filters:
            w.put(length, 4 if short else 6).put(order, 3 if short else 5)
            if order:
                w.put(direction, 1).put(compress, 1)
                bits = 3 + resolution - compress
                for i in range(order):
                    w.put(coefficients[i] if i < len(coefficients) else 0, bits)


def gain_control_data(w, ics):
    max_band, adjustments = ics.gain_control
    windows, first_wide, location = {ONLY_LONG: (1, False, 5), LONG_START: (2, True, 2), EIGHT_SHORT: (8, False, 2), LONG_STOP: (2, True, 5)}[ics.sequence]
    w.put(max_band, 2)
    index = 0
    for _ in range(max_band):
        for wd in range(windows):
            entries = adjustments[index] if index < len(adjustments) else []
            index += 1
            w.put(len(entries), 3)
            for level, place in entries:
                w.put(level, 4).put(place, 4 if wd == 0 and first_wide else location)


def spectral_index(book, values):
    """The codeword index of a pair or quadruple and the values its sign bits and escapes follow for (ISO/IEC 14496-3 4.6.3.3)."""
    if book <= 4:
        if book <= 2:
            assert all(abs(v) <= 1 for v in values)
            index = 27 * (values[0] + 1) + 9 * (values[1] + 1) + 3 * (values[2] + 1) + (values[3] + 1)
            return index, []
        assert all(abs(v) <= 2 for v in values)
        a = [abs(v) for v in values]
        return 27 * a[0] + 9 * a[1] + 3 * a[2] + a[3], values
    if book <= 6:
        assert all(abs(v) <= 4 for v in values)
        return 9 * (values[0] + 4) + (values[1] + 4), []
    modulo, largest = {7: (8, 7), 8: (8, 7), 9: (13, 12), 10: (13, 12), 11: (17, 16)}[book]
    a = [min(abs(v), largest) for v in values]
    assert book == 11 or all(abs(v) <= largest for v in values)
    return modulo * a[0] + a[1], values


def escape(w, value):
    """An escape sequence for a magnitude from 16 to 8191: N ones, a zero, then N + 4 bits of value - 2^(N + 4). A larger magnitude
    gives the longer escape_prefix the specification does not allow, for the corrupt fixtures."""
    n = int(math.floor(math.log2(value))) - 4
    assert n >= 0
    for _ in range(n):
        w.put(1, 1)
    w.put(0, 1)
    w.put(value - (1 << (n + 4)), n + 4)


def spectral_data(w, ics, offsets):
    for g, books in enumerate(ics.codebooks()):
        length = ics.groups[g]
        for sfb, book in enumerate(books):
            if book == 0 or book > 11:
                continue
            count = (offsets[sfb + 1] - offsets[sfb]) * length
            values = ics.values.get((g, sfb), [0] * count)
            assert len(values) == count, (g, sfb, len(values), count)
            dimension = 4 if book <= 4 else 2
            for i in range(0, count, dimension):
                index, signed = spectral_index(book, values[i:i + dimension])
                w.code(T.SPECTRAL[book - 1], index)
                for v in signed:
                    if v:
                        w.put(1 if v < 0 else 0, 1)
                if book == 11:
                    for v in values[i:i + dimension]:
                        if abs(v) >= 16:
                            escape(w, abs(v))


def individual_channel_stream(w, ics, object_type, rate_index, common, short_frame=False):
    """Returns where its global_gain starts."""
    start = w.count
    w.put(ics.global_gain, 8)
    if not common:
        ics_info(w, ics, object_type, rate_index)
    section_data(w, ics)
    scale_factor_data(w, ics)
    w.put(1 if ics.pulse else 0, 1)
    if ics.pulse:
        pulse_data(w, ics.pulse)
    w.put(1 if ics.tns else 0, 1)
    if ics.tns:
        tns_data(w, ics)
    w.put(1 if ics.gain_control else 0, 1)
    if ics.gain_control:
        gain_control_data(w, ics)
    spectral_data(w, ics, bands(rate_index, ics.sequence, short_frame))
    return start


# --- SBR -----------------------------------------------------------------------------------------

def sbr_bands(header, rate):
    """The high and low resolution band counts and the noise floor bands, (high, low, noise), that an SBR header gives at an output rate."""
    # Clean-room rewrite pending: docs/design/aac-sbr-frequency-tables.md
    raise NotImplementedError


def sbr_header(w, header):
    w.put(header.get("amp_res", 1), 1).put(header["start"], 4).put(header["stop"], 4).put(header["xover"], 3).put(0, 2)
    extra1 = any(k in header for k in ("freq_scale", "alter_scale", "noise_bands"))
    w.put(1 if extra1 else 0, 1).put(0, 1)
    if extra1:
        w.put(header.get("freq_scale", 2), 2).put(header.get("alter_scale", 1), 1).put(header.get("noise_bands", 2), 2)


GRID_CLASSES = {"FIXFIX": 0, "FIXVAR": 1, "VARFIX": 2, "VARVAR": 3}


def sbr_grid(w, kind="FIXFIX"):
    """sbr_grid with one envelope, of high frequency resolution. FIXFIX uses 1.5 dB steps for one envelope; the other classes (FIXVAR, VARFIX
    and VARVAR, here without relative borders and with bs_pointer 0) use the header's bs_amp_res."""
    w.put(GRID_CLASSES[kind], 2)
    if kind == "FIXFIX":
        w.put(0, 2).put(1, 1)                        # bs_num_env_raw, bs_freq_res
        return
    w.put(0, 4 if kind == "VARVAR" else 2)           # bs_var_bord_0 and _1, or bs_var_bord
    w.put(0, 4 if kind == "VARVAR" else 2)           # bs_num_rel_0 and _1, or bs_num_rel
    w.put(0, 1).put(1, 1)                            # bs_pointer (one bit for one envelope), bs_freq_res


def codebook_lav(name):
    codes, _ = T.SBR_HUFFMAN[name]
    return (len(codes) - 1) // 2


def sbr_envelope(w, counts, start, balance, coarse=False, time=False):
    """sbr_envelope of one envelope of high frequency resolution: coded across frequency, a start value, then differences of zero, or
    across time, differences of zero from the previous frame."""
    high = counts[0]
    steps = "3_0dB" if coarse else "1_5dB"
    if time:
        name = f"t_huffman_env_{'bal_' if balance else ''}{steps}"
        for _ in range(high):
            w.code(T.SBR_HUFFMAN[name], codebook_lav(name))
        return
    w.put(start, (5 if balance else 6) if coarse else (6 if balance else 7))
    name = f"f_huffman_env_{'bal_' if balance else ''}{steps}"
    for _ in range(high - 1):
        w.code(T.SBR_HUFFMAN[name], codebook_lav(name))


def sbr_noise(w, counts, start, balance, time=False):
    """sbr_noise of one noise floor: coded across frequency, a start value, then differences of zero, or across time."""
    n_q = counts[2]
    if time:
        name = "t_huffman_noise_bal_3_0dB" if balance else "t_huffman_noise_3_0dB"
        for _ in range(n_q):
            w.code(T.SBR_HUFFMAN[name], codebook_lav(name))
        return
    w.put(start, 5)
    name = "f_huffman_env_bal_3_0dB" if balance else "f_huffman_env_3_0dB"
    for _ in range(n_q - 1):
        w.code(T.SBR_HUFFMAN[name], codebook_lav(name))


def sbr_harmonics(w, counts, present):
    """bs_add_harmonic_flag and, if set, a bs_add_harmonic bit per high resolution band, none of them set."""
    w.put(1 if present else 0, 1)
    if present:
        w.put(0, counts[0])


def sbr_fixvar_grid(w, resolutions):
    """sbr_grid of a FIXVAR frame (4.4.2.8): bs_var_bord 0, the relative borders (each 2 bits, value 0), bs_pointer 0 and then bs_freq_res
    of the LAST envelope first, which is what a reader that takes them in envelope order gets wrong. resolutions are in envelope order."""
    count = len(resolutions)
    w.put(GRID_CLASSES["FIXVAR"], 2).put(0, 2).put(count - 1, 2)
    for _ in range(count - 1):
        w.put(0, 2)
    w.put(0, (1, 2, 2, 3, 3)[count - 1])
    for resolution in reversed(resolutions):
        w.put(resolution, 1)


def sbr_fixvar_data(w, counts, channels):
    """sbr_data of a single channel element (one entry in channels) or a channel pair without coupling (two) whose grids are FIXVAR frames of
    several envelopes. Each channel is a list of (frequency resolution, difference) per envelope: the envelope is coded across frequency with
    that resolution's band count, the differences being the same size with alternating signs, so the bits an envelope takes depend on its resolution and a reader
    that pairs the resolutions with the wrong envelopes finds another length. Two noise floors, coded across frequency (3 dB header)."""
    high, low, n_q = counts
    pair = len(channels) == 2
    w.put(0, 1)                                      # bs_data_extra
    if pair:
        w.put(0, 1)                                  # bs_coupling
    for channel in channels:
        sbr_fixvar_grid(w, [r for r, _ in channel])
    for channel in channels:
        for _ in channel:
            w.put(0, 1)                              # bs_df_env: across frequency
        w.put(0, 1).put(0, 1)                        # bs_df_noise of the two noise floors
    for _ in channels:
        w.put(0, 2 * n_q)                            # bs_invf_mode
    for channel in channels:
        for resolution, difference in channel:
            name = "f_huffman_env_3_0dB"
            w.put(31, 6)
            for band in range((high if resolution else low) - 1):
                # the sign alternates, so the values stay in range whatever the difference
                w.code(T.SBR_HUFFMAN[name], codebook_lav(name) + (difference if band % 2 == 0 else -difference))
    for _ in channels:
        for _ in range(2):
            sbr_noise(w, counts, 10, False)
    for _ in channels:
        w.put(0, 1)                                  # bs_add_harmonic_flag
    w.put(0, 1)                                      # bs_extended_data


def sbr_data(w, counts, pair=False, coupled=False, ps=False, grid="FIXFIX", data_extra=False, time_env=False, time_noise=False, harmonic=False,
             extension=False):
    """sbr_single_channel_element or sbr_channel_pair_element (4.4.2), then the extended data, here a parametric stereo extension of a
    PS header that enables nothing and no envelopes, so a decoder upmixes the mono core plainly. The options are the optional tools a
    stream may use: extra data bits, the frame class of the grid, envelopes and noise floors coded across time (of the first channel),
    sinusoids (of the first channel) and an extension of another ID than parametric stereo's."""
    n_q = counts[2]
    coarse = grid != "FIXFIX"
    if not pair:
        w.put(1 if data_extra else 0, 1)             # bs_data_extra
        if data_extra:
            w.put(0, 4)                              # bs_reserved
        sbr_grid(w, grid)
        w.put(1 if time_env else 0, 1).put(1 if time_noise else 0, 1)   # bs_df_env, bs_df_noise
        w.put(0, 2 * n_q)                            # bs_invf_mode
        sbr_envelope(w, counts, 40, False, coarse, time_env)
        sbr_noise(w, counts, 10, False, time_noise)
        sbr_harmonics(w, counts, harmonic)
    elif coupled:
        w.put(1 if data_extra else 0, 1)
        if data_extra:
            w.put(0, 8)
        w.put(1, 1)                                  # bs_coupling
        sbr_grid(w, grid)
        w.put(1 if time_env else 0, 1).put(1 if time_noise else 0, 1)   # sbr_dtdf of the first channel
        w.put(0, 2)                                  # and of the second
        w.put(0, 2 * n_q)
        sbr_envelope(w, counts, 40, False, coarse, time_env)
        sbr_noise(w, counts, 10, False, time_noise)
        sbr_envelope(w, counts, 12, True, coarse)
        sbr_noise(w, counts, 6, True)
        sbr_harmonics(w, counts, harmonic)
        sbr_harmonics(w, counts, False)
    else:
        w.put(1 if data_extra else 0, 1)
        if data_extra:
            w.put(0, 8)
        w.put(0, 1)
        sbr_grid(w, grid)
        sbr_grid(w, grid)
        w.put(1 if time_env else 0, 1).put(1 if time_noise else 0, 1)
        w.put(0, 2)
        w.put(0, 4 * n_q)
        sbr_envelope(w, counts, 40, False, coarse, time_env)
        sbr_envelope(w, counts, 40, False, coarse)
        sbr_noise(w, counts, 10, False, time_noise)
        sbr_noise(w, counts, 10, False)
        sbr_harmonics(w, counts, harmonic)
        sbr_harmonics(w, counts, False)
    if ps:
        w.put(1, 1).put(2, 4)                        # bs_extended_data, two bytes of extensions
        w.put(2, 2)                                  # bs_extension_id EXT_ID_PS
        # ps_data: enable_ps_header, enable_iid 0, enable_icc 0, enable_ext 0, frame_class 0, num_env_idx 0.
        w.put(1, 1).put(0, 1).put(0, 1).put(0, 1).put(0, 1).put(0, 2)
        w.put(0, 7)                                  # bs_fill_bits
    elif extension:
        w.put(1, 1).put(1, 4)                        # bs_extended_data, one byte of extensions
        w.put(0, 2).put(0, 6)                        # bs_extension_id 0, which defines nothing, and its bits
    else:
        w.put(0, 1)


class Element:
    """What an element of a raw data block holds, by its kind."""

    def __init__(self, kind, **fields):
        self.kind = kind
        self.fields = fields


def raw_data_block(elements, object_type=2, rate_index=4, sbr_rate=None, short_frame=False):
    """The bytes of a raw data block and its elements' spans: (id, start bit, end bit, second channel's start)."""
    w = Writer()
    spans = []
    for element in elements:
        f = element.fields
        start = w.count
        second = -1
        if element.kind in (SCE, LFE):
            w.put(element.kind, 3).put(f.get("tag", 0), 4)
            individual_channel_stream(w, f["ics"], object_type, rate_index, False, short_frame)
        elif element.kind == CPE:
            w.put(CPE, 3).put(f.get("tag", 0), 4)
            first, other = f["ics"]
            common = f.get("common", True)
            w.put(1 if common else 0, 1)
            if common:
                ics_info(w, first, object_type, rate_index, common=True)
                mask = f.get("mask", 0)
                w.put(mask, 2)
                if mask == 1:
                    for group in f["ms_used"]:
                        for used in group:
                            w.put(used, 1)
            individual_channel_stream(w, first, object_type, rate_index, common, short_frame)
            second = individual_channel_stream(w, other, object_type, rate_index, common, short_frame)
        elif element.kind == CCE:
            w.put(CCE, 3).put(f.get("tag", 0), 4)
            w.put(f.get("independent", 0), 1)
            targets = f["targets"]
            w.put(len(targets) - 1, 3)
            lists = 0
            for is_pair, tag, left, right in targets:
                lists += 1
                w.put(is_pair, 1).put(tag, 4)
                if is_pair:
                    w.put(left, 1).put(right, 1)
                    lists += 1 if left and right else 0
            w.put(f.get("domain", 0), 1).put(0, 1).put(0, 2)
            ics = f["ics"]
            individual_channel_stream(w, ics, object_type, rate_index, False, short_frame)
            for _ in range(1, lists):
                if not f.get("independent", 0):
                    w.put(1, 1)                      # common_gain_element_present
                w.code(T.SCALEFACTOR, 60)
        elif element.kind == DSE:
            data = f["data"]
            w.put(DSE, 3).put(f.get("tag", 0), 4).put(1 if f.get("aligned") else 0, 1)
            w.put(min(len(data), 255), 8)
            if len(data) >= 255:
                w.put(len(data) - 255, 8)
            if f.get("aligned"):
                w.align()
            for byte in data:
                w.put(byte, 8)
        elif element.kind == PCE:
            w.put(PCE, 3)
            f["write"](w)
        elif element.kind == FIL:
            payload = f["payload"]
            count = len(payload)
            w.put(FIL, 3)
            if count < 15:
                w.put(count, 4)
            else:
                w.put(15, 4).put(count - 14, 8)
            for byte in payload:
                w.put(byte, 8)
        spans.append((element.kind, start, w.count, second))
    end = w.count
    w.put(END, 3)
    spans.append((END, end, w.count, -1))
    w.align()
    return w.bytes(), spans


def payload_bytes(writer_function, count=None):
    """An extension payload (or several) as the fill element's bytes: what writer_function writes, from its 4-bit type on, padded with
    zero bits to whole bytes, or to count bytes."""
    w = Writer()
    writer_function(w)
    w.align()
    data = w.bytes()
    if count is not None:
        assert len(data) <= count, (len(data), count)
        data += bytes(count - len(data))
    return data


def sbr_payload(counts, header=None, pair=False, coupled=False, ps=False, crc=False, **tools):
    def write(w):
        w.put(EXT_SBR_DATA_CRC if crc else EXT_SBR_DATA, 4)
        if crc:
            w.put(0, 10)
        w.put(1 if header else 0, 1)
        if header:
            sbr_header(w, header)
        if "fixvar" in tools:
            sbr_fixvar_data(w, counts, tools["fixvar"])
        else:
            sbr_data(w, counts, pair, coupled, ps, **tools)
    return payload_bytes(write)


def fill_data_payload(count, nibble=0, byte=0xA5):
    def write(w):
        w.put(EXT_FILL_DATA, 4).put(nibble, 4)
        for _ in range(count - 1):
            w.put(byte, 8)
    return payload_bytes(write)


def other_payload(text, kind=EXT_FILL):
    """EXT_FILL (or another type) with its other bits: four zero bits, then the bytes, so they lie on byte boundaries."""
    def write(w):
        w.put(kind, 4).put(0, 4)
        for byte in text:
            w.put(byte, 8)
    return payload_bytes(write)


def data_element_payload(data):
    def write(w):
        w.put(EXT_DATA_ELEMENT, 4).put(0, 4)         # data_element_version ANC_DATA
        length = len(data)
        while length >= 255:
            w.put(255, 8)
            length -= 255
        w.put(length, 8)
        for byte in data:
            w.put(byte, 8)
    return payload_bytes(write)


def drc_payload(bands=((0x40, 1, 8),), reference=80, pce_tag=None, excluded=None):
    """dynamic_range_info: bands as (top, sign, control), the first band's top ignored where there is one band."""
    def write(w):
        w.put(EXT_DYNAMIC_RANGE, 4)
        w.put(1 if pce_tag is not None else 0, 1)
        if pce_tag is not None:
            w.put(pce_tag, 4).put(0, 4)
        w.put(1 if excluded else 0, 1)
        if excluded:
            for i, mask in enumerate(excluded):
                w.put(mask, 7).put(1 if i + 1 < len(excluded) else 0, 1)
        w.put(1 if len(bands) > 1 else 0, 1)
        if len(bands) > 1:
            w.put(len(bands) - 1, 4).put(0, 4)
            for top, _, _ in bands:
                w.put(top, 8)
        w.put(1 if reference is not None else 0, 1)
        if reference is not None:
            w.put(reference, 7).put(0, 1)
        for _, sign, control in bands:
            w.put(sign, 1).put(control, 7)
    return payload_bytes(write)


def crc_regions(block, spans):
    """The bits of a raw data block the ADTS CRC covers after the header (1.A.4.3, as fdk-aac's decoder reads it): from each element's
    tag, the first 192 bits of a single channel, LFE or coupling element and of a channel pair, the first 128 bits of the pair's
    second channel from its global_gain, filled up with zero bits where they are shorter, and the whole of a program config or data
    stream element."""
    out = []

    def bits(start, count):
        return [((block[(start + i) // 8] >> (7 - (start + i) % 8)) & 1, 1) for i in range(count)]

    for kind, start, end, second in spans:
        tag = start + 3
        if kind in (SCE, LFE, CCE, CPE):
            length = min(end - tag, 192)
            out += bits(tag, length) + [(0, 1)] * (192 - length)
            if kind == CPE:
                length = min(end - second, 128)
                out += bits(second, length) + [(0, 1)] * (128 - length)
        elif kind in (DSE, PCE):
            out += bits(tag, end - tag)
    return out
