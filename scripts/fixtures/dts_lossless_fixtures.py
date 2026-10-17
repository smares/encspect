"""The lossless (XLL), DTS:X and .dtshd fixtures of make_dts_fixtures.py, built with dts_model.py.

XLL (ETSI TS 102 114 clause 8): units of a core frame and an extension substream whose asset has a core and a lossless component, with the channel
set sub-headers of Table 8-4, the navigation index of 8.4.2 and silent band data, so that FFmpeg's decoder (dca_xll.c) plays silence and names the
profile; and the damage and the reserved values the clause allows to build. DTS:X: the word FFmpeg's dca_xll.c and MediaInfo's File_Dts.cpp look for
after the band data. DTS-HD files: the chunks of ATSC A/103:2014 Annex E around the same units.
"""

import dts_model as m

STEREO = ["front left", "front right"]
SURROUND_5_1 = ["front center", "front left", "front right", "back left", "back right", "LFE"]
SEVEN_ONE = ["front center", "front left", "front right", "LFE", "back left", "back right", "side left", "side right"]

# Table 6-22's bits (one for each speaker) as the names the analyzer reports.
SPEAKER_BITS = ["front center", "front left", "front right", "surround left", "surround right", "LFE", "back center", "back left", "back right", "side left", "side right",
                "front left of center", "front right of center", "top front left", "top front center", "top front right", "LFE 2", "front left wide", "front right wide",
                "top center", "top side left", "top side right", "top back center", "top back left", "top back right"]


def layout_of(mask):
    """The analyzer's names for a mask of Table 6-22: the surround pair is the back pair unless a rear pair (bit 7) exists."""
    rear = bool(mask & (1 << 7))
    names = []
    for bit, name in enumerate(SPEAKER_BITS):
        if mask & (1 << bit):
            if name == "surround left":
                name = "side left" if rear else "back left"
            elif name == "surround right":
                name = "side right" if rear else "back right"
            names.append(name)
    return names


def cc_lossless(cc, **extra):
    reason = "ffmpeg's lossless decoder sets the bit rate to 0 (dca_xll.c: avctx->bit_rate = 0), so ffprobe lists none"
    skip = {"ffprobe_bitrate": reason, **extra.pop("skip", {})}
    # The hand-built core frames are stereo, whatever the lossless layer holds, and libdca reads the core only.
    return cc(ffprobe_bitrate=False, skip=skip, **{"libdca_channels": 2, **extra})


def undecodable(cc, message, reason, **extra):
    """The crosscheck of a lossless extension that ffmpeg's decoder refuses (the message it logs, at the warning level for the features it does not implement):
    it decodes no frame, so it names no lossless profile and ffprobe names the core's."""
    skip = {"ffprobe_profile": "ffmpeg names the lossless profile when its decoder has decoded a frame (dca_xll.c), which it does not here, so ffprobe names the core's", "decode": reason, **extra.pop("skip", {})}
    return cc_lossless(cc, ffprobe_profile=False, decode=False, decode_error=message, decode_log="warning", skip=skip, **extra)


def build(api):
    """api: the helpers of make_dts_fixtures (fixture, rows, cc, stream)."""
    fixture, rows, cc, stream = api["fixture"], api["rows"], api["cc"], api["stream"]
    name = "DTS-HD Master Audio"

    def unit(asset, **options):
        return m.core_frame(size=512) + m.exss([dict(asset)], **options)

    def lossless(file_name, description, asset, expected, findings=(), fields=None, count=4, crosscheck=None, sets=None, xll=None, name_=name, size=None, **options):
        sizes = {"core": 96, "xll": size or 96}
        a = dict(components=["core", "xll"], sizes=sizes, **asset)
        if sets is not None or xll:
            a["component_options"] = {"xll": {**({"sets": sets} if sets is not None else {}), **(xll or {})}}
        parts = [unit(a, **options) for _ in range(count)]
        layout, rate = expected[0], expected[1]
        samples = expected[2] if len(expected) > 2 else count * 512
        fixture(file_name, ".dts", description, b"".join(parts), rows(parts), [stream(name_, rate, layout, expected[3] if len(expected) > 3 else "2/0", samples, vbr=True)],
                list(findings), fields=fields, crosscheck=crosscheck if crosscheck is not None else cc_lossless(cc))
        return parts

    def stats(channels, sets, rate, bits, width):
        return {"DTS/Statistics/Lossless Channel Sets": f"{sets} channel set" + ("s" if sets != 1 else ""), "DTS/Statistics/Lossless Channels": f"{channels} channels",
                "DTS/Statistics/Lossless Sampling Frequency": f"{rate} Hz", "DTS/Statistics/Lossless Source PCM Resolution": f"{bits} bits",
                "DTS/Statistics/Lossless Storage Bit Width": f"{width} bits"}

    # The layers: 2.0, 5.1 and 7.1 at 48, 96 and 192 kHz, 24 bits; 16 and 20 bits at 48 kHz.
    stereo = dict(channels=2, mask=0x02)
    five = dict(channels=6, mask=0x0F)
    for rate in (48000, 96000, 192000):
        label = f"{rate // 1000}k"
        wide = dict(max_rate=rate) if rate != 48000 else {}
        size = {48000: 96, 96000: 96, 192000: 128}[rate]
        samples = 4 * 512 * rate // 48000
        extension = {"Extension Sampling Frequency": f"{rate} Hz"} if rate != 48000 else {}
        fields = {**stats(2, 1, rate, 24, 24), **{f"DTS/Statistics/{k}": v for k, v in extension.items()}}
        lossless(f"xll-2-0-24bit-{label}", f"Four units of a stereo core frame and a lossless extension of one stereo channel set, 24 bits at {rate} Hz.", {**stereo, **wide},
                 (STEREO, rate, samples), fields=fields, size=size)
        lossless(f"xll-5-1-24bit-{label}", f"Four units of a stereo core frame and a lossless extension of one 5.1 channel set, 24 bits at {rate} Hz.", {**five, **wide},
                 (SURROUND_5_1, rate, samples, "2/0"), fields={**stats(6, 1, rate, 24, 24), **{f"DTS/Statistics/{k}": v for k, v in extension.items()}}, size=size + 16)

    # 7.1 as Blu-ray discs lay it out, which the DTS-HD Master Audio 7.1 sample of the FFmpeg archive shows: a primary 5.1 set of the side surround pair with the downmix coefficients
    # to stereo, and a set of the rear pair that mixes into it.
    def seven_one(rate):
        return [m.xll_set(6, 0x627, rate=rate, dmix=dict(type=1, embedded=True)), m.xll_set(2, 0x180, rate=rate, primary=False, dmix=dict(embedded=True))]

    for rate in (48000, 96000):
        label = f"{rate // 1000}k"
        samples = 4 * 512 * rate // 48000
        wide = dict(max_rate=rate) if rate != 48000 else {}
        lossless(f"xll-7-1-24bit-{label}", f"Four units of a stereo core frame and a lossless extension of two channel sets, a primary 5.1 set with downmix coefficients and a set of two more channels, 24 bits at {rate} Hz.",
                 dict(channels=8, mask=0x84B, **wide), (SEVEN_ONE, rate, samples, "2/0"), sets=seven_one(rate), size=192,
                 fields={**stats(8, 2, rate, 24, 24), "DTS/Frame[0]/Extension Substream/Asset 1 Data/XLL/Channel Set 1/Downmix Coefficients": "12 coefficients",
                         "DTS/Frame[0]/Extension Substream/Asset 1 Data/XLL/Channel Set 2/Downmix Coefficients": "18 coefficients"})
    # A rejected set still counts in the hierarchy: the second set's downmix coefficients are counted from the first set's 6 channels (FFmpeg's hier_ofs).
    rejected = seven_one(48000)
    rejected[0] = m.xll_set(6, 0x627, bit_res=24, storage=16, dmix=dict(type=1, embedded=True))
    lossless("xll-7-1-hierarchy-after-rejected-set", "Four units of a lossless extension of two channel sets, the primary 5.1 set with a resolution of 24 bits stored in 16, so it is rejected, and a set of two more channels.",
             dict(channels=8, mask=0x84B), (SEVEN_ONE, 48000), sets=rejected, size=192, findings=[("dts.lossless-inconsistent", "source resolution of 24 bits is more than its storage width of 16 bits")],
             fields={"DTS/Frame[0]/Extension Substream/Asset 1 Data/XLL/Channel Set 2/Downmix Coefficients": "18 coefficients", "DTS/Frame[0]/Extension Substream/Asset 1 Data/XLL/Channel Set 2/Channel Mask": "0x180"},
             crosscheck=undecodable(cc, "Invalid PCM bit resolution for XLL channel set", "ffmpeg's decoder refuses a resolution beyond the width (dca_xll.c: 'Invalid PCM bit resolution for XLL channel set')",
                                  ffprobe_channels=2, skip={"ffprobe_channels": "ffmpeg cannot decode the lossless layer, so it reports the core's two channels and their layout"}))
    lossless("xll-2-0-16bit-48k", "Four units of a stereo core frame and a lossless extension of a stereo set of 16 bits stored in 16.", dict(channels=2, mask=0x02, bit_resolution=16),
             (STEREO, 48000), sets=[m.xll_set(2, bit_res=16, storage=16)], fields=stats(2, 1, 48000, 16, 16))
    lossless("xll-2-0-20bit-48k", "Four units of a stereo core frame and a lossless extension of a stereo set of 20 bits stored in 24.", dict(channels=2, mask=0x02, bit_resolution=20),
             (STEREO, 48000), sets=[m.xll_set(2, bit_res=20, storage=24)], fields=stats(2, 1, 48000, 20, 24))

    # Fields beyond the common ones: the replacement set, interpolation, representation, positions in polar coordinates, scalable least significant bits.
    lossless("xll-interpolated", "Four units whose lossless channel set states an interpolation factor of 2 (Table 8-6) at 96 kHz.", dict(channels=2, mask=0x02, max_rate=96000),
             (STEREO, 96000, 4 * 1024), sets=[m.xll_set(2, rate=96000, interp=1)], size=96,
             crosscheck=undecodable(cc, "XLL sampling frequency modifier is not implemented", "ffmpeg's decoder does not implement the sampling frequency modifier (dca_xll.c: 'XLL sampling frequency modifier')",
                                  ffprobe_rate=48000, skip={"ffprobe_rate": "ffmpeg takes the rate from the core until its decoder has decoded the lossless extension, which it refuses here"}))
    lossless("xll-replacement-set", "Four units whose lossless channel set is a member of replacement set 1 and the active one (Table 8-7).", dict(channels=2, mask=0x02),
             (STEREO, 48000), sets=[m.xll_set(2, replacement=1, active=True)],
             crosscheck=undecodable(cc, "XLL replacement set is not implemented", "ffmpeg's decoder does not implement replacement sets (dca_xll.c: 'XLL replacement set')"))
    lossless("xll-no-mask", "Four units whose lossless channel set gives the position of each channel in polar coordinates, not a channel mask.", dict(channels=2, mask=0x02),
             (STEREO, 48000), sets=[m.xll_set(2, polar=True)],
             crosscheck=undecodable(cc, "Disabled XLL channel mask is not implemented", "ffmpeg's decoder does not implement polar speaker positions (dca_xll.c: 'Disabled XLL channel mask')"))
    lossless("xll-scalable-lsbs", "Four units whose lossless extension splits the least significant bits of band 0 (the scalable least significant bits flag of the common header).", dict(channels=2, mask=0x02),
             (STEREO, 48000), xll=dict(scalable=True))

    # The reserved value and the sizes that do not fit.
    lossless("xll-downmix-type-7", "Four units whose primary lossless channel set states downmix type 7, which Table 8-8 does not use.", dict(channels=2, mask=0x02),
             (STEREO, 48000), sets=[m.xll_set(2, dmix=dict(type=7, embedded=False))], findings=[("dts.reserved-value", "nLLDownmixType 7 is unused")],
             crosscheck=undecodable(cc, "Invalid XLL primary channel set downmix type", "ffmpeg's decoder refuses downmix type 7 (dca_xll.c: 'Invalid XLL primary channel set downmix type')"))
    lossless("xll-future-version", "Four units whose lossless extension is of version 2, which 8.3.1 does not describe, so its channel sets are not read.", dict(channels=2, mask=0x02),
             (STEREO, 48000), xll=dict(version=1), findings=[("dts.reserved-value", "XLL version 2 is newer")],
             crosscheck=undecodable(cc, "XLL stream version 2 is not implemented", "ffmpeg's decoder does not implement a newer version (dca_xll.c: 'XLL stream version 2')"))
    lossless("xll-resolution-over-width", "Four units whose lossless channel set has a source resolution of 24 bits and a storage width of 16.", dict(channels=2, mask=0x02),
             (STEREO, 48000), sets=[m.xll_set(2, bit_res=24, storage=16)], findings=[("dts.lossless-inconsistent", "source resolution of 24 bits is more than its storage width of 16 bits")],
             crosscheck=undecodable(cc, "Invalid PCM bit resolution for XLL channel set", "ffmpeg's decoder refuses a resolution beyond the width (dca_xll.c: 'Invalid PCM bit resolution for XLL channel set')"))
    lossless("xll-mask-count", "Four units whose lossless channel set of 2 channels has a channel mask of 3 speakers.", dict(channels=2, mask=0x02),
             (STEREO, 48000), sets=[m.xll_set(2, mask=0x7)], findings=[("dts.lossless-inconsistent", "channel mask of 3 speakers")],
             crosscheck=undecodable(cc, "Invalid XLL channel mask", "ffmpeg's decoder refuses a mask whose speakers differ from the channels (dca_xll.c: 'Invalid XLL channel mask')"))
    lossless("xll-set-bad-crc", "Four units whose lossless channel set sub-header has a CRC16 that fails.", dict(channels=2, mask=0x02),
             (STEREO, 48000), sets=[m.xll_set(2, bad_crc=True)], findings=[("dts.crc-mismatch", "channel set 1 sub-header CRC16")],
             crosscheck=undecodable(cc, "Invalid XLL sub-header checksum", "ffmpeg's decoder checks the sub-header CRC16 (dca_xll.c: 'Invalid XLL sub-header checksum')"))
    lossless("xll-navigation-bad-crc", "Four units whose lossless navigation index has a CRC16 that fails.", dict(channels=2, mask=0x02),
             (STEREO, 48000), xll=dict(bad_navi_crc=True), findings=[("dts.crc-mismatch", "navigation index CRC16")],
             crosscheck=undecodable(cc, "Invalid NAVI checksum", "ffmpeg's decoder checks the navigation index CRC16 (dca_xll.c: 'Invalid NAVI checksum')"))
    lossless("xll-x-navigation-bad-crc", "Four units whose lossless frame has the DTS:X word after its band data and a navigation index whose CRC16 fails, so the word is not looked for.", dict(channels=6, mask=0x0F),
             (SURROUND_5_1, 48000), xll=dict(x_sync=0x02000850, bad_navi_crc=True), findings=[("dts.crc-mismatch", "navigation index CRC16")],
             fields={"DTS/Statistics/HasDtsX": "false"},
             crosscheck=undecodable(cc, "Invalid NAVI checksum", "ffmpeg's decoder checks the navigation index CRC16 before it looks for the DTS:X word (dca_xll.c: 'Invalid NAVI checksum')"))
    lossless("xll-x-rejected-set", "Four units whose lossless channel set has a source resolution of 24 bits stored in 16, so it is rejected, and the DTS:X word after the band data, which is not looked for.", dict(channels=2, mask=0x02),
             (STEREO, 48000), sets=[m.xll_set(2, bit_res=24, storage=16)], xll=dict(x_sync=0x02000850),
             findings=[("dts.lossless-inconsistent", "source resolution of 24 bits is more than its storage width of 16 bits")], fields={"DTS/Statistics/HasDtsX": "false"},
             crosscheck=undecodable(cc, "Invalid PCM bit resolution for XLL channel set", "ffmpeg's decoder refuses a resolution beyond the width in parse_sub_headers, before it looks for the DTS:X word (dca_xll.c: 'Invalid PCM bit resolution for XLL channel set'); MediaInfo 24.01 would still read the word",
                                  ffprobe_channels=2, skip={"ffprobe_channels": "ffmpeg cannot decode the lossless layer, so it reports the core's two channels and their layout"}))
    lossless("xll-frame-size", "Four units whose lossless extension states a frame of 20 bytes, less than its band data reach.", dict(channels=2, mask=0x02),
             (STEREO, 48000), xll=dict(frame_size=20), findings=[("dts.lossless-inconsistent", "past the 20 bytes of the frame")],
             crosscheck=undecodable(cc, "Read past end of XLL frame", "ffmpeg's decoder refuses band data that runs past the frame's stated size (dca_xll.c: 'Read past end of XLL frame')"))
    lossless("xll-set-size", "Four units whose lossless channel set sub-header states a size of 6 bytes, less than its fields need.", dict(channels=2, mask=0x02),
             (STEREO, 48000), sets=[m.xll_set(2, size_override=6)], findings=[("dts.substream-overrun", "channel set 1 sub-header's fields end past the 6 bytes")],
             crosscheck=undecodable(cc, "Invalid XLL sub-header checksum", "ffmpeg's decoder checks the sub-header CRC16 over the stated size (dca_xll.c: 'Invalid XLL sub-header checksum')"))

    # Cut inside the sub-header and inside the navigation index.
    full = m.xll_frame(96)
    for file_name, text, cut, wanted in (("xll-set-header-overrun", "channel set sub-header", 22, "ends inside the channel set 1 sub-header"),
                                         ("xll-navigation-overrun", "navigation index", 26, "ends inside the navigation index")):
        a = dict(components=["core", "xll"], sizes={"core": 96, "xll": cut}, raw={"xll": full[:cut]}, channels=2, mask=0x02)
        parts = [unit(a) for _ in range(2)]
        fixture(file_name, ".dts", f"Two units whose lossless component ends inside the {text}.", b"".join(parts), rows(parts), [stream(name, 48000, STEREO, "2/0", 2 * 512, vbr=True)],
                [("dts.header-overrun", wanted)],
                crosscheck=undecodable(cc, "Invalid XLL sub-header checksum" if "sub-header" in text else "Invalid NAVI checksum",
                                       "ffmpeg's decoder checks the CRC16 of the sub-header and of the navigation index over the cut component (dca_xll.c), which fails"))

    # Padding in the headers: the reserved bytes of the common header and of a sub-header.
    lossless("xll-header-padding", "Four units whose lossless common header and channel set sub-header end with reserved bytes before the CRC16.", dict(channels=2, mask=0x02),
             (STEREO, 48000), sets=[m.xll_set(2, header_pad=3)], xll=dict(header_pad=2), fields=stats(2, 1, 48000, 24, 24))

    # Not loudspeaker feeds: a stereo Lt/Rt set (8.3.2, Table 8-3).
    lossless("xll-lt-rt", "Four units whose lossless asset is Lt/Rt encoded for matrix surround decoding: its channels are not loudspeaker feeds, and its set has no mask.",
             dict(channels=2, one_to_one=False, representation=2), (STEREO, 48000), sets=[m.xll_set(2, one_to_one=False)], fields=stats(2, 1, 48000, 24, 24))

    # DTS:X.
    five_x = dict(channels=6, mask=0x0F)
    for file_name, word, description, codec in (
        ("xll-x", 0x02000850, "the word 0x02000850 that FFmpeg and MediaInfo read as DTS:X", name + " + DTS:X"),
        ("xll-x-imax", 0xF14000D0, "the word 0xF14000D0 that FFmpeg and MediaInfo read as DTS:X IMAX", name + " + DTS:X IMAX"),
        ("xll-x-imax-d1", 0xF14000D1, "the word 0xF14000D1 that FFmpeg reads as DTS:X IMAX and MediaInfo as DTS:X", name + " + DTS:X IMAX"),
        ("xll-x-other-word", 0x12345678, "the word 0x12345678, which neither tool knows", name),
    ):
        lossless(file_name, f"Four units of a stereo core frame and a lossless extension of a 5.1 channel set whose frame has {description} after its band data.", five_x,
                 (SURROUND_5_1, 48000), size=96, xll=dict(x_sync=word), name_=codec,
                 fields={"DTS/Statistics/HasDtsX": "true" if word == 0x02000850 else "false", "DTS/Statistics/HasDtsXImax": "true" if word in (0xF14000D0, 0xF14000D1) else "false"},
                 crosscheck=cc_lossless(cc))

    # FFmpeg's dca_xll.c gives up on a frame in the sub-headers before it looks for the word, for what it does not implement as for what is invalid;
    # the analyzer reads no word after the same fields, so the stream keeps the profile of the lossless extension alone.
    x = dict(x_sync=0x02000850)
    nox = {"DTS/Statistics/HasDtsX": "false"}
    lossless("xll-x-interpolated", "Four units whose lossless channel set states an interpolation factor of 2 at 96 kHz, and the DTS:X word after the band data, which is not looked for.",
             dict(channels=2, mask=0x02, max_rate=96000), (STEREO, 96000, 4 * 1024), sets=[m.xll_set(2, rate=96000, interp=1)], xll=x, size=96, fields=nox,
             crosscheck=undecodable(cc, "XLL sampling frequency modifier is not implemented", "ffmpeg's decoder does not implement the sampling frequency modifier, and stops before the DTS:X word (dca_xll.c: 'XLL sampling frequency modifier')",
                                  ffprobe_rate=48000, skip={"ffprobe_rate": "ffmpeg takes the rate from the core until its decoder has decoded the lossless extension, which it refuses here"}))
    lossless("xll-x-downmix-type-7", "Four units whose primary lossless channel set states downmix type 7, which Table 8-8 does not use, and the DTS:X word after the band data, which is not looked for.",
             dict(channels=2, mask=0x02), (STEREO, 48000), sets=[m.xll_set(2, dmix=dict(type=7, embedded=False))], xll=x, fields=nox, findings=[("dts.reserved-value", "nLLDownmixType 7 is unused")],
             crosscheck=undecodable(cc, "Invalid XLL primary channel set downmix type", "ffmpeg's decoder refuses downmix type 7 before it looks for the DTS:X word (dca_xll.c: 'Invalid XLL primary channel set downmix type')"))
    lossless("xll-x-storage-width-18", "Four units whose lossless channel set stores its samples in 18 bits, and the DTS:X word after the band data, which is not looked for.",
             dict(channels=2, mask=0x02, bit_resolution=18), (STEREO, 48000), sets=[m.xll_set(2, bit_res=18, storage=18)], xll=x, fields={**nox, **stats(2, 1, 48000, 18, 18)},
             crosscheck=undecodable(cc, "18-bit XLL storage resolution is not implemented", "ffmpeg's decoder implements storage widths of 16, 20 and 24 bits only, and stops before the DTS:X word (dca_xll.c: '18-bit XLL storage resolution')"))
    lossless("xll-x-no-mask", "Four units whose lossless channel set gives the position of each channel in polar coordinates, not a channel mask, and the DTS:X word after the band data, which is not looked for.",
             dict(channels=2, mask=0x02), (STEREO, 48000), sets=[m.xll_set(2, polar=True)], xll=x, fields=nox,
             crosscheck=undecodable(cc, "Disabled XLL channel mask is not implemented", "ffmpeg's decoder does not implement polar speaker positions, and stops before the DTS:X word (dca_xll.c: 'Disabled XLL channel mask')"))


def cc_hd(cc, **extra):
    """The crosscheck of a .dtshd file with a lossless stream: no bit rate in ffprobe, and libdca reads raw DTS only."""
    skip = {"libdca": "libdca reads raw DTS only.", **extra.pop("skip", {})}
    return cc_lossless(cc, libdca=False, skip=skip, **extra)


def build_dtshd(api):
    """The .dtshd files: the chunks of A/103 Annex E around units of a core frame and a lossless extension substream."""
    fixture, rows, cc, stream = api["fixture"], api["rows"], api["cc"], api["stream"]
    name = "DTS-HD Master Audio"
    five = dict(components=["core", "xll"], sizes={"core": 96, "xll": 96}, channels=6, mask=0x0F)

    def units(count=4, asset=five, x_sync=None):
        a = dict(asset)
        if x_sync is not None:
            a["component_options"] = {"xll": {"x_sync": x_sync}}
        return [m.core_frame(size=512) + m.exss([dict(a)]) for _ in range(count)]

    def build_file(chunks, parts):
        """chunks: (id, payload) or (id, payload, size) in file order, the payload None for STRMDATA, which holds parts. Returns the file and the frames' rows."""
        data = b""
        frames = []
        for chunk in chunks:
            chunk_id, payload = chunk[0], chunk[1]
            if payload is None:
                body = b"".join(parts)
                start = len(data) + 16
                frames = [[start + o, n] for o, n in rows(parts)]
                data += m.dtshd_chunk(chunk_id, body, **({"size": chunk[2]} if len(chunk) > 2 else {}))
            else:
                data += m.dtshd_chunk(chunk_id, payload, **({"size": chunk[2]} if len(chunk) > 2 else {}))
        return data, frames

    def file(file_name, description, chunks, parts, expected, findings=(), fields=None, crosscheck=None, gaps=(), cut=None, frames=None, name_=name, extra=b"", regions=None):
        data, rows_ = build_file(chunks, parts)
        data += extra
        if cut is not None:
            data = data[:cut]
        fixture(file_name, ".dtshd", description, data, rows_ if frames is None else frames, expected, list(findings), fields=fields, gaps=gaps, crosscheck=crosscheck or cc(), fmt="DTS-HD", **({"regions": regions} if regions else {}))

    lossless = [stream(name, 48000, SURROUND_5_1, "2/0", 4 * 512, vbr=True)]
    header = ("DTSHDHDR", m.dtshd_header(vbr=True, core=True, extension=True, tc_rate=2, stamp=48000 * 3600 + 1234, substreams=0))
    presentation = ("AUPR-HDR", m.dtshd_presentation(48000, 4, 512, 2048, 0x0F, delay=0, lossless=True, core=True, core_in_extension=False))
    metadata = [("CORESSMD", m.dtshd_core_metadata(48000, 384, 0x0003, 512)), ("EXTSS_MD", m.dtshd_extension_metadata(1000, True, 1200, 256))]

    all_chunks = [header, ("FILEINFO", m.dtshd_text("Hand-built DTS-HD Master Audio, VBR, 5.1")), *metadata, presentation,
                  ("AUPRINFO", bytes([0]) + m.dtshd_text("Main audio")), ("NAVI-TBL", m.dtshd_table(2, 4, interval=2)),
                  ("BITSHVTB", bytes([0]) + (1).to_bytes(4, "big") + (3).to_bytes(4, "big") + bytes([2])), ("BLACKOUT", bytes(16)), ("BRANCHPT", m.dtshd_table(1, 4)),
                  ("STRMDATA", None), ("TIMECODE", m.dtshd_timecode(48000, 2, 172800000, 7, 172800512, 9)),
                  ("BUILDVER", m.dtshd_text("DTS-HD Encoder hand-built 1.2.3 (Jan  1 2026)"))]
    parts = units()
    claim = {"STRMDATA/DTS/Encoder": "DTS-HD Encoder hand-built 1.2.3 (Jan  1 2026)", "STRMDATA/DTS/Encoder/Confidence": "Low"}
    file("dtshd-all-chunks", "A .dtshd file with every chunk Annex E defines, four units of a core frame and a lossless extension substream in STRMDATA, and BUILDVER and TIMECODE after it.",
         all_chunks, parts, lossless, fields={"BUILDVER/Text": "DTS-HD Encoder hand-built 1.2.3 (Jan  1 2026)", "DTSHDHDR/Stream Metadata/IsVbr": "true",
                                               "DTSHDHDR/Time Code": "01:00:00 and 1234 samples", "FILEINFO/Text": "Hand-built DTS-HD Master Audio, VBR, 5.1", **claim},
         crosscheck=cc_hd(cc))
    file("dtshd-core", "A .dtshd file with a header, the audio presentation header and three stereo core frames in STRMDATA.",
         [("DTSHDHDR", m.dtshd_header()), ("AUPR-HDR", m.dtshd_presentation(48000, 3, 512, 1536, 0x02)), ("STRMDATA", None)],
         [m.core_frame(size=512) for _ in range(3)], [stream("DTS", 48000, STEREO, "2/0", 3 * 512)],
         crosscheck=cc(libdca=False, skip={"libdca": "libdca reads raw DTS only."}))
    file("dtshd-lossless", "A .dtshd file with the metadata of a variable bit rate 5.1 stream and four units of a core frame and a lossless extension substream.",
         [header, *metadata, presentation, ("STRMDATA", None)], parts, lossless, crosscheck=cc_hd(cc))
    file("dtshd-x", "A .dtshd file whose lossless extension frames have the word that FFmpeg and MediaInfo read as DTS:X after their band data.",
         [header, presentation, ("STRMDATA", None)], units(4, x_sync=0x02000850), [stream(name + " + DTS:X", 48000, SURROUND_5_1, "2/0", 4 * 512, vbr=True)],
         fields={"STRMDATA/DTS/Statistics/HasDtsX": "true"}, crosscheck=cc_hd(cc))

    # Damage.
    lossless_findings = lambda *f: list(f)
    data, rows_ = build_file([header, presentation, ("STRMDATA", None)], parts)
    cut = len(data) - 100
    file("dtshd-stream-truncated", "A .dtshd file that ends 100 bytes inside the last unit of its STRMDATA chunk.", [header, presentation, ("STRMDATA", None)], parts,
         lossless, findings=[("dtshd.chunk-truncated", "STRMDATA chunk"), ("dts.frame-truncated", "The last frame")], cut=cut,
         frames=rows_[:-1] + [[rows_[-1][0], rows_[-1][1] - 100]],
         crosscheck=cc_hd(cc, ffprobe=False, decode=False, skip={"ffprobe": "ffmpeg's demuxer skips the STRMDATA chunk by its size and fails at the end of the cut file (dtshddec.c: avio_skip: 'End of file')", "decode": "no container"}))
    data, _ = build_file([header, ("CORESSMD", m.dtshd_core_metadata(48000, 384, 0x0003, 512)), ("STRMDATA", None)], parts)
    file("dtshd-metadata-truncated", "A .dtshd file that ends inside its CORESSMD chunk, before any audio.", [header, ("CORESSMD", m.dtshd_core_metadata(48000, 384, 0x0003, 512)), ("STRMDATA", None)],
         parts, [], findings=[("dtshd.chunk-truncated", "CORESSMD chunk"), ("dtshd.no-stream-data", "no STRMDATA")], cut=16 + 16 + 16 + 8, frames=[],
         crosscheck=cc(ffprobe=False, decode=False, libdca=False, skip={"ffprobe": "ffmpeg's demuxer finds no STRMDATA chunk in the cut file (dtshddec.c: end of file before the stream data)", "decode": "no stream", "libdca": "libdca reads raw DTS only."}))
    file("dtshd-chunk-size-too-small", "A .dtshd file whose FILEINFO chunk states a size of 0 bytes, less than the 4 that FFmpeg's demuxer accepts.",
         [header, ("FILEINFO", b"", 0), ("STRMDATA", None)], parts, lossless,
         findings=[("dtshd.chunk-size", "FILEINFO chunk")], frames=None,
         crosscheck=cc(ffprobe=False, decode=False, libdca=False, skip={"ffprobe": "ffmpeg's demuxer refuses a chunk size under 4 (dtshddec.c: 'chunk size too small')", "decode": "no container", "libdca": "libdca reads raw DTS only."}))
    # Padding that does not bring the next chunk to a 32-bit boundary.
    misaligned = [header, ("FILEINFO", m.dtshd_text("odds"), 5), ("STRMDATA", None)]
    data = m.dtshd_chunk("DTSHDHDR", header[1]) + m.dtshd_chunk("FILEINFO", m.dtshd_text("odds"), pad=False)
    rows_ = [[len(data) + 16 + o, n] for o, n in rows(parts)]
    fixture("dtshd-chunk-misaligned", ".dtshd", "A .dtshd file whose FILEINFO chunk is not padded, so STRMDATA starts off a 32-bit boundary.",
            data + m.dtshd_chunk("STRMDATA", b"".join(parts)), rows_, lossless, [("dtshd.chunk-misaligned", "STRMDATA chunk")], crosscheck=cc_hd(cc), fmt="DTS-HD")
    # A STRMDATA size that is less than the stream: the bytes after it are not a chunk.
    short = sum(n for _, n in rows(parts)) - 100
    frames = [[16 + 16 + 16 + o, n] for o, n in rows(parts)]
    file("dtshd-stream-size-wrong", "A .dtshd file whose STRMDATA chunk states a size 100 bytes short of the stream, so the end of the last unit is read as a chunk.",
         [header, ("STRMDATA", None, short)], parts, lossless,
         findings=[("dtshd.chunk-header", "are not a chunk header"), ("dts.frame-truncated", "The last frame")],
         frames=frames[:-1] + [[frames[-1][0], frames[-1][1] - 100]],
         crosscheck=cc_hd(cc, ffprobe=False, decode=False, skip={"ffprobe": "ffmpeg's demuxer reads the bytes after the chunk as a chunk header and refuses its size (dtshddec.c: 'chunk size too small' or 'chunk size too big')", "decode": "no container"}))
    file("dtshd-trailing-data", "A .dtshd file with five bytes after its last chunk.", [header, ("STRMDATA", None)], parts, lossless,
         findings=[("dtshd.trailing-data", "5 bytes after the last chunk")], extra=b"\x01\x02\x03\x04\x05", crosscheck=cc_hd(cc))
    # An ID3v1 tag after the last chunk: its first sixteen bytes read as a chunk ID and, with a title of five characters padded with nulls, as a size of
    # zero. It is a tag, not a chunk: the chain of chunks ends at the tag (the tag owns the bytes), so the file is not cut short by it.
    for title, label in (("Intro", "five"), ("Intros", "six")):
        tag = b"TAG" + title.encode().ljust(30, b"\0") + b"Artist".ljust(30, b"\0") + b"Album".ljust(30, b"\0") + b"2026" + b"Comment".ljust(28, b"\0") + b"\0\x01\x11"
        size = len(m.dtshd_chunk("DTSHDHDR", header[1]) + m.dtshd_chunk("STRMDATA", b"".join(parts)))
        file(f"dtshd-id3v1-{label}-char-title", f"A .dtshd file with an ID3v1 tag whose title has {label} characters after its last chunk.", [header, ("STRMDATA", None)], parts, lossless,
             extra=tag, regions=[["ID3v1", size, 128]],
             crosscheck=cc_hd(cc, ffprobe=False, decode=False, skip={"ffprobe": "ffmpeg's demuxer reads the tag as a chunk header and refuses its size (dtshddec.c: 'chunk size too small' or 'chunk size too big')", "decode": "no container"}))
    file("dtshd-no-stream-data", "A .dtshd file with a header and metadata, but no STRMDATA chunk.", [header, presentation], [], [], findings=[("dtshd.no-stream-data", "no STRMDATA")], frames=[],
         crosscheck=cc(ffprobe=False, decode=False, libdca=False, skip={"ffprobe": "ffmpeg's demuxer finds no STRMDATA chunk (dtshddec.c: end of file)", "decode": "no stream", "libdca": "libdca reads raw DTS only."}))

    # Reserved values, metadata that differs from the stream, chunks the standard does not define, tables that do not fit.
    reserved = ("DTSHDHDR", m.dtshd_header(clock_code=3, tc_rate=9, reserved_flags=0x0100, core=True, extension=True))
    file("dtshd-reserved-values", "A .dtshd file whose header has a reserved reference clock code, a reserved time code frame rate and a reserved flag, and whose presentation has a reserved flag.",
         [reserved, ("AUPR-HDR", m.dtshd_presentation(48000, 4, 512, 2048, 0x0F, lossless=True, core=True, reserved_flags=0x0100)), ("STRMDATA", None)], parts, lossless,
         findings=[("dtshd.reserved-value", "reference clock code of the time code is 3"), ("dtshd.reserved-value", "time code frame rate 9 is reserved"),
                   ("dtshd.reserved-value", "Bitw_Stream_Metadata has the reserved flags"), ("dtshd.reserved-value", "Bitw_Aupres_Metadata has the reserved flags")],
         crosscheck=cc_hd(cc))
    file("dtshd-metadata-differs", "A .dtshd file whose AUPR-HDR gives 96,000 Hz and the channels of a 7.1 mask for a 48 kHz 5.1 stream.",
         [header, ("AUPR-HDR", m.dtshd_presentation(96000, 4, 1024, 4096, 0x84B, lossless=True, core=True)), ("STRMDATA", None)], parts, lossless,
         findings=[("dtshd.metadata-differs", "Max_Sample_Rate_Hz 96000"), ("dtshd.metadata-differs", "Channel_Mask names 8 channels")],
         crosscheck=cc(ffprobe=False, libdca=False, skip={"ffprobe": "ffmpeg's demuxer takes the sampling frequency and the channels from AUPR-HDR, which the decoder then overrides, so the stream it reports is not the file's metadata", "libdca": "libdca reads raw DTS only."}))
    file("dtshd-unknown-chunk", "A .dtshd file with a chunk of an ID the standard does not define between the header and STRMDATA.", [header, ("XTRACHNK", bytes(range(16))), ("STRMDATA", None)],
         parts, lossless, crosscheck=cc_hd(cc))
    file("dtshd-table-size", "A .dtshd file whose NAVI-TBL states 1,000 entries of 4 bytes and holds one.", [header, ("NAVI-TBL", m.dtshd_table(1000, 4, interval=2, data=bytes(4))), ("STRMDATA", None)],
         parts, lossless, findings=[("dtshd.table-size", "1000 entries of 4 bytes")], crosscheck=cc_hd(cc))
    file("dtshd-cbr", "A .dtshd file with the metadata of a constant bit rate stream: the extension substreams' frame payload, no peak rate.",
         [("DTSHDHDR", m.dtshd_header(vbr=False)), ("CORESSMD", m.dtshd_core_metadata(48000, 384, 0x0003, 512)), ("EXTSS_MD", m.dtshd_extension_metadata(500, False, payload=192)), presentation, ("STRMDATA", None)],
         parts, lossless, fields={"EXTSS_MD/Frame Payload": "192 bytes"}, crosscheck=cc_hd(cc))
