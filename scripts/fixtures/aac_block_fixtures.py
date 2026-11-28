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
            fields[f"{prefix}/Statistics/Block Usage/{name}"] = share(count, total, "channel stream")
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


def stream(codec, rate, channels, mode, samples, vbr=True, encoder=None, layout=None, **extra):
    from make_mp4_fixtures import SPEAKERS
    entry = {"codec": codec, "sample_rate": rate, "channels": channels, "channel_mode": mode, "channel_layout": layout or SPEAKERS[channels], "samples": samples,
             "is_vbr": vbr}
    if encoder:
        entry["encoder"] = list(encoder)
    entry.update(extra)
    return entry


def manifest(description, data, rows, streams, fields, findings=(), skip=None, rate=RATE, fmt="AAC", boxes=None, trace_skip=None, golden_hides=None):
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
            "units": rows, "fields": fields, "findings": [list(f) for f in findings], "crosscheck": check,
            **({"golden_hides": golden_hides} if golden_hides else {})}


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
        **statistics("ADTS", streams), **value_fields("ADTS", [[s] for s in streams]),
    }
    return "windows.aac", data, manifest(
        "Mono frames of every window sequence (only long, long start, eight short with groups of 3, 2, 2 and 1 windows, long stop) and both window "
        "shapes; the first codes bands with every Huffman codebook from 1 to 11, escapes up to 8191, noise substitution, two TNS filters and two "
        "pulses; the short one TNS in three windows; the last codes no band.",
        data, rows, [stream("AAC LC", RATE, 1, "Mono", 6 * 1024)], fields)


def window_sequences_invalid():
    """Mono frames whose window sequences follow one another as the slopes of the four windows allow in some places and do not in others."""
    long_ = lambda seed: channel(M.ONLY_LONG, 1, [[(11, 10), (5, 12), (0, 8)]], seed=seed)
    start = lambda seed: channel(M.LONG_START, 0, [[(11, 10), (3, 12), (0, 8)]], seed=seed)
    short = lambda seed: channel(M.EIGHT_SHORT, 0, SHORT_SECTIONS, groups=(3, 2, 2, 1), seed=seed)
    stop = lambda seed: channel(M.LONG_STOP, 1, [[(11, 6), (9, 10), (5, 12)]], seed=seed)
    # (channel stream, whether the window sequence may follow the one before it)
    sequence = [
        (long_(81), True), (short(82), False), (long_(83), False), (start(84), True), (long_(85), False), (start(86), True), (short(87), True), (stop(88), True),
        (stop(89), False), (long_(90), True), (stop(91), False), (long_(92), True),
    ]
    streams = [ics for ics, _ in sequence]
    data, rows = concat(adts_frames([M.raw_data_block([M.Element(M.SCE, ics=ics)]) for ics in streams]))
    invalid = sum(1 for _, ok in sequence if not ok)
    f = "ADTS/Frame[{}]/Raw Data Block/SCE/Channel/ICS Info/Window Sequence"
    fields = {f.format(i): f"{ics.sequence} ({WINDOW_NAMES[ics.sequence].lower()})" for i, (ics, _) in enumerate(sequence)}
    fields.update(statistics("ADTS", streams))
    fields.update(value_fields("ADTS", [[s] for s in streams]))
    return "window-sequences-invalid.aac", data, manifest(
        "Twelve mono frames, one window sequence each, in which the slopes of five windows do not match those of the window before: eight short after only long, "
        "only long after eight short, only long after long start, long stop after long stop and long stop after only long; the others follow as the slopes match "
        "(long start after only long, eight short after long start, long stop after eight short, only long after long stop).",
        data, rows, [stream("AAC LC", RATE, 1, "Mono", len(sequence) * 1024)], fields,
        [["aac.window-slopes-differ", f"{invalid} raw data blocks hold a window sequence whose left slope differs"]],
        skip={"decode": "FFmpeg's decoder windows a transition the window shapes do not allow differently from the analyzer's (docs/formats/aac.md, Decoding)."})


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
        "a common short window with a mask per group.", data, rows, [stream("AAC LC", RATE, 2, "Stereo", 6 * 1024)], fields,
        [["aac.window-slopes-differ", "a window sequence whose left slope differs"]])


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

def loud_tools():
    """Two files at a level that an agreement within 16-bit steps means something (the other hand-built files peak near -100 dBFS): a mono one with pulse data and TNS in every
    window sequence, and a stereo one whose channels code the same bands with noise substitution, flagged mid/side, in grouped short windows and in long windows."""
    loud = 150
    tns_long = [[(20, 8, 1, 0, 1, [1, 2, 3, 4, 5, 6, 7, 8]), (12, 4, 0, 1, 1, [1, 2, 3, 0])]]
    tns_short = [[(3, 2, 0, 0, 1, [3, 1])], [], [(4, 1, 1, 0, 0, [2])], [], [], [(5, 3, 0, 1, 1, [1, 2, 3])], [], []]
    sections = [[(9, 12), (5, 10), (7, 10), (3, 8), (0, 4)]]
    short = [[(9, 4), (5, 3), (7, 3)], [(9, 4), (5, 3), (7, 3)], [(9, 4), (5, 3), (7, 3)], [(9, 4), (5, 3), (7, 3)]]
    mono = [
        channel(M.ONLY_LONG, 1, sections, gain=loud, tns=tns_long, pulse=(2, [(3, 15), (10, 7), (4, 1), (6, 9)]), seed=61),
        channel(M.LONG_START, 0, sections, gain=loud, pulse=(0, [(5, 12), (9, 3)]), seed=62),
        channel(M.EIGHT_SHORT, 0, short, gain=loud, groups=(3, 2, 2, 1), tns=tns_short, seed=63),
        channel(M.LONG_STOP, 1, sections, gain=loud, tns=tns_long, seed=64),
        channel(M.ONLY_LONG, 1, sections, gain=loud, pulse=(4, [(1, 15)]), seed=65),
    ]
    data, rows = concat(adts_frames([M.raw_data_block([M.Element(M.SCE, ics=ics)]) for ics in mono]))
    first = manifest("Mono frames at a level near -30 dBFS with pulse data (up to four pulses, amplitudes 1 to 15) and TNS filters in each window sequence.",
                     data, rows, [stream("AAC LC", RATE, 1, "Mono", 5 * 1024)], statistics("ADTS", mono))

    noisy = [[(9, 4), (13, 6), (5, 4), (13, 8), (0, 4)]]
    noisy_short = [[(9, 3), (13, 5), (7, 3), (13, 3)] for _ in range(4)]
    long_pair = (channel(M.ONLY_LONG, 1, noisy, gain=loud, seed=66), channel(M.ONLY_LONG, 1, noisy, gain=loud, seed=67))
    short_pair = (channel(M.EIGHT_SHORT, 0, noisy_short, gain=loud, groups=(3, 2, 2, 1), seed=68),
                  channel(M.EIGHT_SHORT, 0, noisy_short, gain=loud, groups=(3, 2, 2, 1), seed=69))
    long_used = [[1 if b in (4, 5, 6, 7, 8, 9, 14, 15, 16, 17, 18, 19, 20, 21) else 0 for b in range(26)]]
    short_used = [[0, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0], [0] * 14, [0, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 1, 1], [0] * 14]
    made = [pair_block(1, long_used, first=long_pair[0], second=long_pair[1]), pair_block(1, short_used, first=short_pair[0], second=short_pair[1]),
            pair_block(2, first=long_pair[0], second=long_pair[1]), pair_block(0, first=long_pair[0], second=long_pair[1])]
    pairs, rows2 = concat(adts_frames([m[0] for m in made], channels=2))
    streams = [t for m in made for t in m[1]]
    second = manifest("Channel pairs at a level near -30 dBFS whose channels both code noise substitution in bands that the mid/side mask flags (masks 1 and 2, in long windows "
                      "and in eight short windows of groups of 3, 2, 2 and 1), which both channels fill with the same noise, and one pair without a flag.",
                      pairs, rows2, [stream("AAC LC", RATE, 2, "Stereo", 4 * 1024)], statistics("ADTS", streams), [["aac.window-slopes-differ", "a window sequence whose left slope differs"]])
    # Scalefactors near the largest (250) with a TNS filter of order 12 whose coefficients are the largest magnitudes (4-bit indexes 7 and 8, 0.998 each way): the filter's gain over the
    # band is far past what a float holds without a limit.
    wild = [[(9, 40), (0, 9)]]
    blow = [channel(M.ONLY_LONG, 1, wild, gain=250, tns=[[(40, 12, 0, 0, 1, [7, 8, 7, 8, 7, 8, 7, 8, 7, 8, 7, 8])]], seed=70 + n) for n in range(3)]
    overflow, rows3 = concat(adts_frames([M.raw_data_block([M.Element(M.SCE, ics=ics)]) for ics in blow]))
    third = manifest("Mono frames with scalefactors near the largest and a TNS filter of order 12 with coefficients of 0.998: the decoded values saturate and stay finite.",
                     overflow, rows3, [stream("AAC LC", RATE, 1, "Mono", 3 * 1024)], statistics("ADTS", blow))
    return [("loud-pulse-tns.aac", data, first), ("loud-noise-pair.aac", pairs, second), ("tns-overflow.aac", overflow, third)]


STEREO_CODINGS = ["Left/right stereo", "Mid/side stereo", "Intensity stereo", "Mid/side and intensity stereo", "Reserved"]


def classify(first, second, common, mask, ms_used):
    """The stereo coding of a channel pair (ISO/IEC 14496-3 4.6.8), whether or not it has a common window: ms_mask_present 3 is reserved and
    comes first; intensity stereo is any band of the second channel with codebook 14 or 15; mid/side is any band whose ms_used flag is set
    (ms_mask_present 2 sets every band; the flags need a common window) where neither channel has codebook 13, 14 or 15, as FFmpeg's
    apply_mid_side_stereo requires (faad2's ms_decode checks the first channel's noise only). Written from the decoders' rule, not from the analyzer."""
    if common and mask == 3:
        return 4
    intensity = midside = False
    for g, (books, firsts) in enumerate(zip(second.codebooks(), first.codebooks())):
        for sfb, book in enumerate(books):
            if book in (14, 15):
                intensity = True
            elif common and (mask == 2 or (mask == 1 and ms_used[g][sfb])) and book != 13 and firsts[sfb] < 13:
                midside = True
    return 3 if intensity and midside else 2 if intensity else 1 if midside else 0


def round_half_even(numerator, denominator):
    quotient, rest = divmod(numerator, denominator)
    return quotient + (1 if rest * 2 > denominator or (rest * 2 == denominator and quotient % 2) else 0)


def coding_fields(prefix, units, rate=RATE, short_frame=False):
    """The statistics Block Sizes and Coded Bandwidth of units given as the lists of their channel streams (Ics objects, coupling channel
    elements left out): the share of long (with start and stop) and short windows, and the lowest, most common and highest of the units'
    highest top of a band in whole hertz, from the band tables of aac_tables.py through the model."""
    streams = [s for unit in units for s in unit]
    fields = {}
    short = sum(1 for s in streams if s.sequence == M.EIGHT_SHORT)
    for name, count in (("Long, start and stop", len(streams) - short), ("Short", short)):
        if count:
            fields[f"{prefix}/Statistics/Block Sizes/{name}"] = share(count, len(streams), "channel stream")
    index = rate_index(rate)
    tops = []
    for unit in units:
        hertz = [round_half_even(M.bands(index, s.sequence, short_frame)[s.max_sfb] * rate,
                                 2 * ((120 if short_frame else 128) if s.sequence == M.EIGHT_SHORT else (960 if short_frame else 1024))) for s in unit]
        if hertz:
            tops.append(max(hertz))
    if tops:
        common = min(tops, key=lambda t: (-tops.count(t), t))
        fields.update({f"{prefix}/Statistics/Coded Bandwidth/Lowest": f"{min(tops)} Hz", f"{prefix}/Statistics/Coded Bandwidth/Most Common": f"{common} Hz",
                       f"{prefix}/Statistics/Coded Bandwidth/Highest": f"{max(tops)} Hz"})
    fields.update(value_fields(prefix, units, rate, short_frame))
    return fields


def value_figures(ics, index, short_frame=False):
    """(the upper edge of the highest nonzero line within a window, nonzero values, escape-coded values) of what the model wrote for a channel stream: the values of
    codebooks 1 to 11 in the order the band codes them (the windows of a group one after another), pulse data left out; codebook 11 codes 16 and more as an escape."""
    offsets = M.bands(index, ics.sequence, short_frame)
    last = nonzero = escapes = 0
    for g, books in enumerate(ics.codebooks()):
        for sfb, book in enumerate(books):
            if not 1 <= book <= 11:
                continue
            width = offsets[sfb + 1] - offsets[sfb]
            for at, value in enumerate(ics.values.get((g, sfb), [0] * (width * ics.groups[g]))):
                if value:
                    nonzero += 1
                    last = max(last, offsets[sfb] + at % width + 1)
                    escapes += 1 if book == 11 and abs(value) >= 16 else 0
    return last, nonzero, escapes


def value_fields(prefix, units, rate=RATE, short_frame=False):
    """The statistics "Last Non-Zero Coefficient" (the lowest, most common and highest over the units of the highest nonzero line of their channel streams, in whole hertz),
    "Non-Zero Coefficients" and "Escape-Coded Coefficients", from the values the model wrote."""
    index = rate_index(rate)
    tops = []
    nonzero = escapes = 0
    for unit in units:
        hertz = []
        for s in unit:
            last, count, escaped = value_figures(s, index, short_frame)
            nonzero += count
            escapes += escaped
            if last:
                lines = (120 if short_frame else 128) if s.sequence == M.EIGHT_SHORT else (960 if short_frame else 1024)
                hertz.append(round_half_even(last * rate, 2 * lines))
        if hertz:
            tops.append(max(hertz))
    fields = {}
    if tops:
        common = min(tops, key=lambda t: (-tops.count(t), t))
        fields.update({f"{prefix}/Statistics/Last Non-Zero Coefficient/Lowest": f"{min(tops)} Hz", f"{prefix}/Statistics/Last Non-Zero Coefficient/Most Common": f"{common} Hz",
                       f"{prefix}/Statistics/Last Non-Zero Coefficient/Highest": f"{max(tops)} Hz"})
    if nonzero:
        fields[f"{prefix}/Statistics/Non-Zero Coefficients"] = f"{nonzero} coefficient{'' if nonzero == 1 else 's'}"
        fields[f"{prefix}/Statistics/Escape-Coded Coefficients"] = share(escapes, nonzero, "coefficient")
    return fields


def pair_statistics(prefix, pairs):
    """The statistics of channel pairs that a model description (second channel, common, mask, ms_used, first channel) fixes: the common
    window and intensity stereo counts, the share of the second channels' bands in intensity stereo, and the stereo codings."""
    fields = {f"{prefix}/Statistics/Channel Pairs/Common Window": share(sum(1 for p in pairs if p[1]), len(pairs), "element")}
    intensity = [any(b in (14, 15) for g in p[0].codebooks() for b in g) for p in pairs]
    fields[f"{prefix}/Statistics/Channel Pairs/Intensity Stereo"] = share(sum(intensity), len(pairs), "element")
    bands = sum(len(g) for p in pairs for g in p[0].codebooks())
    intensity_bands = sum(1 for p in pairs for g in p[0].codebooks() for b in g if b in (14, 15))
    if intensity_bands:
        fields[f"{prefix}/Statistics/Channel Pairs/Intensity Stereo Bands"] = f"{tenths(intensity_bands, bands)} %"
        # Out of phase: codebook 14, or codebook 15 inverted by ms_used, which only ms_mask_present 1 does here (faad2's invert_intensity and
        # fdk-aac, whose stereo.cpp clears the mid/side flags before intensity stereo; FFmpeg inverts for mask 2 too)
        out = sum(1 for second, common, mask, used, _ in pairs for g, books in enumerate(second.codebooks()) for sfb, book in enumerate(books)
                  if book in (14, 15) and (book == 14) != bool(common and mask == 1 and used[g][sfb]))
        fields[f"{prefix}/Statistics/Channel Pairs/Out of Phase"] = share(out, intensity_bands, "band")
    codes = [classify(p[4], p[0], p[1], p[2], p[3]) for p in pairs]
    for code, name in enumerate(STEREO_CODINGS):
        if code in codes:
            fields[f"{prefix}/Statistics/Stereo Coding/{name}"] = share(codes.count(code), len(pairs), "channel pair")
    return fields


def stereo_coding():
    """Channel pairs of every stereo coding: the intensity stereo bands of the second channel make a pair intensity stereo whether or not it
    has a common window, an ms_used flag on an intensity stereo band inverts the sign and does not code mid/side, and an ms_used flag on a band
    that either channel codes with noise substitution signals correlated noise and codes none either."""
    first = channel(M.ONLY_LONG, 1, [[(11, 4), (5, 10), (1, 20), (0, 6)]], seed=21)
    plain = channel(M.ONLY_LONG, 1, [[(9, 6), (3, 14), (1, 14), (0, 6)]], gain=90, seed=22)
    partial = channel(M.ONLY_LONG, 1, [[(9, 6), (3, 14), (15, 10), (14, 4), (0, 6)]], gain=95, seed=23)
    whole = channel(M.ONLY_LONG, 1, [[(15, 40)]], gain=95, seed=24)
    short = channel(M.EIGHT_SHORT, 0, [[(11, 4), (15, 10)], [(3, 6), (14, 8)]], groups=(4, 4), seed=25)
    noisy_first = channel(M.ONLY_LONG, 1, [[(11, 4), (13, 10), (1, 20), (0, 6)]], seed=27)
    noisy = channel(M.ONLY_LONG, 1, [[(9, 6), (3, 14), (13, 10), (1, 4), (0, 6)]], gain=90, seed=28)
    noisy_intensity = channel(M.ONLY_LONG, 1, [[(9, 6), (3, 14), (13, 5), (15, 10), (0, 5)]], gain=95, seed=29)
    long_first = channel(M.ONLY_LONG, 1, [[(11, 30)]], seed=26)

    def flags(*bands):
        return [[1 if b in bands else 0 for b in range(40)]]

    # (second channel, common window, ms_mask_present, ms_used, first channel)
    cases = [
        (plain, True, 0, None, first), (plain, True, 1, flags(), first), (plain, True, 1, flags(0, 3, 7), first), (plain, True, 2, None, first),
        (partial, True, 1, flags(22, 23), first), (partial, True, 1, flags(3, 25), first), (whole, True, 2, None, first), (partial, True, 2, None, first),
        (partial, False, 0, None, first), (plain, False, 0, None, first), (short, False, 0, None, long_first),
        # ms_used only on noise substitution bands: of the second channel, of the first, and with intensity stereo bands beside them
        (noisy, True, 1, flags(22, 23), first), (plain, True, 1, flags(6, 7), noisy_first), (noisy, True, 1, flags(3, 22), first),
        (noisy_intensity, True, 1, flags(21, 22, 27), first), (noisy, True, 2, None, noisy_first),
    ]
    made = [pair_block(mask, used, common=common, first=a, second=b) for b, common, mask, used, a in cases]
    data, rows = concat(adts_frames([m[0] for m in made], channels=2))
    pairs = [(b, common, mask, used, a) for b, common, mask, used, a in cases]
    units = [list(m[1]) for m in made]
    fields = {**pair_statistics("ADTS", pairs), **statistics("ADTS", [s for u in units for s in u]), **coding_fields("ADTS", units)}
    return "stereo-coding.aac", data, manifest(
        "Sixteen channel pairs: left/right (mask 0, and mask 1 with no flag set), mid/side (mask 1 with flags, mask 2), intensity stereo with the "
        "mid/side flag set only on intensity stereo bands, in every band of a mask 2 pair, and in pairs without a common window (long and short "
        "windows), both coding tools at once (mask 1 and mask 2 with intensity stereo bands), and the mid/side flag on noise substitution bands "
        "of either channel, which codes no mid/side, alone, beside a flag on an ordinary band and beside intensity stereo bands.",
        data, rows, [stream("AAC LC", RATE, 2, "Stereo", len(cases) * 1024)], fields,
        [["aac.intensity-without-common-window", "without a common window"], ["aac.window-slopes-differ", "a window sequence whose left slope differs"]])


def stereo_coding_reserved():
    """Channel pairs with ms_mask_present 3, which the specification reserves."""
    first = channel(M.ONLY_LONG, 1, [[(11, 4), (5, 10), (1, 20), (0, 6)]], seed=31)
    plain = channel(M.ONLY_LONG, 1, [[(9, 6), (3, 14), (1, 14), (0, 6)]], gain=90, seed=32)
    partial = channel(M.ONLY_LONG, 1, [[(9, 6), (3, 14), (15, 10), (14, 4), (0, 6)]], gain=95, seed=33)
    cases = [(plain, True, 3, None, first), (partial, True, 3, None, first), (plain, True, 0, None, first)]
    made = [pair_block(mask, used, common=common, first=a, second=b) for b, common, mask, used, a in cases]
    data, rows = concat(adts_frames([m[0] for m in made], channels=2))
    units = [list(m[1]) for m in made]
    fields = {**pair_statistics("ADTS", cases), **statistics("ADTS", [s for u in units for s in u]), **coding_fields("ADTS", units)}
    return "stereo-coding-reserved.aac", data, manifest(
        "Channel pairs with ms_mask_present 3, which is reserved: with and without intensity stereo bands, which it takes precedence over, and one "
        "pair of left and right.", data, rows, [stream("AAC LC", RATE, 2, "Stereo", len(cases) * 1024)], fields,
        [["aac.ms-mask-reserved", "ms_mask_present 3"]],
        skip={"decode": "ffmpeg rejects ms_mask_present 3 (aacdec_template.c, decode_cpe: ms_present = 3 is reserved).",
              "faad_trace": "faad2 rejects ms_mask_present 3 (syntax.c, error 32) and stops at the first frame it rejects."})


def stereo_coding_elements():
    """Frames of a 5.1 stream whose blocks hold several channel pairs: a single channel element, two channel pair elements of different
    codings and an LFE element, so that a unit has six channel streams and two pairs."""
    center = channel(M.ONLY_LONG, 1, [[(11, 12), (5, 20)]], seed=41)
    lfe = channel(M.ONLY_LONG, 1, [[(1, 8)]], seed=42)
    long_a = channel(M.ONLY_LONG, 1, [[(11, 4), (5, 10), (1, 20), (0, 6)]], seed=43)
    long_b = channel(M.ONLY_LONG, 1, [[(9, 6), (3, 14), (1, 14), (0, 6)]], gain=90, seed=44)
    intense = channel(M.ONLY_LONG, 1, [[(9, 6), (3, 14), (15, 10), (14, 4), (0, 6)]], gain=95, seed=45)
    start = channel(M.LONG_START, 0, [[(11, 6), (3, 20)]], seed=46)
    start_b = channel(M.LONG_START, 0, [[(9, 6), (3, 20)]], seed=47)
    shorts = channel(M.EIGHT_SHORT, 0, [[(11, 4), (5, 6)]], groups=(8,), seed=48)
    shorts_b = channel(M.EIGHT_SHORT, 0, [[(9, 4), (5, 6)]], groups=(8,), seed=49)
    used = [[1 if b % 3 == 0 else 0 for b in range(40)]]
    frames = [
        # a mid/side pair and an intensity stereo pair
        [(M.SCE, center), (M.CPE, long_a, long_b, True, 1, used), (M.CPE, long_a, intense, True, 0, None), (M.LFE, lfe)],
        # a left/right pair and a pair of long start windows with mid/side stereo in every band
        [(M.SCE, center), (M.CPE, long_a, long_b, True, 0, None), (M.CPE, start, start_b, True, 2, None), (M.LFE, lfe)],
        # a pair of eight short windows and one without a common window
        [(M.SCE, center), (M.CPE, shorts, shorts_b, True, 2, None), (M.CPE, long_a, long_b, False, 0, None), (M.LFE, lfe)],
    ]
    blocks, units, pairs = [], [], []
    for frame in frames:
        elements, streams = [], []
        for item in frame:
            if item[0] == M.CPE:
                _, a, b, common, mask, flags = item
                elements.append(M.Element(M.CPE, ics=(a, b), common=common, mask=mask, ms_used=flags))
                streams += [a, b]
                pairs.append((b, common, mask, flags, a))
            else:
                elements.append(M.Element(item[0], ics=item[1]))
                streams.append(item[1])
        blocks.append(M.raw_data_block(elements, rate_index=rate_index(RATE)))
        units.append(streams)
    data, rows = concat(adts_frames(blocks, channels=6))
    fields = {**pair_statistics("ADTS", pairs), **statistics("ADTS", [s for u in units for s in u]), **coding_fields("ADTS", units),
              "ADTS/Statistics/Block Usage": f"{sum(len(u) for u in units)} channel streams"}
    return "stereo-coding-elements.aac", data, manifest(
        "Three frames of a 5.1 stream, each holding a single channel element, two channel pair elements and an LFE element: six channel streams "
        "and two pairs in every unit, with mid/side, intensity stereo, left/right, mid/side in long start windows, eight short windows and a pair "
        "without a common window.", data, rows, [stream("AAC LC", RATE, 6, "5.1", len(frames) * 1024)], fields,
        [["aac.lfe-restrictions", "3 raw data blocks hold an LFE channel stream"], ["aac.window-slopes-differ", "a window sequence whose left slope differs"]])


SBR_HEADER = {"amp_res": 1, "start": 5, "stop": 9, "xover": 0}


def sbr_units(count, layout, ps=False, core=22050, coupled=False, crc=False, gain=100, low=()):
    """Blocks of a channel element at the core rate and an SBR fill element after it; every fourth carries the SBR header."""
    counts = M.sbr_bands(SBR_HEADER, 2 * core)
    out = []
    for i in range(count):
        header = SBR_HEADER if i % 4 == 0 else None
        payload = M.sbr_payload(counts, header, pair=layout != "mono", coupled=coupled, ps=ps, crc=crc)
        fill = M.Element(M.FIL, payload=payload)
        if layout == "mono":
            ics = channel(M.ONLY_LONG, 1, [[*low, (11, 4), (3, 16)]], gain=gain, seed=i, rate=core)
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


def ps_phase_adts():
    """The 20-band parametric stereo fixture with phase differences and procedure Rb (mp4/ps-phase-20-bands.m4a) as ADTS, for DecoderWorkTests, which repeats ADTS frames."""
    blocks = sbr_units(12, "mono", ps=("phase", 1, 4, 2), gain=135)
    data, rows = concat(adts_frames(blocks, rate=22050, channels=1))
    stats = "ADTS/Statistics/SBR"
    fields = {stats + "/Parametric Stereo": share(12, 12, "access unit"),
              stats + "/Parametric Stereo/Parametric Stereo Mixing/Procedure Rb": share(12, 12, "raw data block"),
              stats + "/Parametric Stereo/Parametric Stereo Phase/Phase differences": share(12, 12, "raw data block"),
              "ADTS/Frame/Raw Data Block/FIL/Extension Payload/SBR Data/Parametric Stereo Extension": "2 (EXT_ID_PS)"}
    return "ps-phase.aac", data, manifest(
        "ADTS at a core rate of 22050 Hz, mono, whose access units carry SBR data with a parametric stereo extension of intensity differences and coherences in 20 bands (modes 1 and 4: "
        "mixing procedure Rb) in 2 envelopes, and the inter-channel and overall phase differences of the extension: HE-AAC v2, signaled only implicitly.", data, rows,
        [stream("HE-AAC v2", 44100, 2, "Parametric stereo", 12 * 2048, ffprobe_profile="LC", ffprobe_channels=1)], fields,
        [["aac.implicit-sbr", "and 12 carry the parametric stereo extension: the stream is HE-AAC v2"]],
        skip={"faad": "faad -i prints twice the rate of any ADTS stream at 24 kHz or less, whatever its frames hold."}, rate=22050)


def ps_phase_then_none_adts():
    """Six blocks with phase differences, then six whose parametric stereo data has enable_ext 0: the phases stop with them (8.4.2, enable_ext)."""
    blocks = sbr_units(6, "mono", ps=("phase", 1, 4, 2), gain=135) + sbr_units(6, "mono", ps=True, gain=135)
    data, rows = concat(adts_frames(blocks, rate=22050, channels=1))
    stats = "ADTS/Statistics/SBR"
    fields = {stats + "/Parametric Stereo": share(12, 12, "access unit"),
              stats + "/Parametric Stereo/Parametric Stereo Phase/Phase differences": share(6, 12, "raw data block"),
              stats + "/Parametric Stereo/Parametric Stereo Phase/No phase differences": share(6, 12, "raw data block")}
    return "ps-phase-then-none.aac", data, manifest(
        "ADTS at a core rate of 22050 Hz, mono, whose first six access units carry parametric stereo data with phase differences and mixing procedure Rb, and the last six parametric stereo data without the extension: "
        "HE-AAC v2, signaled only implicitly.", data, rows,
        [stream("HE-AAC v2", 44100, 2, "Parametric stereo", 12 * 2048, ffprobe_profile="LC", ffprobe_channels=1)], fields,
        [["aac.implicit-sbr", "and 12 carry the parametric stereo extension: the stream is HE-AAC v2"]],
        skip={"faad": "faad -i prints twice the rate of any ADTS stream at 24 kHz or less, whatever its frames hold."}, rate=22050)


def sbr_fixvar_adts():
    """Channel pairs followed by SBR data whose grids are FIXVAR frames of two and three envelopes of different frequency resolutions in
    both channels, each envelope coded with another difference: the resolutions are sent last envelope first (4.4.2.8), so a reader that
    takes them in envelope order finds other band counts, and its envelopes, which take as many bits as the codewords of their differences and
    their bands come to, run past the payload (aac.sbr-past-fill) for these grids. Apple's HE-AAC encoder writes such frames."""
    counts = M.sbr_bands(SBR_HEADER, 44100)
    channels = [[(1, 12), (0, 5), (0, 20)], [(1, 0), (0, 0)]]
    blocks = []
    for i in range(12):
        header = SBR_HEADER if i % 4 == 0 else None
        fill = M.Element(M.FIL, payload=M.sbr_payload(counts, header, pair=True, fixvar=channels))
        block, _ = pair_block(0, rate=22050, extra=[fill], first=channel(M.ONLY_LONG, 1, [[(11, 4), (3, 16)]], seed=i, rate=22050),
                              second=channel(M.ONLY_LONG, 1, [[(9, 4), (1, 16)]], seed=i + 1, rate=22050))
        blocks.append(block)
    data, rows = concat(adts_frames(blocks, rate=22050, channels=2))
    return "sbr-fixvar-resolutions.aac", data, manifest(
        "ADTS at a core rate of 22050 Hz whose channel pairs are each followed by SBR data with FIXVAR frame grids of three and two envelopes whose "
        "frequency resolutions differ (sent last envelope first): the first channel's envelopes are of high, low and low resolution, the second's of "
        "high and low.", data, rows,
        [stream("HE-AAC", 44100, 2, "Stereo", 12 * 2048, ffprobe_profile="LC")], sbr_fields("ADTS", 12, 3, False),
        [["aac.implicit-sbr", "ADTS cannot signal SBR or PS explicitly, but 12 of 12 raw data blocks hold SBR data: the stream is HE-AAC"]],
        skip={"faad": "faad -i prints twice the rate of any ADTS stream at 24 kHz or less, whatever its frames hold; the trace check reads the SBR data instead."},
        rate=22050)


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
        [stream("HE-AAC", 44100, 2, "Stereo", 12 * 2048, ffprobe_profile="LC")], fields,
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
        [stream("HE-AAC", 44100, 2, "Stereo", FAAC_UNITS * 2048, encoder=("FAAC", "Medium") if named else None, ffprobe_profile="LC")], fields,
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
    units = [b for b, _ in sbr_units(12, "mono", ps="wide")]
    out.append(mp4_fixture(
        "ps-34-bands", "Mono AAC LC at 22050 Hz in its configuration, whose access units carry SBR data with a parametric stereo extension of intensity "
        "differences and coherences in 34 bands (mode 2): HE-AAC v2, signaled only implicitly, whose decoder works at the 34-band resolution.", asc(2, 22050, 1), 22050, 1, units, "HE-AAC v2", 44100, 2, "Parametric stereo",
        {stats + "/SBR/Parametric Stereo": share(12, 12, "access unit"),
         "MP4/mdat/Track 1 Samples/AAC/Access Unit/FIL/Extension Payload/SBR Data/Parametric Stereo Extension": "2 (EXT_ID_PS)"},
        [["aac.implicit-sbr", "and 12 carry the parametric stereo extension: the stream is HE-AAC v2"]],
        {"label": "LC AAC", "channels": 2, "rate": 44100}, ffprobe_profile="HE-AACv2"))
    units = [b for b, _ in sbr_units(12, "mono", ps="mixed")]
    out.append(mp4_fixture(
        "ps-mixed-bands", "Mono AAC LC at 22050 Hz in its configuration, whose access units carry SBR data with a parametric stereo extension of intensity differences in 34 bands "
        "(mode 2) and coherences in 20 (mode 1), each band with a value of its own: HE-AAC v2, signaled only implicitly, whose decoder maps the 20 coherence bands onto 34 "
        "with the averages of Table 8.45.", asc(2, 22050, 1), 22050, 1, units, "HE-AAC v2", 44100, 2, "Parametric stereo",
        {stats + "/SBR/Parametric Stereo": share(12, 12, "access unit"),
         "MP4/mdat/Track 1 Samples/AAC/Access Unit/FIL/Extension Payload/SBR Data/Parametric Stereo Extension": "2 (EXT_ID_PS)"},
        [["aac.implicit-sbr", "and 12 carry the parametric stereo extension: the stream is HE-AAC v2"]],
        {"label": "LC AAC", "channels": 2, "rate": 44100}, ffprobe_profile="HE-AACv2"))
    # The full parametric stereo decoder's tools: inter-channel and overall phase differences in the extension, and mixing procedure Rb (coherence modes 3 to 5).
    for name, iid_mode, icc_mode, envelopes, bands, quant in (("ps-phase-10-bands", 3, 3, 2, "10", "fine"), ("ps-phase-20-bands", 1, 4, 2, "20", "coarse"), ("ps-phase-34-bands", 2, 5, 2, "34", "coarse")):
        units = [b for b, _ in sbr_units(12, "mono", ps=("phase", iid_mode, icc_mode, envelopes), gain=135)]
        count = {"10": 5, "20": 11, "34": 17}[bands]
        out.append(mp4_fixture(
            name, f"Mono AAC LC at 22050 Hz in its configuration, whose access units carry SBR data with a parametric stereo extension of {quant} intensity differences and coherences in {bands} bands "
            f"(modes {iid_mode} and {icc_mode}: mixing procedure Rb) in {envelopes} envelopes, and the inter-channel and overall phase differences of the extension, {count} to an envelope: HE-AAC v2, signaled "
            "only implicitly, whose decoder applies the phases.", asc(2, 22050, 1), 22050, 1, units, "HE-AAC v2", 44100, 2, "Parametric stereo",
            {stats + "/SBR/Parametric Stereo": share(12, 12, "access unit"),
             stats + "/SBR/Parametric Stereo/Parametric Stereo Mixing/Procedure Rb": share(12, 12, "raw data block"),
             stats + "/SBR/Parametric Stereo/Parametric Stereo Phase/Phase differences": share(12, 12, "raw data block"),
             "MP4/mdat/Track 1 Samples/AAC/Access Unit/FIL/Extension Payload/SBR Data/Parametric Stereo Extension": "2 (EXT_ID_PS)"},
            [["aac.implicit-sbr", "and 12 carry the parametric stereo extension: the stream is HE-AAC v2"]],
            {"label": "LC AAC", "channels": 2, "rate": 44100}, ffprobe_profile="HE-AACv2"))
    units = [b for b, _ in sbr_units(12, "mono", ps=("phase", 1, 4, 2), gain=135, low=((0, 8),))]
    out.append(mp4_fixture(
        "ps-phase-high-core", "Mono AAC LC at 22050 Hz in its configuration whose core has no line below about 690 Hz (the first eight scalefactor bands are empty), with the parametric stereo extension of ps-phase-20-bands.m4a: "
        "HE-AAC v2, signaled only implicitly. the phases act on the QMF channels above the lowest, where FFmpeg's decoder and the analyzer's agree as closely as on any parametric stereo file at this level.", asc(2, 22050, 1), 22050, 1, units,
        "HE-AAC v2", 44100, 2, "Parametric stereo",
        {stats + "/SBR/Parametric Stereo": share(12, 12, "access unit"),
         stats + "/SBR/Parametric Stereo/Parametric Stereo Phase/Phase differences": share(12, 12, "raw data block")},
        [["aac.implicit-sbr", "and 12 carry the parametric stereo extension: the stream is HE-AAC v2"]],
        {"label": "LC AAC", "channels": 2, "rate": 44100}, ffprobe_profile="HE-AACv2"))
    for name, options, what, code, text in (
            ("ps-phase-overrun", {"overrun": True}, "whose extension states two bytes less than its phase data takes, so that the data runs past it", "aac.ps-extension-overrun",
             "12 raw data blocks hold parametric stereo phase data that runs past its extension"),
            ("ps-phase-without-intensity", {"no_iid": True}, "with phase differences but no intensity differences (enable_iid 0), which 8.4.2 does not allow",
             "aac.ps-phase-without-intensity", "12 raw data blocks hold parametric stereo phase differences with enable_iid 0")):
        units = [b for b, _ in sbr_units(12, "mono", ps=("phase", 1, 4, 2, options.get("overrun", False), options.get("no_iid", False)), gain=135)]
        out.append(mp4_fixture(
            name, f"Mono AAC LC at 22050 Hz in its configuration, whose access units carry SBR data with a parametric stereo extension in 20 bands and 2 envelopes {what}: HE-AAC v2, signaled only implicitly.",
            asc(2, 22050, 1), 22050, 1, units, "HE-AAC v2", 44100, 2, "Parametric stereo",
            {stats + "/SBR/Parametric Stereo": share(12, 12, "access unit")},
            [["aac.implicit-sbr", "and 12 carry the parametric stereo extension: the stream is HE-AAC v2"], [code, text]],
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
    blocks, units, pairs = [], [], []
    for n in range(4):
        pair = (channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=n), channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=n + 1))
        cpe = M.Element(M.CPE, ics=pair, mask=0)
        cce = M.Element(M.CCE, tag=0, independent=n % 2, targets=[(1, 0, 1, 1)], ics=channel(M.ONLY_LONG, 1, [[(11, 4), (3, 4)]], seed=n + 2))
        blocks.append(M.raw_data_block([cpe, cce]))
        # the coupling channel element's stream is no channel stream of the statistics or the series
        units.append(list(pair))
        pairs.append((pair[1], True, 0, None, pair[0]))
    data, rows = concat(adts_frames(blocks, channels=2))
    out.append(("coupling.aac", data, manifest(
        "A channel pair and a coupling channel element coupled to both its channels, with a gain element list for the second, in a stream of "
        "channel configuration 2, which provides for no coupling channel element.", data, rows, [stream("AAC LC", RATE, 2, "Stereo", 4 * 1024)],
        {"ADTS/Frame/Raw Data Block/CCE/Target": "CPE 0, both channels", "ADTS/Frame/Raw Data Block/CCE/Gain Element Lists": "1",
         "ADTS/Frame[1]/Raw Data Block/CCE/IsIndependentlySwitched": "true",
         "ADTS/Statistics/Block Usage": "8 channel streams", **pair_statistics("ADTS", pairs), **coding_fields("ADTS", units)},
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
        "inside its spectral data, an escape sequence of nine leading ones, and predictor data in AAC LC. Each block's reading stops there.", data, rows, [stream("AAC LC", RATE, 1, "Mono", len(rows) * 1024, lost_samples=6 * 1024)],
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
        [[code, text] for _, code, text in soft] + [["aac.elements-mismatch", "other than channel configuration 1 defines"], ["aac.layout-changed", "channel elements in another order than the block before it"], ["aac.window-slopes-differ", "a window sequence whose left slope differs"]],
        skip={"ffprobe": "ffmpeg rejects some of the frames.", "faad": "faad2 rejects some of the frames.", "decode": "decoders reject some of the frames",
              "faad_trace": "faad2 stops at frames it rejects."})))
    return out


# --- Gate 2 fixtures: low-delay SBR headers, parametric stereo, signatures per stream, SBR evidence, missing blocks, prediction bands, rates ----

def mp4_only(name, description, config, rate, channels, samples, durations, streams, fields, findings, crosscheck, chunk=10, timescale=None):
    """An MP4 fixture of one AAC track whose access units are the given bytes, with the manifest written from the arguments."""
    from make_mp4_fixtures import aac_entry, boxes_of, unit_list
    track = Track(1, samples, rate, channels, aac_entry(config, rate, channels), durations=durations, chunk=chunk, timescale=timescale)
    data, records, offsets = movie([track])
    return name, data, {"description": description, "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)), "streams": streams,
                        "units": unit_list(offsets[0], samples, chunk), "fields": fields, "findings": [list(f) for f in findings], "crosscheck": crosscheck}


# fdk-aac's channel_configuration_array (tpdec_asc.cpp) and ld_sbr_header: one sbr_header() for each single channel and channel pair element
# of the channel configuration, none for its LFE element.
ELD_CONFIGURATIONS = [
    (1, 1, "Mono", ["front center"]), (2, 1, "Stereo", ["front left", "front right"]),
    (3, 2, "3.0", ["front center", "front left", "front right"]),
    (4, 3, "4.0", ["front center", "front left", "front right", "back center"]),
    (5, 3, "5.0", ["front center", "front left", "front right", "back left", "back right"]),
    (6, 3, "5.1", ["front center", "front left", "front right", "back left", "back right", "LFE"]),
    (7, 4, "7.1", ["front center", "front left of center", "front right of center", "front left", "front right", "back left", "back right", "LFE"]),
    # Configuration 13 (22.2): fdk-aac's EldSpecificConfig_Parse counts no header for it (tpdec_asc.cpp), and neither does MediaInfo.
    (13, 0, "22.2", ["front center", "front left of center", "front right of center", "front left", "front right", "side left", "side right", "back left",
                     "back right", "back center", "LFE", "LFE 2", "top front center", "top front left", "top front right", "top side left", "top side right",
                     "top center", "top back left", "top back right", "top back center", "bottom front center", "bottom front left", "bottom front right"]),
]


def eld_ld_sbr():
    """AAC ELD with low-delay SBR for each channel configuration with a count of headers: the headers, then an ELD extension and the terminator and
    epConfig after them, so a wrong header count shows as a lost extension. The SBR rate is the core rate (ldSbrSamplingRate 0)."""
    out = []
    for configuration, headers, mode, layout in ELD_CONFIGURATIONS:
        bits = Bits()
        bits.put(31, 5).put(39 - 32, 6).put(rate_index(48000), 4).put(configuration, 4)
        bits.put(0, 1).put(0, 1).put(0, 1).put(0, 1)  # frameLengthFlag (512 samples), resilience flags
        bits.put(1, 1).put(0, 1).put(0, 1)  # ldSbrPresentFlag, ldSbrSamplingRate, ldSbrCrcFlag
        for _ in range(headers):
            bits.put(1, 1).put(5, 4).put(9, 4).put(0, 3).put(0, 2).put(0, 1).put(0, 1)  # amp_res 1, start 5, stop 9, no header extras
        bits.put(1, 4).put(1, 4).put(0xA5, 8)  # an extension of type 1 and one byte
        bits.put(0, 4)  # ELDEXT_TERM
        bits.put(0, 2)  # epConfig
        config = bits.align().bytes()
        samples = [bytes([(i * 7) % 30 + 40]) for i in range(20)]
        asc = "MP4/moov/trak/mdia/minf/stbl/stsd/mp4a/esds/ES_Descriptor/DecoderConfigDescriptor/DecoderSpecificInfo/AudioSpecificConfig"
        fields = {asc + "/Channel Configuration": f"{configuration} ({ELD_NAMES[configuration]})", asc + "/HasLowDelaySbr": "true",
                  asc + "/ELD Extension": "1 byte", asc + "/ELD Extension Terminator": "", asc + "/Error Protection Config": "0"}
        fields = {k: v for k, v in fields.items() if v != ""}
        entry = stream("ER AAC ELD with SBR", 48000, len(layout), mode, 20 * 512, vbr=True, layout=layout)
        out.append(('mp4',) + mp4_only(
            f"eld-ld-sbr-{configuration}",
            f"AAC ELD, channel configuration {configuration}, with low-delay SBR: "
            + (f"{headers} sbr_header{'' if headers == 1 else 's'} (one for each single channel and channel pair element)" if headers else "no sbr_header (fdk-aac and MediaInfo read none for this configuration)")
            + ", then an ELD extension of one byte, the terminator and epConfig 0.",
            config, 48000, len(layout), samples, [512] * 20, [entry], fields, [],
            {"ffprobe": False, "mp4box": True, "faad": None,
             "skip": {"ffprobe": "ffmpeg cannot open its decoder for ELD with low-delay SBR (\"Not yet implemented in FFmpeg\"), so it lists no stream.",
                      "faad": "faad2 decodes ER AAC LD but not ELD."}}))
    return out


ELD_NAMES = {13: "22.2", 1: "mono", 2: "stereo", 3: "3.0: center, left/right", 4: "4.0: center, left/right, rear center", 5: "5.0: center, left/right, left/right surround",
             6: "5.1: center, left/right, left/right surround, LFE", 7: "7.1: center, left/right, left/right outside front, left/right surround, LFE"}


def parametric_stereo_cores():
    """PS only applies to a core of one channel (fdk-aac's sbrdecoder.cpp, ffmpeg's aacdec_template.c)."""
    from make_mp4_fixtures import adts_header
    core = 22050
    counts = M.sbr_bands(SBR_HEADER, 2 * core)
    blocks = []
    for i in range(12):
        header = SBR_HEADER if i % 4 == 0 else None
        sce = M.Element(M.SCE, ics=channel(M.ONLY_LONG, 1, [[(11, 4), (3, 16)]], seed=i, rate=core))
        first = channel(M.ONLY_LONG, 1, [[(11, 4), (3, 16)]], seed=i, rate=core)
        second = channel(M.ONLY_LONG, 1, [[(9, 4), (1, 16)]], seed=i + 1, rate=core)
        blocks.append(M.raw_data_block([sce, M.Element(M.FIL, payload=M.sbr_payload(counts, header, pair=False, ps=True)),
                                        M.Element(M.CPE, ics=(first, second), common=False),
                                        M.Element(M.FIL, payload=M.sbr_payload(counts, header, pair=True))], rate_index=rate_index(core)))
    data, rows = concat(adts_frames(blocks, rate=core, channels=3))
    stats = "ADTS/Statistics/SBR"
    out = [("adts", "ps-in-multichannel-sce.aac", data, manifest(
        "ADTS at a core rate of 22050 Hz with channel configuration 3 (a single channel element and a channel pair element), each followed by SBR "
        "data, and the single channel element's carrying the parametric stereo extension's ID: decoders apply parametric stereo to a core of one "
        "channel only, so the stream is HE-AAC with three channels and the ID is reported.", data, rows,
        [stream("HE-AAC", 44100, 3, "3.0", 12 * 2048, ffprobe_profile="LC", layout=["front center", "front left", "front right"])],
        {stats: share(12, 12, "access unit"), stats + "/Parametric Stereo": share(12, 12, "access unit"),
         "ADTS/Frame/Raw Data Block/FIL/Extension Payload/SBR Data/Parametric Stereo Extension": "2 (EXT_ID_PS)"},
        [["aac.implicit-sbr", "ADTS cannot signal SBR or PS explicitly, but 12 of 12 raw data blocks hold SBR data: the stream is HE-AAC,"],
         ["aac.ps-without-mono-core", "12 of 12 raw data blocks carry the parametric stereo extension's ID, but the core has 3 channels"]],
        skip={"faad": "faad -i prints twice the rate of any ADTS stream at 24 kHz or less, whatever its frames hold."}, rate=core))]
    units = []
    counts = M.sbr_bands(SBR_HEADER, 2 * core)
    for i in range(12):
        header = SBR_HEADER if i % 4 == 0 else None
        fill = M.Element(M.FIL, payload=M.sbr_payload(counts, header, pair=True))
        block, _ = pair_block(0, rate=core, extra=[fill], first=channel(M.ONLY_LONG, 1, [[(11, 4), (3, 16)]], seed=i, rate=core),
                              second=channel(M.ONLY_LONG, 1, [[(9, 4), (1, 16)]], seed=i + 1, rate=core))
        units.append(block[0] if isinstance(block, tuple) else block)
    # The same layout from a program config element (channel configuration 0): a front single channel element and a front channel pair element.
    def pce(w):
        w.put(0, 4).put(1, 2).put(rate_index(core), 4)
        w.put(2, 4).put(0, 4).put(0, 4).put(0, 2).put(0, 3).put(0, 4)
        w.put(0, 1).put(0, 1).put(0, 1)
        w.put(0, 1).put(0, 4)  # front single channel element 0
        w.put(1, 1).put(0, 4)  # front channel pair element 0
        w.align()
        w.put(0, 8)

    blocks = []
    for i in range(12):
        header = SBR_HEADER if i % 4 == 0 else None
        sce = M.Element(M.SCE, ics=channel(M.ONLY_LONG, 1, [[(11, 4), (3, 16)]], seed=i, rate=core))
        first = channel(M.ONLY_LONG, 1, [[(11, 4), (3, 16)]], seed=i, rate=core)
        second = channel(M.ONLY_LONG, 1, [[(9, 4), (1, 16)]], seed=i + 1, rate=core)
        blocks.append(M.raw_data_block([M.Element(M.PCE, write=pce), sce, M.Element(M.FIL, payload=M.sbr_payload(counts, header, pair=False, ps=True)),
                                        M.Element(M.CPE, ics=(first, second), common=False),
                                        M.Element(M.FIL, payload=M.sbr_payload(counts, header, pair=True))], rate_index=rate_index(core)))
    data, rows = concat(adts_frames(blocks, rate=core, channels=0))
    out.append(("adts", "ps-in-pce-multichannel.aac", data, manifest(
        "ADTS at a core rate of 22050 Hz with channel configuration 0 and a program config element in every block (a front single channel element "
        "and a front channel pair element, three channels), the single channel element followed by SBR data with the parametric stereo extension's "
        "ID: as with channel configuration 3, parametric stereo is not applied to a core of three channels, and the ID is reported.", data, rows,
        [stream("HE-AAC", 44100, 3, "3 channels", 12 * 2048, ffprobe_profile="LC", layout=["front center", "front left", "front right"])],
        {stats: share(12, 12, "access unit"), stats + "/Parametric Stereo": share(12, 12, "access unit")},
        [["aac.implicit-sbr", "ADTS cannot signal SBR or PS explicitly, but 12 of 12 raw data blocks hold SBR data: the stream is HE-AAC,"],
         ["aac.ps-without-mono-core", "12 of 12 raw data blocks carry the parametric stereo extension's ID, but the core has 3 channels"]],
        skip={"faad": "faad -i prints twice the rate of any ADTS stream at 24 kHz or less, whatever its frames hold."}, rate=core)))
    asc_path = "MP4/moov/trak/mdia/minf/stbl/stsd/mp4a/esds/ES_Descriptor/DecoderConfigDescriptor/DecoderSpecificInfo/AudioSpecificConfig"
    out.append(("mp4",) + mp4_only(
        "ps-signaled-over-stereo-core",
        "HE-AAC v2 signaled hierarchically (audio object type 29) over a core of channel configuration 2: decoders apply parametric stereo to a core of "
        "one channel only (ffmpeg's decoder drops the flag, fdk-aac allows one single channel element), so the stream is HE-AAC with two channels and a "
        "warning says so.", asc(2, core, 2, sbr=True, ps=True, signaling="hierarchical", extension_rate=44100), core, 2, units, [1024] * 12,
        [stream("HE-AAC", 44100, 2, "Stereo", 12 * 2048, vbr=True, ffprobe_profile="HE-AAC")],
        {asc_path + "/Audio Object Type": "29 (PS)"},
        [["aac.ps-without-mono-core", "signals PS, but the core has 2 channels"]],
        {"ffprobe": True, "mp4box": True, "decode": True, "faad": {"label": "LC AAC", "channels": 2, "rate": 44100}}))
    return out


def signature_streams():
    """Two joined ADTS streams (a changed sample rate), each with its own encoder signature in a fill element of its first frame: each stream is
    named by its own."""
    def stream_frames(rate, text, count=6):
        # The channel ends on a bit that puts the next fill element's payload on a byte boundary, where text is shown.
        ics = channel(M.ONLY_LONG, 1, [[(11, 4), (3, 16)]], seed=46, rate=rate)
        plain = M.raw_data_block([M.Element(M.SCE, ics=ics)], rate_index=rate_index(rate))
        named = M.raw_data_block([M.Element(M.SCE, ics=ics), M.Element(M.FIL, payload=M.other_payload(text))], rate_index=rate_index(rate))
        return adts_frames([named] + [plain] * (count - 1), rate=rate, channels=1)

    data, rows = concat(stream_frames(44100, b"libfaac 1.40.0") + stream_frames(48000, b"Lavc60.31.102"))
    return [("adts", "two-signature-streams.aac", data, manifest(
        "Six mono frames at 44100 Hz whose first holds the text \"libfaac 1.40.0\" in a fill element, then six at 48000 Hz whose first holds "
        "\"Lavc60.31.102\": two streams, each named by its own signature.", data, rows,
        [stream("AAC LC", 44100, 1, "Mono", 6 * 1024, encoder=("FAAC 1.40.0", "High")),
         stream("AAC LC", 48000, 1, "Mono", 6 * 1024, encoder=("FFmpeg (Lavc60.31.102)", "High"))],
        {}, [["aac.stream-changed", "from AAC LC, 44100 Hz, channel configuration 1 to AAC LC, 48000 Hz"]],
        skip={"ffprobe": "ffmpeg keeps one stream across the change of sample rate.", "faad": "faad2 stops at the change of sample rate.",
              "faad_trace": "faad2 reads ADTS only from the first byte.", "decode": "ffmpeg keeps one stream across the change of sample rate."}))]


def streams_limited():
    """Sixty-six runs of three mono frames that alternate between 44100 and 48000 Hz: 64 streams, and the last 6 frames in no stream."""
    def run(rate):
        ics = channel(M.ONLY_LONG, 1, [[(11, 4), (3, 16)]], seed=46, rate=rate)
        plain = M.raw_data_block([M.Element(M.SCE, ics=ics)], rate_index=rate_index(rate))
        return adts_frames([plain] * 3, rate=rate, channels=1)

    frames = [frame for index in range(66) for frame in run(48000 if index % 2 else 44100)]
    data, rows = concat(frames)
    streams = [stream("AAC LC", 48000 if index % 2 else 44100, 1, "Mono", 3 * 1024) for index in range(64)]
    return [("adts", "streams-limited.aac", data, manifest(
        "Sixty-six runs of three mono frames that alternate between 44100 and 48000 Hz: the first 64 runs are streams, the last 6 frames are in no stream.",
        data, rows, streams, {},
        [["aac.stream-changed", "65 times"], ["aac.streams-limited", "6 frames are in no stream"]],
        skip={"ffprobe": "ffmpeg keeps one stream across the changes of sample rate.", "faad": "faad2 stops at the first change of sample rate.",
              "faad_trace": "faad2 reads ADTS only from the first byte.", "decode": "ffmpeg keeps one stream across the changes of sample rate."},
        golden_hides=["frames"]))]


def sbr_evidence():
    """Streams that carry SBR data no decoder would apply, or in too few blocks, or in frames the analyzer flags as damaged: they stay AAC LC."""
    from make_mp4_fixtures import adts_header
    core = 22050
    counts = M.sbr_bands(SBR_HEADER, 2 * core)
    skip = {"faad": "faad -i prints twice the rate of any ADTS stream at 24 kHz or less, whatever its frames hold."}

    def pair(i, fill=None):
        return pair_block(0, rate=core, extra=[fill] if fill else [], first=channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=i, rate=core),
                          second=channel(M.ONLY_LONG, 1, [[(9, 4)]], seed=i + 1, rate=core))[0]

    out = []
    # One block of ten holds SBR data, which is readable.
    blocks = [pair(i, M.Element(M.FIL, payload=M.sbr_payload(counts, SBR_HEADER, pair=True)) if i == 4 else None) for i in range(10)]
    data, rows = concat(adts_frames(blocks, rate=core, channels=2))
    out.append(("adts", "sbr-in-one-block.aac", data, manifest(
        "ADTS at 22050 Hz of ten channel pairs, the fifth followed by a readable SBR payload with an SBR header and no other holding any: "
        "the first block holds none, which decides the stream, so it stays AAC LC at 22050 Hz.", data, rows,
        [stream("AAC LC", core, 2, "Stereo", 10 * 1024)], {"ADTS/Statistics/SBR": share(1, 10, "access unit")},
        [["aac.sbr-data-sparse", "1 of 10 raw data blocks hold SBR data after a channel element"]],
        skip={**skip, "decode": "ffmpeg prints \"No quantized data read for sbr_dequant\" for the SBR payload in the middle of the stream."}, rate=core)))
    # Three blocks of ten hold SBR data after a channel element, not the first, so the stream stays AAC LC.
    blocks = [pair(i, M.Element(M.FIL, payload=M.sbr_payload(counts, SBR_HEADER if i == 1 else None, pair=True)) if i in (1, 4, 7) else None) for i in range(10)]
    data, rows = concat(adts_frames(blocks, rate=core, channels=2))
    out.append(("adts", "sbr-in-three-blocks.aac", data, manifest(
        "ADTS at 22050 Hz of ten channel pairs, the second, fifth and eighth followed by an SBR payload (the first with an SBR header): three blocks "
        "of ten, but the first block holds none, which decides the stream, so it stays AAC LC at 22050 Hz.", data, rows,
        [stream("AAC LC", core, 2, "Stereo", 10 * 1024)], {"ADTS/Statistics/SBR": share(3, 10, "access unit")},
        [["aac.sbr-data-sparse", "3 of 10 raw data blocks hold SBR data after a channel element"]],
        skip={**skip, "decode": "ffmpeg prints \"No quantized data read for sbr_dequant\" for the SBR payloads in the middle of the stream."}, rate=core)))
    # The first block decides, as in ffmpeg: SBR data in it makes the stream HE-AAC whatever the later blocks hold (aac.sbr-inconsistent says so where most disagree).
    ffmpeg_he = {"ffprobe_streams": "ffmpeg takes implicit SBR from the first block's payload.", "decode": "ffmpeg prints \"No quantized data read for sbr_dequant\" for the blocks after the first."}
    for name, present, text in (
            ("sbr-in-first-block-only.aac", (0,), "the first block of ten followed by an SBR payload with an SBR header and no other holding any"),
            ("sbr-in-first-three-blocks.aac", (0, 1, 2), "the first three blocks of ten followed by an SBR payload (the first with an SBR header)")):
        blocks = [pair(i, M.Element(M.FIL, payload=M.sbr_payload(counts, SBR_HEADER if i == 0 else None, pair=True)) if i in present else None) for i in range(10)]
        data, rows = concat(adts_frames(blocks, rate=core, channels=2))
        out.append(("adts", name, data, manifest(
            f"ADTS at 22050 Hz of ten channel pairs, {text}: the first block decides, so the stream is HE-AAC at 44100 Hz, and most blocks disagree.",
            data, rows, [stream("HE-AAC", 2 * core, 2, "Stereo", 10 * 2048, ffprobe_profile="LC")], {"ADTS/Statistics/SBR": share(len(present), 10, "access unit")},
            [["aac.implicit-sbr", f"{len(present)} of 10 raw data blocks hold SBR data: the stream is HE-AAC"],
             ["aac.sbr-inconsistent", f"but {len(present)} of 10 raw data blocks hold SBR data; the first block that disagrees is raw data block {len(present) + 1}"]],
            skip={**skip, **ffmpeg_he}, rate=core)))
    # Most blocks hold SBR data, but not the first: the stream stays AAC LC.
    blocks = [pair(i, M.Element(M.FIL, payload=M.sbr_payload(counts, SBR_HEADER if i == 1 else None, pair=True)) if i > 0 else None) for i in range(10)]
    data, rows = concat(adts_frames(blocks, rate=core, channels=2))
    out.append(("adts", "sbr-in-all-but-first-block.aac", data, manifest(
        "ADTS at 22050 Hz of ten channel pairs, all but the first followed by an SBR payload (the second with an SBR header): the first block decides, so the "
        "stream stays AAC LC at 22050 Hz, and most blocks disagree.", data, rows,
        [stream("AAC LC", core, 2, "Stereo", 10 * 1024)], {"ADTS/Statistics/SBR": share(9, 10, "access unit")},
        [["aac.sbr-data-sparse", "9 of 10 raw data blocks hold SBR data after a channel element"],
         ["aac.sbr-inconsistent", "but 9 of 10 raw data blocks hold SBR data; the first block that disagrees is raw data block 2"]],
        skip={**skip, "decode": "ffmpeg prints \"No quantized data read for sbr_dequant\" for the SBR payloads after the first block."}, rate=core)))
    # The first frame fails its CRC and holds no SBR data, and it is no evidence: the second block decides, so the stream is HE-AAC.
    blocks = [pair(i, M.Element(M.FIL, payload=M.sbr_payload(counts, SBR_HEADER if i == 1 else None, pair=True)) if i > 0 else None) for i in range(10)]
    data, rows = concat(adts_frames(blocks, rate=core, channels=2, protect=True, damage=[0]))
    out.append(("adts", "sbr-after-first-frame-failing-crc.aac", data, manifest(
        "Protected ADTS frames at 22050 Hz of ten channel pairs, the first with a wrong CRC and no SBR data and the others followed by an SBR payload: the "
        "first frame is no evidence, so the second block decides and the stream is HE-AAC at 44100 Hz.", data, rows,
        [stream("HE-AAC", 2 * core, 2, "Stereo", 10 * 2048, ffprobe_profile="LC")], {"ADTS/Frame/CRC/Check": "mismatch"},
        [["aac.crc-mismatch", "1 ADTS frame fails the CRC check"], ["aac.implicit-sbr", "9 of 9 raw data blocks hold SBR data: the stream is HE-AAC"]],
        skip={**skip, **ffmpeg_he, "ffprobe": "ffmpeg does not check ADTS CRCs, and its first block holds no SBR data.", "decode": "ffmpeg does not check ADTS CRCs, and its first block holds no SBR data."}, rate=core)))
    # SBR data after a coupling channel element: 4.5.2.8.2.2 puts an SBR element after each independently switched coupling channel element (id_aac ID_SCE,
    # Table 4.57 note 1) and 1.6.5.2 takes any payload for implicit signaling, so the stream is HE-AAC. fdk-aac excludes the element from those SBR follows, faad2 rejects
    # it, and only ffmpeg keeps it as the previous element (where a program config element gives it a channel).
    blocks = []
    for i in range(12):
        cpe = M.Element(M.CPE, ics=(channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=i, rate=core), channel(M.ONLY_LONG, 1, [[(9, 4)]], seed=i + 1, rate=core)), mask=0)
        cce = M.Element(M.CCE, tag=0, independent=1, targets=[(1, 0, 1, 1)], ics=channel(M.ONLY_LONG, 1, [[(11, 4), (3, 4)]], seed=i + 2, rate=core))
        fill = M.Element(M.FIL, payload=M.sbr_payload(counts, SBR_HEADER if i % 4 == 0 else None, pair=False))
        blocks.append(M.raw_data_block([cpe, cce, fill], rate_index=rate_index(core)))
    data, rows = concat(adts_frames(blocks, rate=core, channels=2))
    out.append(("adts", "sbr-after-coupling-channel.aac", data, manifest(
        "ADTS at 22050 Hz of a channel pair, a coupling channel element and a fill element of SBR data, twelve times, in a stream of channel "
        "configuration 2: the standard puts the SBR data of a coupling channel element after it (read as a single channel element's), so the stream is HE-AAC at 44100 Hz; "
        "fdk-aac and faad2 read no SBR after a coupling element.", data, rows,
        [stream("HE-AAC", 2 * core, 2, "Stereo", 12 * 2048, ffprobe_profile="LC")], {"ADTS/Statistics/SBR": share(12, 12, "access unit")},
        [["aac.implicit-sbr", "12 of 12 raw data blocks hold SBR data: the stream is HE-AAC"],
         ["aac.coupling-without-program", "12 raw data blocks hold a coupling channel element"]],
        skip={**skip, "ffprobe_streams": "ffmpeg cannot decode the frames (\"channel element 2.0 is not allocated\"), so it reports no sample rate.",
              "decode": "ffmpeg finds no channel for the coupling element in a stream of channel configuration 2.",
              "faad_trace": "faad2 is built without coupling and stops at the element."}, rate=core)))
    # A channel pair's SBR data (its fields) after a coupling channel element: 4.5.2.8.2.2 puts an SBR element after each independently switched coupling channel element (id_aac ID_SCE,
    # Table 4.57 note 1) and 1.6.5.2 takes any payload for implicit signaling, so the stream is HE-AAC. fdk-aac excludes the element from those SBR follows, faad2 rejects
    # it, and only ffmpeg keeps it as the previous element (where a program config element gives it a channel).
    blocks = []
    for i in range(12):
        cpe = M.Element(M.CPE, ics=(channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=i, rate=core), channel(M.ONLY_LONG, 1, [[(9, 4)]], seed=i + 1, rate=core)), mask=0)
        cce = M.Element(M.CCE, tag=0, independent=i % 2, targets=[(1, 0, 1, 1)], ics=channel(M.ONLY_LONG, 1, [[(11, 4), (3, 4)]], seed=i + 2, rate=core))
        fill = M.Element(M.FIL, payload=M.sbr_payload(counts, SBR_HEADER if i % 4 == 0 else None, pair=True))
        blocks.append(M.raw_data_block([cpe, cce, fill], rate_index=rate_index(core)))
    data, rows = concat(adts_frames(blocks, rate=core, channels=2))
    out.append(("adts", "sbr-after-coupling-element.aac", data, manifest(
        "ADTS at 22050 Hz of a channel pair, a coupling channel element and a fill element of SBR data, twelve times, in a stream of channel "
        "configuration 2, the coupling channel element switched independently in the odd blocks and dependently in the even ones and the data laid out as a channel pair's: "
        "the standard puts an SBR element after the independently switched element only, and reads it as a single channel element's (these bits happen to satisfy that reading); "
        "the payloads after the dependently switched one are reported and read by no one, and all count as evidence, so the stream is HE-AAC at 44100 Hz.", data, rows,
        [stream("HE-AAC", 2 * core, 2, "Stereo", 12 * 2048, ffprobe_profile="LC")], {"ADTS/Statistics/SBR": share(12, 12, "access unit")},
        [["aac.implicit-sbr", "12 of 12 raw data blocks hold SBR data: the stream is HE-AAC"],
         ["aac.sbr-without-channel-element", "6 raw data blocks hold SBR data that follows no single channel element, channel pair element or independently switched coupling channel element"],
         ["aac.coupling-without-program", "12 raw data blocks hold a coupling channel element"]],
        skip={**skip, "ffprobe_streams": "ffmpeg cannot decode the frames (\"channel element 2.0 is not allocated\"), so it reports no sample rate.",
              "decode": "ffmpeg finds no channel for the coupling element in a stream of channel configuration 2.",
              "faad_trace": "faad2 is built without coupling and stops at the element."}, rate=core)))
    # Every block has its SBR payload before the channel element, where no decoder reads it.
    blocks = []
    for i in range(12):
        ics = channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=i, rate=core)
        blocks.append(M.raw_data_block([M.Element(M.FIL, payload=M.sbr_payload(counts, SBR_HEADER if i % 4 == 0 else None, pair=False)),
                                        M.Element(M.SCE, ics=ics)], rate_index=rate_index(core)))
    data, rows = concat(adts_frames(blocks, rate=core, channels=1))
    out.append(("adts", "sbr-before-channel-element.aac", data, manifest(
        "ADTS at 22050 Hz of single channel elements, each preceded by a fill element of SBR data: the payloads follow no channel element, so they are reported and read by no "
        "one, but 1.6.5.2 item 1 takes any payload for implicit signaling: the stream is HE-AAC at 44100 Hz (decoders read none and decode AAC LC at 22050 Hz).", data, rows,
        [stream("HE-AAC", 2 * core, 1, "Mono", 12 * 2048)], {"ADTS/Statistics/SBR": share(12, 12, "access unit")},
        [["aac.implicit-sbr", "12 of 12 raw data blocks hold SBR data: the stream is HE-AAC"], ["aac.sbr-without-channel-element", "12 raw data blocks hold SBR data that follows no single channel element, channel pair element or independently switched coupling channel element"]],
        skip={**skip, "ffprobe_streams": "ffmpeg cannot decode the frames (\"SBR was found before the first channel element\"), so it reports no sample rate.",
              "decode": "ffmpeg reports that SBR was found before the first channel element, the condition the fixture tests.",
              "faad_trace": "faad2 does not read the stream: its SBR payload follows no channel element, and its trace holds one raw data block."}, rate=core)))
    # The last frame is cut short by the end of the data and holds two blocks with SBR data.
    plain = pair(0)
    sbr_blocks = [pair(i, M.Element(M.FIL, payload=M.sbr_payload(counts, SBR_HEADER if i == 1 else None, pair=True)))[0] for i in (1, 2)]
    frames = [adts_header(7 + len(plain[0]), core, 2, 1) + plain[0] for _ in range(2)]
    frames.append(adts_header(7 + sum(len(b) for b in sbr_blocks) + 5, core, 2, 1, blocks=2) + b"".join(sbr_blocks))
    data, rows = concat(frames)
    out.append(("adts", "sbr-in-truncated-frame.aac", data, manifest(
        "ADTS at 22050 Hz of two channel pair frames and a third that holds two blocks, each a channel pair followed by SBR data, and ends 5 bytes "
        "before its stated length: a damaged frame, so its SBR data counts for nothing and the stream stays AAC LC.",
        data, rows, [stream("AAC LC", core, 2, "Stereo", 4 * 1024, lost_samples=2 * 1024)], {},
        [["aac.frame-truncated", "has"]],
        skip={**skip, "ffprobe": "ffmpeg drops the cut frame.", "decode": "ffmpeg drops the cut frame.",
              "faad_trace": "faad2 reads one raw data block per ADTS frame and does not read the cut frame of two blocks."}, rate=core)))
    # A stream cut before its first SBR header (FAAC sends one every 30 access units): the payloads before it cannot be read, but decoders
    # take the stream for HE-AAC from the first.
    cut = [pair(i, M.Element(M.FIL, payload=M.sbr_payload(counts, SBR_HEADER if i == 5 else None, pair=True))) for i in range(12)]
    data, rows = concat(adts_frames(cut, rate=core, channels=2))
    out.append(("adts", "sbr-cut-before-header.aac", data, manifest(
        "ADTS at 22050 Hz of twelve channel pairs followed by SBR data, the SBR header only in the sixth, as in a stream cut before its first header: "
        "the five payloads before it cannot be read, but decoders take the stream for HE-AAC from the first, so it is HE-AAC at 44100 Hz.",
        data, rows, [stream("HE-AAC", 44100, 2, "Stereo", 12 * 2048, ffprobe_profile="LC")],
        {"ADTS/Statistics/SBR": share(12, 12, "access unit"), "ADTS/Statistics/SBR/SBR Headers": "1 header"},
        [["aac.implicit-sbr", "12 of 12 raw data blocks hold SBR data: the stream is HE-AAC"], ["aac.sbr-before-header", "5 raw data blocks hold SBR data before the first SBR header"]],
        skip={"faad": "faad -i prints twice the rate of any ADTS stream at 24 kHz or less, whatever its frames hold."}, rate=core)))
    # Every frame fails its CRC.
    blocks = [pair(i, M.Element(M.FIL, payload=M.sbr_payload(counts, SBR_HEADER if i % 4 == 0 else None, pair=True))) for i in range(12)]
    data, rows = concat(adts_frames(blocks, rate=core, channels=2, protect=True, damage=range(12)))
    out.append(("adts", "sbr-in-frames-failing-crc.aac", data, manifest(
        "Protected ADTS frames at 22050 Hz of twelve channel pairs followed by SBR data, every frame with a wrong CRC: their SBR data counts for "
        "nothing and the stream stays AAC LC.", data, rows, [stream("AAC LC", core, 2, "Stereo", 12 * 1024)],
        {"ADTS/Frame/CRC/Check": "mismatch"}, [["aac.crc-mismatch", "12 ADTS frames fail the CRC check"]],
        skip={**skip, "ffprobe": "ffmpeg does not check ADTS CRCs, and takes the SBR data of the first frame.",
              "decode": "ffmpeg does not check ADTS CRCs."}, rate=core)))
    return out

def blocks_missing():
    """Unprotected ADTS frames hold their raw data blocks one after another, so a frame that ends early holds fewer than its header declares."""
    from make_mp4_fixtures import adts_header
    block = pair_block(0, first=channel(M.ONLY_LONG, 1, [[(11, 4)]]), second=channel(M.ONLY_LONG, 1, [[(9, 4)]]))[0][0]
    data, rows = concat([adts_header(7 + len(block), RATE, 2, 1, blocks=4) + block for _ in range(10)])
    out = [("adts", "blocks-missing.aac", data, manifest(
        "Ten unprotected ADTS frames whose headers declare four raw data blocks each (number_of_raw_data_blocks_in_frame 3), each holding one "
        "channel pair block: the samples are counted for the block it holds.", data, rows, [stream("AAC LC", RATE, 2, "Stereo", 10 * 1024)],
        {"ADTS/Statistics/Raw Data Blocks": "10 blocks"},
        [["aac.blocks-missing", "10 ADTS frames hold fewer raw data blocks than their headers declare"]],
        skip={"ffprobe": "ffmpeg's parser counts the declared blocks and decodes one.", "faad": "faad2 reads one raw data block per ADTS frame.",
              "faad_trace": "faad2 reads one raw data block per ADTS frame.", "decode": "ffmpeg decodes one block of a frame that declares four."}))]
    data, rows = concat([adts_header(7 + 2 * len(block) + 3, RATE, 2, 1, blocks=2) + block + block + bytes([0x11, 0x22, 0x33]) for _ in range(10)])
    out.append(("adts", "blocks-trailing-bytes.aac", data, manifest(
        "Ten unprotected ADTS frames of two channel pair blocks each, then three bytes that belong to no block.", data, rows, [stream("AAC LC", RATE, 2, "Stereo", 10 * 2048)],
        {"ADTS/Statistics/Raw Data Blocks": "20 blocks"},
        [["aac.block-trailing-bytes", "10 raw data blocks hold bytes after their END element"]],
        skip={"ffprobe": "ffmpeg decodes the bytes after the END element as a further block.", "faad": "faad2 reads one raw data block per ADTS frame.",
              "faad_trace": "faad2 reads one raw data block per ADTS frame.", "decode": "ffmpeg decodes the bytes after the END element as a further block, which fails."})))
    return out


def prediction_bands():
    """AAC Main with max_sfb 40 at 44100 Hz (PRED_SFB_MAX 40): prediction_used has a flag for each of the 40 bands, which the node lists in full."""
    blocks = []
    flags = [[1] * 40, [0] * 32 + [1] * 8, [1, 0] * 20, [0] * 39 + [1]]
    for i, used in enumerate(flags):
        ics = channel(M.ONLY_LONG, 1, [[(1, 40)]], seed=i, predictor=(False, 0, used))
        blocks.append(M.raw_data_block([M.Element(M.SCE, ics=ics)], object_type=1))
    data, rows = concat(adts_frames(blocks, profile=0, channels=1))
    c = "ADTS/Frame{}/Raw Data Block/SCE/Channel/ICS Info/Prediction Used"
    return [("adts", "main-prediction-bands.aac", data, manifest(
        "AAC Main (ADTS profile 0), mono at 44100 Hz, max_sfb 40 in every frame with prediction used in all 40 bands, in bands 32 to 39, in every other "
        "band, and in band 39 only.", data, rows, [stream("AAC Main", RATE, 1, "Mono", 4 * 1024)],
        {c.format(""): "0-39", c.format("[1]"): "32-39", c.format("[2]"): band_text([[bool(b) for b in flags[2]]]), c.format("[3]"): "39"}))]


def rate_fixtures():
    """Sample rates that cannot be right: an explicit 1 Hz in an AudioSpecificConfig, and a reserved usacSamplingFrequencyIndex."""
    asc_path = "MP4/moov/trak/mdia/minf/stbl/stsd/mp4a/esds/ES_Descriptor/DecoderConfigDescriptor/DecoderSpecificInfo/AudioSpecificConfig"
    # A channel pair with max_sfb 0 per access unit: a valid block that holds no spectral data, whatever band tables the rate gives.
    silent = pair_block(0, first=channel(M.ONLY_LONG, 1, None), second=channel(M.ONLY_LONG, 1, None))[0][0]
    samples = [silent] * 3
    entry = stream("AAC LC", 1, 2, "Stereo", 3 * 1024, vbr=True)
    out = [("mp4",) + mp4_only(
        "rate-1-hz", "AAC LC whose AudioSpecificConfig states an explicit sampling frequency of 1 Hz, over three silent access units: every access unit lasts "
        "1024 seconds.", asc(2, 1, 2), 1000, 2, samples, [23] * 3, [entry], {asc_path + "/Sample Rate": "1 Hz"},
        [["aac.implausible-sample-rate", "The explicit sampling frequency is 1 Hz"]],
        {"ffprobe": False, "mp4box": True, "faad": None,
         "skip": {"ffprobe": "ffmpeg rejects the explicit sampling frequency (\"invalid sampling rate index 15\").", "faad": "faad2 rejects a sample rate of 1 Hz.",
                  "faad_trace": "faad2 reads no raw data block of a stream of 1 Hz."}}, chunk=3, timescale=1000)]
    bits = Bits()
    bits.put(31, 5).put(42 - 32, 6).put(3, 4).put(1, 4)  # audio object type 42, sampling frequency index 3 (48 kHz), channel configuration 1
    bits.put(13, 5).put(1, 3).put(1, 5)  # usacSamplingFrequencyIndex 13, coreSbrFrameLengthIndex 1, channelConfigurationIndex 1
    bits.put(0, 4).put(0, 2).put(0, 1).put(0, 1)  # one element: SCE, tw_mdct 0, noiseFilling 0
    bits.put(0, 1)  # usacConfigExtensionPresent
    config = bits.align().bytes()
    out.append(("mp4",) + mp4_only(
        "usac-reserved-rate", "xHE-AAC whose UsacConfig states usacSamplingFrequencyIndex 13, which ISO/IEC 23003-3 reserves: the sample rate is unknown.",
        config, 48000, 1, [bytes([0x80, 0, 0, 0])] * 10, [1024] * 10,
        [{**stream("xHE-AAC (USAC)", 0, 1, "Mono", 10 * 1024, vbr=True)}],
        {asc_path + "/UsacConfig/Sample Rate": "13 (reserved)"},
        [["aac.reserved-sample-rate", "usacSamplingFrequencyIndex 13 is reserved"], ["aac.usac-frames-not-analyzed", "usacIndependencyFlag"]],
        {"ffprobe": False, "mp4box": True, "mp4box_samples": True, "faad": None,
         "skip": {"ffprobe": "ffmpeg 6.1 does not implement audio object type 42 and lists no stream; MP4Box lists the samples instead.",
                  "faad": "faad2 2.11 does not read USAC configurations (it fails to open the file)."}}))
    return out


# --- Where the decoder follows the standard (ISO/IEC 14496-3) and not libxaac's order of the tools -------------------------------------

def stereo_tool_order():
    """Channel pairs of a common window that exercise the order of the tools of Figure 4.2 (4.1.1.2) and 4.6.8, each between silent pairs so that the block's output depends on
    its own spectra only, and set against a block the standard makes equal: intensity bands under ms_mask_present 2 and under mask 1 with flags on the other bands only (4.6.8.2.3
    inverts under mask 1 alone); mid/side flags over bands where only the first channel codes noise and the same pair without a mask (no mid/side where a channel codes noise,
    4.6.8.1.3); a first channel of noise and a second of intensity stereo (position 0), whose output equals the first channel's (the second channel takes the noise), and with
    the flags set under mask 1, whose output is the inverted first channel; and a pair that codes noise in both channels and in one."""
    loud = 150
    quiet = M.Element
    silent = lambda: pair_block(0, first=channel(M.ONLY_LONG, 1, [[(0, 1)]], gain=loud), second=channel(M.ONLY_LONG, 1, [[(0, 1)]], gain=loud))
    one = [(11, 4), (5, 8), (1, 18)]
    two = [(9, 4), (3, 8), (15, 6), (14, 4), (15, 8)]
    first_noise = [[(11, 4), (13, 6), (5, 8), (13, 4), (1, 4)]]
    second_huffman = [[(9, 4), (3, 6), (7, 8), (2, 4), (4, 4)]]
    all_noise = [[(13, 30)]]
    all_intensity = [[(15, 30)]]
    zero_positions = {(0, b): 0 for b in range(30)}
    noisy = [[(11, 4), (13, 6), (5, 8), (13, 4), (1, 6), (7, 4), (0, 4)]]
    other = [[(9, 4), (3, 6), (7, 8), (13, 4), (13, 6), (2, 4), (4, 4)]]
    del quiet
    made = [
        silent(),
        pair_block(2, first=channel(M.ONLY_LONG, 1, [one], gain=loud, seed=71), second=channel(M.ONLY_LONG, 1, [two], gain=loud, seed=72)),
        silent(),
        pair_block(1, [[1] * 12 + [0] * 18], first=channel(M.ONLY_LONG, 1, [one], gain=loud, seed=71), second=channel(M.ONLY_LONG, 1, [two], gain=loud, seed=72)),
        silent(),
        pair_block(1, [[1 if 4 <= b < 10 or 18 <= b < 22 else 0 for b in range(26)]], first=channel(M.ONLY_LONG, 1, first_noise, gain=loud, seed=73), second=channel(M.ONLY_LONG, 1, second_huffman, gain=loud, seed=74)),
        silent(),
        pair_block(0, first=channel(M.ONLY_LONG, 1, first_noise, gain=loud, seed=73), second=channel(M.ONLY_LONG, 1, second_huffman, gain=loud, seed=74)),
        silent(),
        pair_block(0, first=channel(M.ONLY_LONG, 1, all_noise, gain=loud, seed=75), second=channel(M.ONLY_LONG, 1, all_intensity, gain=loud, seed=76, gains=dict(zero_positions))),
        silent(),
        pair_block(1, [[1] * 30], first=channel(M.ONLY_LONG, 1, all_noise, gain=loud, seed=77), second=channel(M.ONLY_LONG, 1, all_intensity, gain=loud, seed=78, gains=dict(zero_positions))),
        silent(),
        pair_block(1, [[1 if b < 32 else 0 for b in range(36)]], first=channel(M.ONLY_LONG, 1, noisy, gain=loud, seed=79), second=channel(M.ONLY_LONG, 1, other, gain=loud, seed=80)),
        silent(),
    ]
    data, rows = concat(adts_frames([m[0] for m in made], channels=2))
    streams = [t for m in made for t in m[1]]
    fields = {
        "ADTS/Frame[1]/Raw Data Block/CPE/Mid/Side Mask Present": "2 (all bands)",
        "ADTS/Frame[3]/Raw Data Block/CPE/Mid/Side Mask Present": "1 (per band)",
        "ADTS/Statistics/Channel Pairs/Intensity Stereo": share(4, 15, "element"),
        **statistics("ADTS", streams),
    }
    return "stereo-tool-order.aac", data, manifest(
        "Channel pairs of a common window at a level near -30 dBFS, each between silent pairs: intensity bands under ms_mask_present 2 and the same pair under mask 1 with flags on "
        "the other bands (equal output); mid/side flags on the bands where only the first channel codes noise and the same pair without a mask (equal second channel); a first "
        "channel of noise and a second of intensity stereo, without a mask (the second channel equals the first) and with the flags under mask 1 (the inverted first channel); and "
        "a pair with noise in both channels and in one.",
        data, rows, [stream("AAC LC", RATE, 2, "Stereo", 15 * 1024)], fields,
        skip={"decode": "ffmpeg's decoder inverts intensity bands under ms_mask_present 2 and draws its own noise.",
              "faad_trace": "faad2 checks the first channel's noise only for mid/side stereo, and the units' stereo codings may differ."})


def tns_order_above_12():
    """Mono blocks with a TNS filter of order 12, 12, 13, 20 and 31 (the 5-bit field's largest) of long windows: the standard takes the first 12 coefficients (4.6.9.3, Table 4.156)."""
    sections = [[(9, 12), (5, 10), (7, 10), (3, 8), (0, 4)]]
    blocks = []
    ics = []
    for order in (12, 12, 13, 20, 31):
        coefficients = [1 if i % 3 else 2 for i in range(order)]
        tns = [[(30, order, 0, 0, 1, coefficients)]]
        channel_ics = channel(M.ONLY_LONG, 1, sections, gain=150, tns=tns, seed=80)
        ics.append(channel_ics)
        blocks.append(M.raw_data_block([M.Element(M.SCE, ics=channel_ics)]))
    data, rows = concat(adts_frames(blocks))
    return "tns-order-above-12.aac", data, manifest(
        "Mono frames of long windows with the same spectrum and a TNS filter of order 12 (twice) and 13, 20 and 31 over 30 bands, whose first 12 coefficients are the same: the standard "
        "limits the order to 12 and decodes the first 12 coefficients, so the last four blocks decode alike; the field allows more, so the stream is reported.", data, rows, [stream("AAC LC", RATE, 1, "Mono", 5 * 1024)], statistics("ADTS", ics),
        [["aac.tns-order-too-high", "a TNS filter of a higher order than the 12 its window allows"]],
        skip={"decode": "ffmpeg refuses a TNS filter of an order above 12 (\"TNS filter order ... is greater than maximum\").",
              "faad_trace": "faad2 does not trace the filter's order."})


def first_block_kbd():
    """Two mono blocks of a Kaiser-Bessel derived window with one spectral line each: the first block's left half has its own shape (4.6.11.3.2), so its first 1,024 samples are
    the line's inverse transform times the Kaiser-Bessel derived rise."""
    sections = [[(1, 1)]]
    ics = [channel(M.ONLY_LONG, 1, sections, gain=140, values={(0, 0): [0, 0, 1, 0]}, seed=90 + i) for i in range(2)]
    data, rows = concat(adts_frames([M.raw_data_block([M.Element(M.SCE, ics=i)]) for i in ics]))
    return "first-block-kbd.aac", data, manifest(
        "Two mono frames of the Kaiser-Bessel derived window with the single spectral line 2 of the lowest band: the first block of a stream has the same shape on both halves.",
        data, rows, [stream("AAC LC", RATE, 1, "Mono", 2 * 1024)], statistics("ADTS", ics))


def fill_with_sbr_and_other_payload():
    """A fill element holding a dynamic range payload and then an SBR payload: 4.5.2.1.5.2 allows no other payload next to SBR data."""
    core = 22050
    counts = M.sbr_bands(SBR_HEADER, 2 * core)
    blocks = []
    for i in range(6):
        payload = M.drc_payload() + M.sbr_payload(counts, SBR_HEADER if i == 0 else None, pair=True)
        blocks.append(pair_block(0, rate=core, extra=[M.Element(M.FIL, payload=payload)], first=channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=i, rate=core),
                                 second=channel(M.ONLY_LONG, 1, [[(9, 4)]], seed=i + 1, rate=core))[0])
    data, rows = concat(adts_frames(blocks, rate=core, channels=2))
    return "fill-sbr-with-other-payload.aac", data, manifest(
        "ADTS at 22050 Hz of six channel pairs, each followed by a fill element of a dynamic range payload and then SBR data: the fill element holds another payload next to the "
        "SBR payload, which the standard does not allow.", data, rows, [stream("HE-AAC", 2 * core, 2, "Stereo", 6 * 2048, ffprobe_profile="LC")],
        {"ADTS/Statistics/SBR": share(6, 6, "access unit")},
        [["aac.fill-sbr-with-other-payload", "6 raw data blocks hold an SBR payload in a fill element with an extension payload of another type"],
         ["aac.implicit-sbr", "6 of 6 raw data blocks hold SBR data: the stream is HE-AAC"]],
        skip={"faad": "faad -i prints twice the rate of any ADTS stream at 24 kHz or less, whatever its frames hold.",
              "decode": "ffmpeg reads the dynamic range payload and the SBR data in one fill element differently.",
              "faad_trace": "faad2 reads no SBR data from a fill element whose first extension payload is another type (4.5.2.1.5.2 allows none next to SBR data), as its trace shows."}, rate=core)


def sbr_crc_checked():
    """Implicit SBR with EXT_SBR_DATA_CRC payloads, two of six with a wrong CRC."""
    core = 22050
    counts = M.sbr_bands(SBR_HEADER, 2 * core)
    blocks = []
    for i in range(6):
        payload = M.sbr_payload(counts, SBR_HEADER if i == 0 else None, pair=True, crc="wrong" if i in (2, 4) else True)
        blocks.append(pair_block(0, rate=core, extra=[M.Element(M.FIL, payload=payload)], first=channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=i, rate=core),
                                 second=channel(M.ONLY_LONG, 1, [[(9, 4)]], seed=i + 1, rate=core))[0])
    data, rows = concat(adts_frames(blocks, rate=core, channels=2))
    return "sbr-crc-checked.aac", data, manifest(
        "ADTS at 22050 Hz of six channel pairs followed by SBR payloads with a CRC-10 (EXT_SBR_DATA_CRC), the third and fifth with a wrong one: HE-AAC at 44100 Hz, four "
        "checksums verified and two failed.", data, rows, [stream("HE-AAC", 2 * core, 2, "Stereo", 6 * 2048, ffprobe_profile="LC")],
        {"ADTS/Statistics/SBR": share(6, 6, "access unit"), "ADTS/Statistics/SBR/Verified SBR Checksums": "4 payloads", "ADTS/Statistics/SBR/Failed SBR Checksums": "2 payloads"},
        [["aac.sbr-crc-mismatch", "2 raw data blocks hold an SBR payload whose CRC differs"], ["aac.implicit-sbr", "6 of 6 raw data blocks hold SBR data: the stream is HE-AAC"]],
        skip={"faad": "faad -i prints twice the rate of any ADTS stream at 24 kHz or less, whatever its frames hold.",
              "decode": "ffmpeg does not check the CRC and prints \"No quantized data read for sbr_dequant\" for the payloads after the first."}, rate=core)


def sbr_too_many_patches():
    """An SBR header at 44100 Hz (core 22050 Hz) whose frequency tables need six patches, where 4.6.18.3.6 allows five."""
    core = 22050
    header = {"amp_res": 1, "start": 0, "stop": 8, "xover": 0, "freq_scale": 0, "alter_scale": 0}
    counts = M.sbr_bands(header, 2 * core)
    blocks = []
    for i in range(6):
        payload = M.sbr_payload(counts, header if i == 0 else None, pair=True)
        blocks.append(pair_block(0, rate=core, extra=[M.Element(M.FIL, payload=payload)], first=channel(M.ONLY_LONG, 1, [[(11, 4)]], seed=i, rate=core),
                                 second=channel(M.ONLY_LONG, 1, [[(9, 4)]], seed=i + 1, rate=core))[0])
    data, rows = concat(adts_frames(blocks, rate=core, channels=2))
    return "sbr-too-many-patches.aac", data, manifest(
        "ADTS at 22050 Hz of six channel pairs followed by SBR payloads whose header (start frequency 0, stop frequency 8, linear scale) needs six patches at 44100 Hz, where the "
        "standard allows five: the payloads are read and decoded with the six patches Figure 4.48 gives, and the stream is HE-AAC from the payloads' presence.", data, rows,
        [stream("HE-AAC", 2 * core, 2, "Stereo", 6 * 2048, ffprobe_profile="LC")], {"ADTS/Statistics/SBR": share(6, 6, "access unit")},
        [["aac.sbr-patches-over-limit", "frequency tables need 6 patches, more than the 5 that 4.6.18.3.6 allows"], ["aac.implicit-sbr", "6 of 6 raw data blocks hold SBR data: the stream is HE-AAC"]],
        skip={"faad": "faad -i prints twice the rate of any ADTS stream at 24 kHz or less, whatever its frames hold.",
              "decode": "ffmpeg is held to the output by crosscheck_he_aac_pcm.py, which compares its patches of six with the decoder's."}, rate=core)


def lfe_restrictions():
    """A single channel element and an LFE element from a program config element, the LFE channel stream first within the restrictions of 4.5.2.1.3.1 and then with a Kaiser-Bessel
    derived window, coefficients above the lowest 12 and TNS."""
    def pce(w):
        w.put(0, 4).put(1, 2).put(rate_index(RATE), 4)
        w.put(1, 4).put(0, 4).put(0, 4).put(1, 2).put(0, 3).put(0, 4)
        w.put(0, 1).put(0, 1).put(0, 1)
        w.put(0, 1).put(0, 4)  # front single channel element 0
        w.put(0, 4)            # LFE element 0
        w.align()
        w.put(0, 8)

    blocks = []
    lfes = []
    for i in range(4):
        sce = channel(M.ONLY_LONG, 1, [[(11, 4), (3, 10)]], gain=140, seed=i)
        if i < 2:
            lfe = channel(M.ONLY_LONG, 0, [[(11, 3)]], gain=140, seed=i + 10)
        else:
            lfe = channel(M.ONLY_LONG, 1, [[(11, 6), (5, 4)]], gain=140, seed=i + 10, tns=[[(8, 2, 0, 0, 1, [1, 2])]])
        lfes.append(lfe)
        blocks.append(M.raw_data_block([M.Element(M.PCE, write=pce), M.Element(M.SCE, ics=sce), M.Element(M.LFE, ics=lfe)]))
    data, rows = concat(adts_frames(blocks, channels=0))
    return "lfe-restrictions-pce.aac", data, manifest(
        "ADTS of channel configuration 0 with a program config element of a front single channel element and an LFE element: the LFE of the first two blocks keeps to its "
        "restrictions, that of the last two has a Kaiser-Bessel derived window, coefficients above the lowest 12 and TNS.", data, rows,
        [stream("AAC LC", RATE, 2, "2 channels", 4 * 1024, layout=["front center", "LFE"])], {},
        [["aac.lfe-restrictions", "2 raw data blocks hold an LFE channel stream with a Kaiser-Bessel derived window"]],
        skip={"decode": "ffmpeg's decoder differs on the filter bank of the LFE."})
def elements_order_changes():
    """Channel configuration 3 (a single channel element and a channel pair element) with the two elements in the other order in every second block: the counts are what the configuration
    defines, the order is not the first block's, and a decoder's per-element state no longer fits the block."""
    blocks = []
    for i in range(6):
        sce = M.Element(M.SCE, ics=channel(M.ONLY_LONG, 1, [[(11, 4), (3, 10)]], gain=140, seed=i))
        cpe = M.Element(M.CPE, ics=(channel(M.ONLY_LONG, 1, [[(9, 4), (3, 10)]], gain=140, seed=i + 20), channel(M.ONLY_LONG, 1, [[(9, 4), (1, 10)]], gain=140, seed=i + 40)), common=True, mask=0)
        blocks.append(M.raw_data_block([cpe, sce] if i % 2 else [sce, cpe]))
    data, rows = concat(adts_frames(blocks, channels=3))
    return "elements-order-changes.aac", data, manifest(
        "ADTS of channel configuration 3 whose blocks alternate between a single channel element and a channel pair element and the two in the other order: the counts are the configuration's, "
        "the order of every block but the first is not the block before's, so each is withheld.", data, rows,
        [stream("AAC LC", RATE, 3, "3.0", 6 * 1024, layout=["front center", "front left", "front right"])], {},
        [["aac.layout-changed", "5 raw data blocks hold channel elements in another order than the block before"]],
        skip={"decode": "ffmpeg maps the elements by their order in each block.", "faad_trace": "faad2 reads the elements by their order."})


def fixtures():
    """(directory, name, data, manifest) of every fixture: "adts" or "mp4"."""
    out = [("adts",) + f for f in [windows(), window_sequences_invalid(), stereo(), stereo_coding(), stereo_coding_reserved(), stereo_coding_elements(), intensity_noise(), implicit_sbr_adts(), ps_phase_adts(), ps_phase_then_none_adts(), sbr_fixvar_adts(), fill_and_data()] + loud_tools() + [elements_order_changes(), stereo_tool_order(), tns_order_above_12(), first_block_kbd(), fill_with_sbr_and_other_payload(), sbr_crc_checked(), sbr_too_many_patches(), lfe_restrictions()] + faac_style_fixtures() + crc_fixtures() + object_types() + corrupt()]
    out += [("mp4",) + f for f in mp4_fixtures()]
    out += eld_ld_sbr() + parametric_stereo_cores() + signature_streams() + streams_limited() + sbr_evidence() + blocks_missing() + prediction_bands() + rate_fixtures()
    return out
