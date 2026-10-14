"""Hand-built fixtures of AAC coding, written with the bitstream model (aac_model.py): raw data blocks with every window sequence and
shape, window grouping, every Huffman codebook with escapes, mid/side masks 0, 1 and 2, intensity stereo in and out of phase, noise
substitution, TNS, pulse data, AAC Main prediction, LTP, SSR gain control, coupling, data stream elements, fill elements with SBR
signaled only implicitly, parametric stereo, dynamic range control, fill data and ancillary data, ADTS CRCs over channel pairs and short
elements, and corrupt variants. make_mp4_fixtures.py writes them with the others. Each manifest lists fields the model wrote, statistics
it counts from what it wrote, and the findings for what it breaks on purpose.
"""

import aac_model as M
from mp4_boxes import Bits, Track, asc, esds, movie, mp4a, rate_index

RATE = 44100
LAV = {1: 1, 2: 1, 3: 2, 4: 2, 5: 4, 6: 4, 7: 7, 8: 7, 9: 12, 10: 12, 11: 16}
WINDOW_NAMES = ["Only Long", "Long Start", "Eight Short", "Long Stop"]


def pattern(book, count, seed):
    """Quantized values for a band of a codebook: every value it can code in turn; codebook 11 adds escapes up to 8191."""
    largest = LAV[book]
    span = 2 * largest + 1
    values = [((i * 7 + seed * 3) % span) - largest for i in range(count)]
    if book == 11:
        for at, value in zip(range(1, count, 5), [16, 17, 31, 255, 1000, -8191, 64, -100]):
            values[at] = value
    return values


def channel(sequence, shape, sections, groups=(1,), gain=100, seed=0, rate=RATE, **options):
    """An Ics whose bands of codebooks 1 to 11 hold patterns and whose scalefactors vary around its global gain."""
    offsets = M.bands(rate_index(rate), sequence)
    max_sfb = sum(length for _, length in sections[0]) if sections else 0
    ics = M.Ics(sequence, shape, max_sfb, groups, sections or None, gain, **options)
    for g, books in enumerate(ics.codebooks()):
        for sfb, book in enumerate(books):
            if 1 <= book <= 11:
                ics.values.setdefault((g, sfb), pattern(book, (offsets[sfb + 1] - offsets[sfb]) * ics.groups[g], seed + sfb + g))
                ics.gains.setdefault((g, sfb), gain + ((sfb + g) % 5) - 2)
            elif book == 13:
                ics.gains.setdefault((g, sfb), gain - 90 + 4 + (sfb % 3))
            elif book in (14, 15):
                ics.gains.setdefault((g, sfb), (sfb % 7) - 3)
    return ics


EVERY_BOOK = [[(11, 3), (1, 2), (2, 2), (3, 2), (4, 2), (5, 2), (6, 2), (7, 2), (8, 2), (9, 2), (10, 2), (11, 4), (0, 3), (13, 4), (11, 15)]]
SHORT_SECTIONS = [[(11, 2), (5, 3), (1, 4), (0, 5)], [(9, 4), (3, 6), (13, 4)], [(10, 3), (7, 4), (2, 7)], [(4, 6), (6, 6), (8, 2)]]


# --- What the analyzer shows for what the model wrote ------------------------------------------------

def tenths(part, whole):
    """A share in tenths of a percent, rounded half to even, as text."""
    q, r = divmod(part * 1000, whole)
    if r * 2 > whole or (r * 2 == whole and q % 2):
        q += 1
    return f"{q // 10}.{q % 10}"


def share(part, whole, unit):
    return f"{part} {unit}{'' if part == 1 else 's'} ({tenths(part, whole)}%)"


def band_text(flags_per_group):
    """Bands as ranges, per group where there are several."""
    parts = []
    for g, flags in enumerate(flags_per_group):
        ranges = []
        run = None
        for b, on in enumerate(list(flags) + [False]):
            if on and run is None:
                run = b
            elif not on and run is not None:
                ranges.append(str(run) if run == b - 1 else f"{run}-{b - 1}")
                run = None
        if ranges:
            parts.append((f"group {g}: " if len(flags_per_group) > 1 else "") + ", ".join(ranges))
    return "; ".join(parts) if parts else "none"


def statistics(prefix, streams):
    """The window and codebook statistics of channel streams (Ics objects of single channel and channel pair elements)."""
    fields = {}
    total = len(streams)
    for sequence, name in enumerate(WINDOW_NAMES):
        count = sum(1 for s in streams if s.sequence == sequence)
        if count:
            fields[f"{prefix}/Statistics/Window Sequences/{name}"] = share(count, total, "channel stream")
    for shape, name in enumerate(["Sine", "KBD"]):
        count = sum(1 for s in streams if s.shape == shape)
        if count:
            fields[f"{prefix}/Statistics/Window Shapes/{name}"] = share(count, total, "channel stream")
    books = [b for s in streams for group in s.codebooks() for b in group]
    for book in sorted(set(books)):
        fields[f"{prefix}/Statistics/Codebooks/Codebook {book}"] = share(books.count(book), len(books), "band")
    return fields


def adts_frames(blocks, profile=1, rate=RATE, channels=1, protect=False, damage=()):
    """ADTS frames of one raw data block each, with the CRC over the model's element regions; damage lists frames whose CRC is wrong."""
    from make_mp4_fixtures import adts_header, bit_fields, crc16

    frames = []
    for number, (block, spans) in enumerate(blocks):
        length = 7 + (2 if protect else 0) + len(block)
        header = adts_header(length, rate, channels, profile, protect=protect)
        if protect:
            crc = crc16(bit_fields(header, 0, 56) + M.crc_regions(block, spans)) ^ (0x0101 if number in damage else 0)
            frames.append(header + crc.to_bytes(2, "big") + block)
        else:
            frames.append(header + block)
    return frames


def concat(frames):
    data = b""
    rows = []
    for frame in frames:
        rows.append([len(data), len(frame)])
        data += frame
    return data, rows


def stream(codec, rate, channels, mode, samples, vbr=True, encoder=None, **extra):
    from make_mp4_fixtures import SPEAKERS
    entry = {"codec": codec, "sample_rate": rate, "channels": channels, "channel_mode": mode, "channel_layout": SPEAKERS[channels], "samples": samples,
             "is_vbr": vbr}
    if encoder:
        entry["encoder"] = list(encoder)
    entry.update(extra)
    return entry


def manifest(description, data, rows, streams, fields, findings=(), skip=None, rate=RATE, fmt="AAC", boxes=None, trace_skip=None):
    skip = dict(skip or {})
    check = {"ffprobe": "ffprobe" not in skip, "decode": "decode" not in skip}
    if "ffprobe_streams" in skip:
        check["ffprobe_streams"] = False
    if trace_skip:
        check["faad_trace_skip"] = {str(k): v for k, v in trace_skip.items()}
    if fmt == "AAC":
        check["faad"] = None if "faad" in skip else {"seconds": f"{len(rows) * 1024 / rate:.3f}", "rate": rate}
    if skip:
        check["skip"] = skip
    return {"description": description, "length": len(data), "format": fmt, "boxes": boxes or [], "streams": streams,
            "units": rows, "fields": fields, "findings": [list(f) for f in findings], "crosscheck": check}


# --- Fixtures ----------------------------------------------------------------------------------

def windows():
    tns_long = [[(20, 8, 1, 0, 1, [1, 2, 3, 4, 5, 6, 7, 8]), (12, 4, 0, 1, 1, [1, 2, 3, 0])]]
    tns_short = [[(3, 2, 0, 0, 1, [3, 1])], [], [(4, 1, 1, 0, 0, [2])], [], [], [(5, 3, 0, 1, 1, [1, 2, 3])], [], []]
    streams = [
        channel(M.ONLY_LONG, 1, EVERY_BOOK, tns=tns_long, pulse=(2, [(3, 5), (10, 2)]), seed=1),
        channel(M.LONG_START, 0, [[(11, 10), (3, 20), (0, 10)]], seed=2),
        channel(M.EIGHT_SHORT, 0, SHORT_SECTIONS, groups=(3, 2, 2, 1), tns=tns_short, seed=3),
        channel(M.LONG_STOP, 1, [[(11, 6), (9, 10), (5, 12)]], seed=4),
        channel(M.ONLY_LONG, 1, [[(11, 2)]], seed=5),
        channel(M.ONLY_LONG, 0, [], seed=6),
    ]
    blocks = [M.raw_data_block([M.Element(M.SCE, ics=ics)]) for ics in streams]
    data, rows = concat(adts_frames(blocks))
    f = "ADTS/Frame[{}]/Raw Data Block/SCE/Channel"
    fields = {
        f.format(0) + "/ICS Info/Window Sequence": "0 (only long)", f.format(0) + "/ICS Info/Window Shape": "1 (KBD)",
        f.format(0) + "/ICS Info/Max SFB": "49", f.format(0) + "/Section Data": "15 sections",
        f.format(0) + "/Section Data/Section[1]": "codebook 1 (signed quadruples up to 1), bands 3-4",
        f.format(0) + "/Noise Substitution Bands": "30-33", f.format(0) + "/TNS Data": "2 filters",
        f.format(0) + "/TNS Data/Window/Filter": "order 8, 20 bands, downward", f.format(0) + "/Pulse Data": "2 pulses",
        f.format(0) + "/Pulse Data/Pulse Start SFB": "2", f.format(0) + "/Pulse Data/Pulse[1]": "offset 10, amplitude 2",
        f.format(1) + "/ICS Info/Window Sequence": "1 (long start)", f.format(1) + "/ICS Info/Window Shape": "0 (sine)",
        f.format(2) + "/ICS Info/Window Sequence": "2 (eight short)", f.format(2) + "/ICS Info/Scale Factor Grouping": "0x6A (4 groups of 3, 2, 2, 1 windows)",
        f.format(2) + "/Section Data/Section[4]": "group 1: codebook 9 (unsigned pairs up to 12), bands 0-3",
        f.format(2) + "/Noise Substitution Bands": "group 1: 10-13", f.format(2) + "/TNS Data": "3 filters",
        f.format(3) + "/ICS Info/Window Sequence": "3 (long stop)", f.format(5) + "/ICS Info/Max SFB": "0",
        "ADTS/Statistics/TNS/Only Long": "1 of 3 (33.3%)", "ADTS/Statistics/TNS/Eight Short": "1 of 1 (100.0%)",
        "ADTS/Statistics/Pulse Data": share(1, 6, "channel stream"), "ADTS/Statistics/Max SFB (Short Windows)/14": share(1, 1, "channel stream"),
        **statistics("ADTS", streams),
    }
    return "windows.aac", data, manifest(
        "Mono frames of every window sequence (only long, long start, eight short with groups of 3, 2, 2 and 1 windows, long stop) and both window "
        "shapes; the first codes bands with every Huffman codebook from 1 to 11, escapes up to 8191, noise substitution, two TNS filters and two "
        "pulses; the short one TNS in three windows; the last codes no band.",
        data, rows, [stream("AAC LC", RATE, 1, "Mono", 6 * 1024)], fields)


def pair_block(mask, ms_used=None, common=True, first=None, second=None, rate=RATE, extra=()):
    a = first or channel(M.ONLY_LONG, 1, [[(11, 4), (5, 10), (1, 20), (0, 6)]], seed=7, rate=rate)
    b = second or channel(M.ONLY_LONG, 1, [[(9, 6), (3, 14), (1, 14), (0, 6)]], gain=90, seed=8, rate=rate)
    return M.raw_data_block([M.Element(M.CPE, ics=(a, b), common=common, mask=mask, ms_used=ms_used), *extra], rate_index=rate_index(rate)), (a, b)


def stereo():
    used = [[1 if b % 3 == 0 else 0 for b in range(40)]]
    short_used = [[1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0], [0] * 14, [1] * 14, [0, 1] * 7]
    made = [
        pair_block(0), pair_block(1, used), pair_block(2),
        pair_block(0, common=False, first=channel(M.ONLY_LONG, 1, [[(11, 30)]], seed=11),
                   second=channel(M.EIGHT_SHORT, 0, [[(11, 4), (2, 10)], [(3, 14)]], groups=(4, 4), seed=12)),
        pair_block(1, short_used, first=channel(M.EIGHT_SHORT, 0, SHORT_SECTIONS, groups=(3, 2, 2, 1), seed=9),
                   second=channel(M.EIGHT_SHORT, 0, SHORT_SECTIONS, groups=(3, 2, 2, 1), seed=10)),
        pair_block(1, used),
    ]
    data, rows = concat(adts_frames([m[0] for m in made], channels=2))
    f = "ADTS/Frame[{}]/Raw Data Block/CPE"
    streams = [s for m in made for s in m[1]]
    fields = {
        f.format(0) + "/Mid/Side Mask Present": "0 (no mid/side)", f.format(1) + "/Mid/Side Mask Present": "1 (per band)",
        f.format(1) + "/Mid/Side Bands": band_text([[u == 1 for u in used[0]]]), f.format(2) + "/Mid/Side Mask Present": "2 (all bands)",
        f.format(3) + "/HasCommonWindow": "false", f.format(3) + "/Channel 0/ICS Info/Window Sequence": "0 (only long)",
        f.format(3) + "/Channel 1/ICS Info/Window Sequence": "2 (eight short)",
        f.format(3) + "/Channel 1/ICS Info/Scale Factor Grouping": "0x77 (2 groups of 4, 4 windows)",
        f.format(4) + "/Mid/Side Bands": band_text([[u == 1 for u in g] for g in short_used]),
        "ADTS/Statistics/Channel Pairs/Common Window": share(5, 6, "element"),
        "ADTS/Statistics/Channel Pairs/Mid/Side Masks/Per Band (1)": share(3, 5, "element"),
        "ADTS/Statistics/Channel Pairs/Mid/Side Masks/All Bands (2)": share(1, 5, "element"),
        **statistics("ADTS", streams),
    }
    return "stereo.aac", data, manifest(
        "Channel pairs: a common window with mid/side masks 0, 1 (every third band) and 2; separate windows, one long and one of two short groups; "
        "a common short window with a mask per group.", data, rows, [stream("AAC LC", RATE, 2, "Stereo", 6 * 1024)], fields)


def intensity_noise():
    first = channel(M.ONLY_LONG, 1, [[(11, 6), (5, 14), (13, 10), (3, 10)]], seed=13)
    second = channel(M.ONLY_LONG, 1, [[(9, 6), (13, 14), (15, 6), (14, 4), (15, 10)]], gain=95, seed=14)
    flips = {20, 21, 26, 27, 30}
    used = [[1 if b in flips else 0 for b in range(40)]]
    made = [pair_block(m, used if m == 1 else None, first=first, second=second) for m in (1, 0, 1, 0)]
    data, rows = concat(adts_frames([m[0] for m in made], channels=2))
    book = {b: 15 for b in range(20, 26)} | {b: 14 for b in range(26, 30)} | {b: 15 for b in range(30, 40)}
    in_phase = [[b in book and (book[b] == 14) == (b in flips) for b in range(40)]]
    out_phase = [[b in book and (book[b] == 14) != (b in flips) for b in range(40)]]
    f = "ADTS/Frame[{}]/Raw Data Block/CPE"
    fields = {
        f.format(0) + "/Channel 1/Intensity Stereo Bands": f"{band_text(in_phase)} in phase; {band_text(out_phase)} out of phase",
        f.format(1) + "/Channel 1/Intensity Stereo Bands": "20-25, 30-39 in phase; 26-29 out of phase",
        f.format(0) + "/Channel 0/Noise Substitution Bands": "20-29", f.format(0) + "/Channel 1/Noise Substitution Bands": "6-19",
        "ADTS/Statistics/Channel Pairs/Intensity Stereo": share(4, 4, "element"),
        "ADTS/Statistics/Channel Pairs/Intensity Stereo Bands": "50.0 %",
        "ADTS/Statistics/Channel Pairs/Out of Phase": share(sum(out_phase[0]) * 2 + 4 * 2, 80, "band"),
        "ADTS/Statistics/Noise Substitution": share(8, 8, "channel stream"),
    }
    return "intensity-noise.aac", data, manifest(
        "Channel pairs whose second channel codes bands 20 to 39 with intensity stereo, codebook 15 in phase and 14 out of phase, where a mid/side "
        "mask on some of them inverts the sign, and whose channels code other bands with noise substitution.",
        data, rows, [stream("AAC LC", RATE, 2, "Stereo", 4 * 1024)], fields)


SBR_HEADER = {"amp_res": 1, "start": 5, "stop": 9, "xover": 0}


def sbr_units(count, layout, ps=False, core=22050, coupled=False, crc=False):
    """Blocks of a channel element at the core rate and an SBR fill element after it; every fourth carries the SBR header."""
    counts = M.sbr_bands(SBR_HEADER, 2 * core)
    out = []
    for i in range(count):
        header = SBR_HEADER if i % 4 == 0 else None
        payload = M.sbr_payload(counts, header, pair=layout != "mono", coupled=coupled, ps=ps, crc=crc)
        fill = M.Element(M.FIL, payload=payload)
        if layout == "mono":
            ics = channel(M.ONLY_LONG, 1, [[(11, 4), (3, 16)]], seed=i, rate=core)
            out.append(M.raw_data_block([M.Element(M.SCE, ics=ics), fill], rate_index=rate_index(core)))
        else:
            block, _ = pair_block(0, rate=core, extra=[fill], first=channel(M.ONLY_LONG, 1, [[(11, 4), (3, 16)]], seed=i, rate=core),
                                  second=channel(M.ONLY_LONG, 1, [[(9, 4), (1, 16)]], seed=i + 1, rate=core))
            out.append(block)
    return out


def sbr_fields(prefix, count, headers, ps):
    high, low, noise = M.sbr_bands(SBR_HEADER, 44100)
    fields = {
        f"{prefix}/Statistics/SBR": share(count, count, "access unit"),
        f"{prefix}/Statistics/SBR/Parametric Stereo": share(count if ps else 0, count, "access unit"),
        f"{prefix}/Statistics/SBR/SBR Headers": f"{headers} header{'' if headers == 1 else 's'}",
    }
    return fields


def implicit_sbr_adts():
    blocks = sbr_units(12, "stereo")
    data, rows = concat(adts_frames(blocks, rate=22050, channels=2))
    fields = {
        "ADTS/Frame/Raw Data Block": "CPE, FIL", "ADTS/Frame/Raw Data Block/FIL/Extension Payload/Extension Type": "13 (EXT_SBR_DATA)",
        "ADTS/Frame/Raw Data Block/FIL/Extension Payload/SBR Header/bs_start_freq": "5",
        "ADTS/Frame/Raw Data Block/FIL/Extension Payload/SBR Header/bs_stop_freq": "9",
        "ADTS/Frame[1]/Raw Data Block/FIL/Extension Payload/HasSbrHeader": "false",
        **sbr_fields("ADTS", 12, 3, False),
    }
    return "implicit-sbr.aac", data, manifest(
        "ADTS at a core rate of 22050 Hz whose channel pairs are each followed by a fill element of SBR data, the SBR header in every fourth: "
        "HE-AAC, signaled only implicitly, at 44100 Hz.", data, rows,
        [stream("HE-AAC", 44100, 2, "Stereo", 12 * 2048, ffprobe_profile="HE-AAC")], fields,
        [["aac.implicit-sbr", "ADTS cannot signal SBR or PS explicitly, but 12 of 12 raw data blocks hold SBR data: the stream is HE-AAC"]],
        skip={"faad": "faad -i prints twice the rate of any ADTS stream at 24 kHz or less, whatever its frames hold; the trace check reads the SBR data instead."},
        rate=22050)


# The SBR layout of FAAC 2.2's HE-AAC encoder at 44100 Hz (FaacSbr): bs_header_extra_1 sent and bs_header_extra_2 not, bs_noise_bands 0, a start
# frequency of 15, a crossover band of 0, a frequency scale of 1 with an alter scale of 0, 3 dB steps and a stop frequency of 12, the header
# in the first access unit and every 30 after it, and plain sbr_data.
FAAC_HEADER = {"amp_res": 1, "start": 15, "stop": 12, "xover": 0, "freq_scale": 1, "alter_scale": 0, "noise_bands": 0}
FAAC_UNITS = 32
FAAC_DEVIANT = 10
# name, what the access unit FAAC_DEVIANT does differently (the options of aac_model.sbr_data), and how the description says it.
FAAC_VARIANTS = [
    ("plain", {}, "nothing: every access unit is plain, FIXFIX or VARFIX frames coded across frequency"),
    ("data-extra", {"data_extra": True}, "sends extra data bits (bs_data_extra)"),
    ("coupling", {"coupled": True}, "couples its channels (bs_coupling)"),
    ("fixvar", {"grid": "FIXVAR"}, "uses a FIXVAR frame class"),
    ("varvar", {"grid": "VARVAR"}, "uses a VARVAR frame class"),
    ("time-envelope", {"time_env": True}, "codes its envelope across time (bs_df_env)"),
    ("time-noise", {"time_noise": True}, "codes its noise floor across time (bs_df_noise)"),
    ("harmonic", {"harmonic": True}, "sends sinusoids (bs_add_harmonic_flag)"),
    ("extended-data", {"extension": True}, "sends extended data (bs_extended_data)"),
]


def faac_style_adts(name, tools, what):
    """ADTS at a core rate of 22050 Hz of channel pairs each followed by SBR data in FAAC 2.2's layout, but for what one access unit does
    differently: the analyzer names FAAC only for the stream that does nothing differently."""
    counts = M.sbr_bands(FAAC_HEADER, 44100)
    blocks = []
    for i in range(FAAC_UNITS):
        header = FAAC_HEADER if i % 30 == 0 else None
        options = tools if i == FAAC_DEVIANT else {"grid": "VARFIX" if i % 7 == 3 else "FIXFIX"}
        fill = M.Element(M.FIL, payload=M.sbr_payload(counts, header, pair=True, **options))
        block, _ = pair_block(0, rate=22050, extra=[fill], first=channel(M.ONLY_LONG, 1, [[(11, 4), (3, 16)]], seed=i, rate=22050),
                              second=channel(M.ONLY_LONG, 1, [[(9, 4), (1, 16)]], seed=i + 1, rate=22050))
        blocks.append(block)
    data, rows = concat(adts_frames(blocks, rate=22050, channels=2))
    named = not tools
    prefix = "ADTS/Frame/Raw Data Block/FIL/Extension Payload/SBR Header"
    fields = {
        prefix + "/bs_start_freq": "15", prefix + "/bs_stop_freq": "12", prefix + "/bs_freq_scale": "1", prefix + "/bs_alter_scale": "0",
        prefix + "/bs_noise_bands": "0", prefix + "/HasHeaderExtra1": "true", prefix + "/HasHeaderExtra2": "false",
        **sbr_fields("ADTS", FAAC_UNITS, 2, False),
    }
    return f"faac-style-{name}.aac", data, manifest(
        f"ADTS at a core rate of 22050 Hz whose channel pairs are each followed by a fill element of SBR data in the layout FAAC 2.2 writes at "
        f"44100 Hz (the header in the first access unit and again in the 31st), where access unit "
        f"{FAAC_DEVIANT + 1} does {what}.",
        data, rows,
        [stream("HE-AAC", 44100, 2, "Stereo", FAAC_UNITS * 2048, encoder=("FAAC", "Medium") if named else None, ffprobe_profile="HE-AAC")], fields,
        [["aac.implicit-sbr", f"{FAAC_UNITS} of {FAAC_UNITS} raw data blocks hold SBR data: the stream is HE-AAC"]],
        skip={"faad": "faad -i prints twice the rate of any ADTS stream at 24 kHz or less, whatever its frames hold; the trace check reads the SBR data instead."},
        rate=22050)


def faac_style_fixtures():
    return [faac_style_adts(name, tools, what) for name, tools, what in FAAC_VARIANTS]


def mp4_fixture(name, description, config, rate, channels, units, codec, out_rate, out_channels, mode, fields, findings, faad, ffprobe_profile=None,
                ffprobe_channels=None):
    entry = mp4a(channels, rate, esds(0x40, config, 0, 0))
    track = Track(1, units, rate, channels, entry, chunk=6)
    data, records, offsets = movie([track])
    from make_mp4_fixtures import boxes_of, unit_list
    entry = stream(codec, out_rate, out_channels, mode, len(units) * (2048 if out_rate != rate else 1024))
    if ffprobe_profile:
        entry["ffprobe_profile"] = ffprobe_profile
    if ffprobe_channels:
        entry["ffprobe_channels"] = ffprobe_channels
    return name, data, {
        "description": description, "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)), "streams": [entry],
        "units": unit_list(offsets[0], units, 6), "fields": fields, "findings": [list(f) for f in findings],
        "crosscheck": {"ffprobe": True, "mp4box": True, "decode": True, "faad": faad},
    }


FAAD_PS_ABSENT = {"label": "HE AAC", "channels": 2, "rate": 44100}


def mp4_fixtures():
    stats = "MP4/mdat/Track 1 Samples/AAC/Statistics"
    out = []
    units = [b for b, _ in sbr_units(12, "stereo")]
    out.append(mp4_fixture(
        "implicit-sbr", "AAC LC at 22050 Hz in its configuration, whose access units carry SBR data in fill elements: HE-AAC, signaled only "
        "implicitly, at 44100 Hz.", asc(2, 22050, 2), 22050, 2, units, "HE-AAC", 44100, 2, "Stereo",
        {"MP4/moov/trak/mdia/minf/stbl/stsd/mp4a/esds/ES_Descriptor/DecoderConfigDescriptor/DecoderSpecificInfo/AudioSpecificConfig": "HE-AAC",
         **{k.replace("ADTS", "MP4/mdat/Track 1 Samples/AAC"): v for k, v in sbr_fields("ADTS", 12, 3, False).items()}},
        [["aac.implicit-sbr", "The AudioSpecificConfig signals no SBR, but 12 of 12 raw data blocks hold SBR data"]],
        {"label": "LC AAC", "channels": 2, "rate": 44100}, ffprobe_profile="HE-AAC"))
    units = [b for b, _ in sbr_units(12, "mono", ps=True)]
    out.append(mp4_fixture(
        "implicit-ps", "Mono AAC LC at 22050 Hz in its configuration, whose access units carry SBR data with a parametric stereo extension: "
        "HE-AAC v2, signaled only implicitly.", asc(2, 22050, 1), 22050, 1, units, "HE-AAC v2", 44100, 2, "Parametric stereo",
        {stats + "/SBR/Parametric Stereo": share(12, 12, "access unit"),
         "MP4/mdat/Track 1 Samples/AAC/Access Unit/FIL/Extension Payload/SBR Data/Parametric Stereo Extension": "2 (EXT_ID_PS)"},
        [["aac.implicit-sbr", "and 12 carry the parametric stereo extension: the stream is HE-AAC v2"]],
        {"label": "LC AAC", "channels": 2, "rate": 44100}, ffprobe_profile="HE-AACv2"))
    units = [b for b, _ in sbr_units(12, "stereo")]
    out.append(mp4_fixture(
        "sbr-downsampled", "HE-AAC signaled hierarchically with an extension rate equal to the core's 22050 Hz (downsampled SBR): decoders derive "
        "SBR's frequency tables at twice the core rate all the same, and output 22050 Hz.", asc(2, 22050, 2, sbr=True, signaling="hierarchical",
                                                                                                  extension_rate=22050), 22050, 2,
        units, "HE-AAC", 22050, 2, "Stereo", {k.replace("ADTS", "MP4/mdat/Track 1 Samples/AAC"): v for k, v in sbr_fields("ADTS", 12, 3, False).items()}, [],
        {"label": "LC AAC", "channels": 2, "rate": 22050}, ffprobe_profile="HE-AAC"))
    units = [b for b, _ in sbr_units(12, "mono", ps=True)]
    name, data, man = mp4_fixture(
        "ps-signaled-absent", "HE-AAC over a mono core whose sync extensions signal SBR present and PS absent, over access units that carry the "
        "parametric stereo extension anyway: mono, with a warning.", asc(2, 22050, 1, sbr=True, ps=False, signaling="backward", extension_rate=44100),
        22050, 1, units, "HE-AAC", 44100, 1, "Mono", {stats + "/SBR/Parametric Stereo": share(12, 12, "access unit")},
        [["aac.ps-signaled-absent", "signals PS absent, but 12 of 12 raw data blocks carry the parametric stereo extension"]], FAAD_PS_ABSENT)
    man["crosscheck"]["decode"] = False
    man["crosscheck"]["skip"] = {"decode": "ffmpeg reports that PS is signaled absent but found, the condition the fixture tests."}
    out.append((name, data, man))
    units = [b for b, _ in sbr_units(12, "stereo", ps=True)]
    name, data, man = (mp4_fixture(
        "ps-in-channel-pair", "AAC LC at 22050 Hz in its configuration whose channel pairs carry SBR data with the parametric stereo extension's "
        "ID, which only a single channel element can carry: HE-AAC with two channels, and a warning.", asc(2, 22050, 2), 22050, 2, units,
        "HE-AAC", 44100, 2, "Stereo", {stats + "/SBR/Parametric Stereo": share(0, 12, "access unit")},
        [["aac.implicit-sbr", "12 of 12 raw data blocks hold SBR data: the stream is HE-AAC,"],
         ["aac.ps-in-channel-pair", "12 raw data blocks hold the parametric stereo extension's ID in a channel pair's SBR data"]],
        {"label": "LC AAC", "channels": 2, "rate": 44100}, ffprobe_profile="HE-AAC"))
    man["crosscheck"]["decode"] = False
    man["crosscheck"]["skip"] = {"decode": "ffmpeg reports PS found in a stream whose channel pair rules it out, the condition the fixture tests."}
    out.append((name, data, man))
    units = [b for b, _ in sbr_units(12, "mono", ps=True)]
    out.append(mp4_fixture(
        "explicit-sbr-implicit-ps", "HE-AAC signaled hierarchically over a mono core, whose access units also carry the parametric stereo "
        "extension: HE-AAC v2 with PS signaled only implicitly.", asc(2, 22050, 1, sbr=True, signaling="hierarchical", extension_rate=44100), 22050, 1,
        units, "HE-AAC v2", 44100, 2, "Parametric stereo", {stats + "/SBR/Parametric Stereo": share(12, 12, "access unit")},
        [["aac.implicit-ps", "12 of 12 raw data blocks carry the parametric stereo extension"]],
        {"label": "LC AAC", "channels": 2, "rate": 44100}, ffprobe_profile="HE-AACv2"))
    units = [b for b, _ in sbr_units(12, "stereo")]
    name, data, man = mp4_fixture(
        "sbr-signaled-absent", "A configuration whose sync extension signals SBR absent, over access units that carry SBR data anyway.",
        asc(2, 22050, 2, sbr=False, signaling="backward"), 22050, 2, units, "AAC LC", 22050, 2, "Stereo",
        {stats + "/SBR": share(12, 12, "access unit")},
        [["aac.sbr-signaled-absent", "signals SBR absent, but 12 of 12 raw data blocks hold SBR data"]],
        {"label": "LC AAC", "channels": 2, "rate": 22050})
    man["crosscheck"]["decode"] = False
    man["crosscheck"]["skip"] = {"decode": "ffmpeg warns that SBR is signaled absent but found, the condition the fixture tests."}
    out.append((name, data, man))
    # A block followed by bytes that belong to no element.
    (block, _), _ = pair_block(0)
    units = [block + (b"\x00\x11" if i == 3 else b"") for i in range(8)]
    name, data, man = mp4_fixture(
        "trailing-bytes", "Access units of one channel pair each, the fourth with two bytes after its END element.", asc(2, 44100, 2), 44100, 2, units,
        "AAC LC", 44100, 2, "Stereo", {"MP4/mdat/Track 1 Samples/AAC/Access Unit[3]/Trailing Bytes": "2 bytes"},
        [["aac.unit-trailing-bytes", "holds 2 bytes after the END element"]], {"label": "LC AAC", "channels": 2, "rate": 44100})
    man["crosscheck"]["decode"] = False
    man["crosscheck"]["skip"] = {"faad_trace": "faad2 reads the access unit's raw data block and does not show the bytes after it.",
                                 "decode": "ffmpeg decodes the bytes after the END element as a further raw data block, which fails."}
    out.append((name, data, man))
    return out


def fill_and_data():
    """Fill elements with dynamic range control, fill data, ancillary data and text, and data stream elements."""
    ics = channel(M.ONLY_LONG, 1, [[(11, 4), (3, 16)]], seed=20)
    # Text is shown where it lies on the block's byte boundaries, as encoders write it: this channel ends at bit 681, so the text of
    # a fill element after it starts on one.
    aligned = channel(M.ONLY_LONG, 1, [[(11, 4), (3, 16)]], seed=46)
    drc = M.drc_payload(bands=((0x10, 1, 12), (0x40, 0, 4), (0xC0, 1, 40)), reference=80, pce_tag=0, excluded=[0b0000001])
    blocks = [
        M.raw_data_block([M.Element(M.SCE, ics=ics), M.Element(M.FIL, payload=drc)]),
        M.raw_data_block([M.Element(M.SCE, ics=ics), M.Element(M.FIL, payload=M.fill_data_payload(20))]),
        M.raw_data_block([M.Element(M.SCE, ics=aligned), M.Element(M.FIL, payload=M.other_payload(b"hand-built AAC"))]),
        M.raw_data_block([M.Element(M.DSE, tag=1, aligned=True, data=b"data stream text"), M.Element(M.SCE, ics=ics)]),
        M.raw_data_block([M.Element(M.SCE, ics=ics), M.Element(M.DSE, tag=2, aligned=False, data=bytes(range(256)) + bytes(44))]),
        M.raw_data_block([M.Element(M.SCE, ics=ics), M.Element(M.FIL, payload=M.drc_payload() + M.fill_data_payload(5))]),
        M.raw_data_block([M.Element(M.SCE, ics=aligned), M.Element(M.FIL, payload=M.data_element_payload(b"ancillary text"))]),
    ]
    data, rows = concat(adts_frames(blocks))
    p = "ADTS/Frame[{}]/Raw Data Block/{}"
    fields = {
        p.format(0, "FIL/Extension Payload/Extension Type"): "11 (EXT_DYNAMIC_RANGE)",
        p.format(0, "FIL/Extension Payload/PCE Instance Tag"): "0",
        p.format(0, "FIL/Extension Payload/DRC Band Increment"): "2",
        p.format(0, "FIL/Extension Payload/DRC Band Top[2]"): "192",
        p.format(0, "FIL/Extension Payload/Program Reference Level"): "80 (-20.00 dB)",
        p.format(0, "FIL/Extension Payload/Dynamic Range"): "-3.00 dB",
        p.format(0, "FIL/Extension Payload/Dynamic Range[1]"): "+1.00 dB",
        p.format(1, "FIL/Extension Payload/Fill Bytes"): "19 bytes",
        p.format(2, "FIL/Extension Payload/Text"): "hand-built AAC",
        p.format(6, "FIL/Extension Payload/Data Element Version"): "0 (ANC_DATA)",
        p.format(6, "FIL/Extension Payload/Text"): "ancillary text",
        p.format(3, "DSE/Text"): "data stream text",
        p.format(4, "DSE/Escape Count"): "45",
        p.format(5, "FIL"): "fill element, EXT_DYNAMIC_RANGE, EXT_FILL_DATA, 8 bytes",
        "ADTS/Statistics/Fill Elements/EXT_DYNAMIC_RANGE": "2 payloads, 14 bytes",
        "ADTS/Statistics/Data Stream Elements": "2 elements, 316 bytes",
    }
    return "fill-and-data.aac", data, manifest(
        "Fill elements with dynamic range control (three bands, a reference level, a program config element's tag and an excluded channel), fill "
        "data, other bits holding text, ancillary data with text, and two payloads in one fill element; data stream elements, one byte-aligned "
        "with text, one of 300 bytes with an escape count.", data, rows, [stream("AAC LC", RATE, 1, "Mono", 7 * 1024)], fields,
        trace_skip={6: "faad2 2.11 returns from an ancillary data element after its first byte (extension_payload in syntax.c) and reads the "
                       "rest of the fill element as further elements."})


def crc_fixtures():
    out = []
    made = [pair_block(m) for m in (0, 0, 2, 0, 0, 2)]
    for name, damage, findings, description in [
        ("crc-stereo.aac", (), [], "Protected ADTS frames of one channel pair each: the CRC covers the pair's first 192 bits and its second channel's "
         "first 128, and every frame's verifies."),
        ("crc-stereo-damaged.aac", (3,), [["aac.crc-mismatch", "1 ADTS frame fails the CRC check"]],
         "Protected channel pair frames, the fourth with a wrong CRC, which fails."),
    ]:
        data, rows = concat(adts_frames([m[0] for m in made], channels=2, protect=True, damage=damage))
        fields = {"ADTS/Frame[3]/CRC/Check": "mismatch" if damage else "verified", "ADTS/Frame/CRC/Check": "verified"}
        out.append((name, data, manifest(description, data, rows, [stream("AAC LC", RATE, 2, "Stereo", 6 * 1024)], fields, findings,
                                         skip={"ffprobe": "ffmpeg does not check ADTS CRCs, and decodes every frame"} if damage else None)))
    ics = channel(M.ONLY_LONG, 1, [[(1, 1)]], seed=21)
    blocks = [M.raw_data_block([M.Element(M.SCE, ics=ics), M.Element(M.FIL, payload=M.fill_data_payload(10))]) for _ in range(6)]
    data, rows = concat(adts_frames(blocks, protect=True))
    out.append(("crc-short-element.aac", data, manifest(
        "Protected mono frames whose single channel element is shorter than 192 bits and followed by fill data: the CRC covers the element "
        "filled up with zero bits, not the fill element, and every frame's verifies.", data, rows,
        [stream("AAC LC", RATE, 1, "Mono", 6 * 1024)], {"ADTS/Frame[2]/CRC/Check": "verified"})))
    return out


def object_types():
    out = []
    # AAC Main (ADTS profile 0): prediction with a reset group and bands that use it.
    streams = [channel(M.ONLY_LONG, 1, [[(11, 4), (5, 26)]], seed=22, predictor=(i % 2 == 0, 5 + i, [1, 0, 1] * 10)) for i in range(4)]
    blocks = [M.raw_data_block([M.Element(M.SCE, ics=s)], object_type=1) for s in streams]
    data, rows = concat(adts_frames(blocks, profile=0))
    c = "ADTS/Frame{}/Raw Data Block/SCE/Channel/ICS Info"
    out.append(("main-prediction.aac", data, manifest(
        "AAC Main (ADTS profile 0): long windows with predictor data, a predictor reset in every other frame and prediction in every third band.",
        data, rows, [stream("AAC Main", RATE, 1, "Mono", 4 * 1024)],
        {c.format("") + "/HasPredictorData": "true", c.format("") + "/Predictor Reset Group": "5", c.format("") + "/Prediction Used": "0, 2-3, 5-6, 8-9, 11-12, 14-15, 17-18, 20-21, 23-24, 26-27, 29",
         c.format("[1]") + "/HasPredictorReset": "false", "ADTS/Statistics/Prediction": share(4, 4, "channel stream"),
         "ADTS/Statistics/Prediction/Predictor Resets": "2 channel streams"})))
    # AAC LTP (ADTS profile 3 in MPEG-4): ltp_data for both channels of a pair with a common window.
    made = []
    for i in range(4):
        a = channel(M.ONLY_LONG, 1, [[(11, 4), (5, 26)]], seed=23 + i, predictor=((300 + i, 3, [1] * 30), (700, 5, [0, 1] * 15) if i % 2 else None))
        b = channel(M.ONLY_LONG, 1, [[(9, 4), (3, 26)]], seed=24 + i)
        made.append(M.raw_data_block([M.Element(M.CPE, ics=(a, b), mask=0)], object_type=4))
    data, rows = concat(adts_frames(made, profile=3, channels=2))
    i = "ADTS/Frame{}/Raw Data Block/CPE/ICS Info"
    out.append(("ltp.aac", data, manifest(
        "AAC LTP (MPEG-4 ADTS profile 3): channel pairs with a common window whose ics_info carries long-term prediction data for the first "
        "channel, and in every other frame for the second.", data, rows, [stream("AAC LTP", RATE, 2, "Stereo", 4 * 1024)],
        {i.format("") + "/LTP Data/LTP Lag": "300 samples", i.format("") + "/HasSecondLtpData": "false",
         i.format("[1]") + "/Second LTP Data/LTP Lag": "700 samples", "ADTS/Statistics/Long-Term Prediction": share(6, 8, "channel stream")},
        skip={"faad": "faad2 reads ADTS profile 3 but its -i output names no LTP; the trace check covers the frames."})))
    # AAC SSR (ADTS profile 2): gain control data in each window sequence.
    seqs = [M.ONLY_LONG, M.LONG_START, M.EIGHT_SHORT, M.LONG_STOP]
    streams = []
    for n, seq in enumerate(seqs):
        windows = {M.ONLY_LONG: 1, M.LONG_START: 2, M.EIGHT_SHORT: 8, M.LONG_STOP: 2}[seq]
        adjustments = [[(3, 1)] if w % 2 == 0 else [] for w in range(2 * windows)]
        streams.append(channel(seq, 0, [[(1, 4)]], groups=(8,), seed=30 + n, gain_control=(2, adjustments)))
    blocks = [M.raw_data_block([M.Element(M.SCE, ics=s)], object_type=3) for s in streams]
    data, rows = concat(adts_frames(blocks, profile=2))
    out.append(("ssr-gain-control.aac", data, manifest(
        "AAC SSR (ADTS profile 2): gain control data for two bands in a frame of each window sequence, whose location fields differ in width.",
        data, rows, [stream("AAC SSR", RATE, 1, "Mono", 4 * 1024)],
        {"ADTS/Frame/Raw Data Block/SCE/Channel/Gain Control Data": "1 adjustment", "ADTS/Frame[2]/Raw Data Block/SCE/Channel/Gain Control Data": "8 adjustments",
         "ADTS/Statistics/Gain Control": share(4, 4, "channel stream")},
        skip={"decode": "ffmpeg skips SSR's gain control and reports it as a missing feature.", "faad": "faad2 is built without SSR.",
              "faad_trace": "faad2 is built without SSR and stops at gain control data."})))
    # A coupling channel element in a stream of channel configuration 2.
    blocks = []
    for n in range(4):
        cpe = M.Element(M.CPE, ics=(channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=n), channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=n + 1)), mask=0)
        cce = M.Element(M.CCE, tag=0, independent=n % 2, targets=[(1, 0, 1, 1)], ics=channel(M.ONLY_LONG, 1, [[(11, 4), (3, 4)]], seed=n + 2))
        blocks.append(M.raw_data_block([cpe, cce]))
    data, rows = concat(adts_frames(blocks, channels=2))
    out.append(("coupling.aac", data, manifest(
        "A channel pair and a coupling channel element coupled to both its channels, with a gain element list for the second, in a stream of "
        "channel configuration 2, which provides for no coupling channel element.", data, rows, [stream("AAC LC", RATE, 2, "Stereo", 4 * 1024)],
        {"ADTS/Frame/Raw Data Block/CCE/Target": "CPE 0, both channels", "ADTS/Frame/Raw Data Block/CCE/Gain Element Lists": "1",
         "ADTS/Frame[1]/Raw Data Block/CCE/IsIndependentlySwitched": "true"},
        [["aac.coupling-without-program", "4 raw data blocks hold a coupling channel element, which channel configuration 2 does not provide for"]],
        skip={"faad": "faad2 is built without coupling and stops at the element.", "faad_trace": "faad2 is built without coupling and stops at the element.",
              "decode": "ffmpeg's decoder finds no channel for the coupling element in a stream of channel configuration 2.",
              "ffprobe_streams": "ffmpeg cannot decode the frames, so it reports no sample rate."})))
    return out


def corrupt():
    """Blocks that break the syntax, each between valid frames so ADTS stays in sync."""
    good = M.raw_data_block([M.Element(M.SCE, ics=channel(M.ONLY_LONG, 1, [[(11, 4), (3, 16)]], seed=40))])

    def section_bits(entries):
        return [(v, w) for entry in entries for v, w in entry]

    bad = {}
    # Codebook 12, which is reserved.
    ics = channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=41)
    ics.raw_sections = section_bits([[(12, 4), (4, 5)]])
    ics.max_sfb = 4
    ics.sections = [[(0, 4)]]
    bad["codebook"] = (M.raw_data_block([M.Element(M.SCE, ics=ics)]), "aac.codebook-reserved", "names codebook 12, which the specification reserves")
    # A section that runs past max_sfb.
    ics = channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=42)
    ics.raw_sections = section_bits([[(11, 4), (6, 5)]])
    ics.sections = [[(0, 4)]]
    bad["section"] = (M.raw_data_block([M.Element(M.SCE, ics=ics)]), "aac.section-overrun", "has a section that ends at band 6, past max_sfb 4")
    # max_sfb beyond the 49 bands of a long window at 44100 Hz.
    w = Bits()
    w.put(0, 3).put(0, 4).put(100, 8).put(0, 1).put(0, 2).put(1, 1).put(60, 6).put(0, 1).put(7, 3)
    bad["max_sfb"] = ((w.align().bytes() + bytes(8), []), "aac.max-sfb-too-large", "gives max_sfb 60, more than the 49 scalefactor bands")
    # Spectral data cut short: the block ends inside it.
    block, spans = M.raw_data_block([M.Element(M.SCE, ics=channel(M.ONLY_LONG, 1, [[(11, 20)]], seed=43))])
    bad["overrun"] = ((block[:len(block) // 2], spans), "aac.element-past-end", "runs past the end of its access unit")
    # An escape sequence of nine leading ones, for a magnitude of 8192.
    ics = channel(M.ONLY_LONG, 1, [[(11, 2)]], seed=44)
    ics.values[(0, 0)] = [8192, 1, 0, 0]
    bad["escape"] = (M.raw_data_block([M.Element(M.SCE, ics=ics)]), "aac.escape-too-long", "holds an escape sequence of more than 8 leading ones")
    # Predictor data in AAC LC, which defines none.
    ics = channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=45, predictor=((300, 3, [1] * 4),))
    bad["prediction"] = (M.raw_data_block([M.Element(M.SCE, ics=ics)]), "aac.prediction-not-allowed", "sets predictor_data_present, which AAC LC does not define")
    corrupt_blocks = []
    findings = []
    for key in ["codebook", "section", "max_sfb", "overrun", "escape", "prediction"]:
        (blk, _), code, message = bad[key]
        corrupt_blocks.append(blk)
        findings.append([code, message])
    frames = []
    for blk in corrupt_blocks:
        frames += [b for b in adts_frames([good, good])]
        frames += adts_frames([(blk, [])])
    frames += adts_frames([good, good, good])
    data, rows = concat(frames)
    out = [("corrupt-syntax.aac", data, manifest(
        "Valid mono frames with six broken ones between them: codebook 12, a section past max_sfb, max_sfb 60 at 44100 Hz, a block that ends "
        "inside its spectral data, an escape sequence of nine leading ones, and predictor data in AAC LC. Each block's reading stops there.", data, rows, [stream("AAC LC", RATE, 1, "Mono", len(rows) * 1024)],
        {"ADTS/Frame[2]/Raw Data Block/SCE/Channel/Section Data/Section": "codebook 12 (reserved), bands 0-3",
         "ADTS/Statistics/Raw Data Blocks/Unreadable Blocks": share(6, len(rows), "block")},
        findings,
        skip={"ffprobe": "ffmpeg drops the broken frames.", "faad": "faad2 stops at the first broken frame.", "decode": "a damaged file, which decoders reject",
              "faad_trace": "faad2 stops at the first broken frame."}))]

    # Breaks the reading survives: an ics_reserved_bit, ms_mask_present 3, pulse data in short windows, a scalefactor out of range, a TNS
    # filter of too high an order, fill data with other bytes than 0xA5, and gain control data in AAC LC.
    soft = []
    ics = channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=50, reserved=1)
    soft.append((M.raw_data_block([M.Element(M.SCE, ics=ics)]), "aac.ics-reserved-bit", "an ics_reserved_bit of 1"))
    a = channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=51)
    b = channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=52)
    soft.append((M.raw_data_block([M.Element(M.CPE, ics=(a, b), mask=3)]), "aac.ms-mask-reserved", "ms_mask_present 3"))
    ics = channel(M.EIGHT_SHORT, 0, [[(1, 4)]], groups=(8,), seed=53, pulse=(0, [(1, 1)]))
    soft.append((M.raw_data_block([M.Element(M.SCE, ics=ics)]), "aac.pulse-in-short-window", "pulse data in eight short windows"))
    ics = channel(M.ONLY_LONG, 1, [[(11, 4)]], gain=250, seed=54)
    ics.gains[(0, 1)] = 270
    ics.gains[(0, 2)] = 270
    ics.gains[(0, 3)] = 250
    soft.append((M.raw_data_block([M.Element(M.SCE, ics=ics)]), "aac.scalefactor-out-of-range", "a scalefactor outside 0 to 255"))
    ics = channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=55, tns=[[(10, 15, 0, 0, 0, [1] * 15)]])
    soft.append((M.raw_data_block([M.Element(M.SCE, ics=ics)]), "aac.tns-order-too-high", "a TNS filter of a higher order than the 12 its window allows"))
    ics = channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=56)
    soft.append((M.raw_data_block([M.Element(M.SCE, ics=ics), M.Element(M.FIL, payload=M.fill_data_payload(6, byte=0x55))]),
                 "aac.fill-data-invalid", "fill data other than the fill nibble 0000 and fill bytes 10100101"))
    ics = channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=57, gain_control=(1, [[(3, 1)]]))
    soft.append((M.raw_data_block([M.Element(M.SCE, ics=ics)]), "aac.gain-control-not-allowed", "gain control data, which only AAC SSR defines"))
    frames = []
    for blk, _, _ in soft:
        frames += adts_frames([good, blk, good])
    data, rows = concat(frames)
    out.append(("corrupt-soft.aac", data, manifest(
        "Valid mono and stereo frames, among them some that break the specification without stopping the reading: an ics_reserved_bit of 1, "
        "ms_mask_present 3, pulse data in short windows, a scalefactor of 270, a TNS filter of order 15, fill bytes of 0x55 and gain control data "
        "outside AAC SSR.",
        data, rows, [stream("AAC LC", RATE, 1, "Mono", len(rows) * 1024)],
        {"ADTS/Frame[1]/Raw Data Block/SCE/Channel/ICS Info/ICS Reserved Bit": "1"},
        [[code, text] for _, code, text in soft] + [["aac.elements-mismatch", "other than channel configuration 1 defines"]],
        skip={"ffprobe": "ffmpeg rejects some of the frames.", "faad": "faad2 rejects some of the frames.", "decode": "decoders reject some of the frames",
              "faad_trace": "faad2 stops at frames it rejects."})))
    return out


def fixtures():
    """(directory, name, data, manifest) of every fixture: "adts" or "mp4"."""
    out = [("adts",) + f for f in [windows(), stereo(), intensity_noise(), implicit_sbr_adts(), fill_and_data()] + faac_style_fixtures() + crc_fixtures() + object_types() + corrupt()]
    out += [("mp4",) + f for f in mp4_fixtures()]
    return out
