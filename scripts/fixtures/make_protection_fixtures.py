#!/usr/bin/env python3
"""Writes the hand-built MP4 fixtures of protected tracks into tests/fixtures/mp4: Common Encryption (ISO/IEC 23001-7)
in its four schemes, with tenc, pssh, senc, saiz, saio and a sample group of encryption parameters, iTunes-style (drms)
and Audible-style (aavd with an adrm box) entries built from the structures the sources name (VLC's libmp4.c, MediaInfo's
File_Mpeg4, GStreamer's qtdemux and FFmpeg's mov.c), an iTunes purchase on a clear file, and damaged variants.

The encrypted samples are pseudo-random bytes (a SHA-256 stream), never valid frames, so an analyzer that read them as AAC
or AC-3 would find faults; the keys and IVs are the test values below and protect nothing. Each fixture has a
<name>.expected.json manifest in the format of make_mp4_fixtures.py, with these additions:

- "protection" in a stream: the scheme, whether its samples are encrypted and the DRM systems the analyzer must report,
  absent for a stream nothing protects.
- "samples": the samples of the protected track as offset and size, which ffprobe must list as its packets where "units"
  is empty because the analyzer reads no frames.
- "crosscheck.encryption": what ffprobe (the "Encryption info" side data of a packet) and MediaInfo ("Encryption") report.

The script uses only the standard library and is deterministic, so CI reruns it and fails when the committed files differ.
"""

import hashlib
import json
import pathlib
import struct
import sys

import ac3_model as m
import dts_model
from make_mp4_fixtures import MP4, SPEAKERS, boxes_of, stream as aac_stream, unit_list
from mp4_boxes import (
    Box, Track, asc, box, data_atom, dinf, esds, freeform, frma, fullbox, ftyp, hdlr, ilst_meta, item, mdhd, movie, mp4a, mvhd, pssh, saio, saiz, sbgp_seig,
    schi, schm, seig_entry, senc, sgpd_seig, sinf, stco, stsc, stsd, stsz, stts, tenc, text_item, tkhd, u8, u16, u32, u64, udta, units, write,
)

KID = bytes(range(0x10, 0x20))
KID_TEXT = "10111213-1415-1617-1819-1a1b1c1d1e1f"
OTHER_KID = bytes(range(0x80, 0x90))
OTHER_KID_TEXT = "80818283-8485-8687-8889-8a8b8c8d8e8f"
CONSTANT_IV = bytes(range(0x40, 0x50))

WIDEVINE = "edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"
PLAYREADY = "9a04f079-9840-4286-ab92-e65be0885f95"
FAIRPLAY = "94ce86fb-07ff-4f43-adb8-93d2fa968ca2"
COMMON = "1077efec-c0b2-4d02-ace3-3c1e52e2fb4b"
UNKNOWN = "01234567-89ab-cdef-0123-456789abcdef"
NAMES = {WIDEVINE: "Widevine Content Protection", PLAYREADY: "Microsoft PlayReady", FAIRPLAY: "Apple FairPlay", COMMON: "W3C Common PSSH box"}

LC = asc(2, 44100, 2, sbr=False, signaling="backward")
STSD = "MP4/moov/trak/mdia/minf/stbl/stsd"
ENCA = STSD + "/enca"
SINF = ENCA + "/sinf"
TENC = SINF + "/schi/tenc"
SAMPLES_ENCRYPTED = ["mp4.samples-encrypted", "are encrypted"]

SCHEMES = {
    b"cenc": "Common Encryption, AES-CTR (cenc)",
    b"cens": "Common Encryption, AES-CTR with pattern (cens)",
    b"cbc1": "Common Encryption, AES-CBC (cbc1)",
    b"cbcs": "Common Encryption, AES-CBC with pattern (cbcs)",
}


def ciphertext(count, seed=b"encspect-test-ciphertext", base=300, spread=90, step=37):
    """count pseudo-random samples whose sizes vary: a SHA-256 stream, not any coding."""
    out = []
    for index in range(count):
        size = base + (index * step) % spread
        data = b""
        counter = 0
        while len(data) < size:
            data += hashlib.sha256(seed + struct.pack(">II", index, counter)).digest()
            counter += 1
        out.append(data[:size])
    return out


def coded_stream(codec, rate, channels, mode, samples, layout=True, vbr=True):
    """A stream whose coding the container's configuration names, or none: AAC's channel layout is the configuration's, AC-3 and DTS state theirs in
    figures the analyzer reports as a stereo pair or not at all (mode None)."""
    entry = aac_stream(codec, rate, channels, mode, samples, vbr=vbr)
    entry["channel_mode"] = mode
    if not layout:
        entry["channel_layout"] = []
    return entry


def protection(scheme, encrypted=True, systems=()):
    return {"scheme": scheme, "is_encrypted": encrypted, "systems": [NAMES.get(s, s) for s in systems]}


def sample_rows(offsets, samples, chunk):
    return unit_list(offsets, samples, chunk)


def encrypted_aac_entry(*sinf_children, original=b"mp4a", format=b"enca", config=LC, extra=()):
    return mp4a(2, 44100, esds(0x40, config, 0, 0), sinf(frma(original), *sinf_children), *extra, format=format)


def cenc_sinf(scheme=b"cenc", **options):
    return [schm(scheme), schi(tenc(**options))]


def manifest(description, data, records, streams, samples_rows, fields, findings, units_rows=(), crosscheck=None):
    return {
        "description": description, "length": len(data), "format": "MP4", "boxes": boxes_of(records, len(data)), "streams": streams,
        "units": [list(u) for u in units_rows], "samples": samples_rows, "fields": fields, "findings": [list(f) for f in findings],
        "crosscheck": crosscheck if crosscheck is not None else {"ffprobe": True, "mp4box": True, "faad": None,
                                                                 "skip": {"faad": "faad2 reads clear AAC only and rejects the encrypted sample entry."}},
    }


def encrypted(name, description, entry, *, count=30, extra_moov=(), extra_stbl=(), findings=None, fields=None, streams=None, scheme_name=None,
              systems=(), before=(), crosscheck=None, brand=None, rate=44100, samples=None, mode="Stereo", codec="AAC LC", channels=2, durations=None, layout=True, track_options=None):
    samples = samples if samples is not None else ciphertext(count)
    track = Track(1, samples, rate, channels, entry, extra=list(extra_stbl), durations=durations, **(track_options or {}))
    options = {"moov_extra": list(extra_moov)}
    if brand:
        options["ftyp_box"] = brand
    data, records, offsets = movie([track], **options)
    total = sum(track.durations)
    stream_entry = streams if streams is not None else [dict(coded_stream(codec, rate, channels, mode, total, layout, vbr=codec == "AAC LC"), protection=protection(scheme_name, True, systems))]
    result = manifest(description, data, records, stream_entry, sample_rows(offsets[0], samples, track.chunk), fields or {},
                      findings if findings is not None else [SAMPLES_ENCRYPTED], crosscheck=crosscheck)
    return name, data, result


def common_encryption():
    out = []
    widevine = pssh(WIDEVINE, b"widevine test data")
    playready = pssh(PLAYREADY, b"<WRMHEADER>test</WRMHEADER>", kids=[KID, OTHER_KID])
    unknown = pssh(UNKNOWN, b"")
    schm_path = SINF + "/schm"
    out.append(encrypted(
        "cenc-aac", "AAC LC in an enca entry protected with cenc (AES-CTR): the sinf box names mp4a as the original format and the scheme cenc, tenc "
        "makes every sample protected with 8-byte IVs and a KID, and three pssh boxes in moov name Widevine, PlayReady (version 1, with two key IDs) "
        "and a system ID the registry does not list. The 30 samples are pseudo-random bytes, so the stream is described from the sample entry alone.",
        encrypted_aac_entry(*cenc_sinf()), extra_moov=[widevine, playready, unknown], scheme_name=SCHEMES[b"cenc"], systems=[WIDEVINE, PLAYREADY, UNKNOWN],
        fields={schm_path + "/Scheme Type": "cenc", schm_path + "/Scheme Version": "65536", SINF + "/frma/Format": "mp4a", TENC: "protected by default",
                TENC + "/Default IsProtected": "true", TENC + "/Default Per Sample IV Size": "8 bytes", TENC + "/Default KID": KID_TEXT,
                "MP4/moov/pssh": "Widevine Content Protection", "MP4/moov/pssh/System ID": f"{WIDEVINE} (Widevine Content Protection)",
                "MP4/moov/pssh/Data Size": "18 bytes", "MP4/moov/pssh[1]": "Microsoft PlayReady", "MP4/moov/pssh[1]/KID Count": "2",
                "MP4/moov/pssh[1]/KID": KID_TEXT, "MP4/moov/pssh[2]": UNKNOWN, "MP4/moov/pssh[2]/System ID": UNKNOWN,
                STSD + "/enca": "enca (encrypted audio)",
                "MP4/moov/trak/mdia/minf/stbl/stsd/enca/esds/ES_Descriptor/DecoderConfigDescriptor/DecoderSpecificInfo/AudioSpecificConfig": "AAC LC"},
        findings=[SAMPLES_ENCRYPTED],
        crosscheck={"ffprobe": True, "mp4box": True, "faad": None, "encryption": {"ffprobe": True, "mediainfo": "Encrypted"},
                    "skip": {"faad": "faad2 reads clear AAC only and rejects the encrypted sample entry."}}))
    for scheme, tag, options, text in [
        (b"cens", "cens-pattern", dict(version=1, crypt=1, skip=9), "AES-CTR with a pattern of 1 encrypted block and 9 clear ones: tenc version 1 gives the crypt and skip blocks."),
        (b"cbc1", "cbc1-iv16", dict(iv_size=16), "AES-CBC with 16-byte IVs."),
        (b"cbcs", "cbcs-constant-iv", dict(version=1, crypt=1, skip=9, iv_size=0, constant_iv=CONSTANT_IV),
         "AES-CBC with a pattern and no IV per sample: tenc gives a 16-byte constant IV, as cbcs files usually do."),
    ]:
        fields = {TENC: "protected by default", SINF + "/schm/Scheme Type": scheme.decode()}
        if options.get("version") == 1:
            fields.update({TENC + "/Default Crypt Byte Block": "1 block", TENC + "/Default Skip Byte Block": "9 blocks"})
        if "constant_iv" in options:
            fields.update({TENC + "/Default Per Sample IV Size": "0 bytes", TENC + "/Default Constant IV Size": "16 bytes", TENC + "/Default Constant IV": CONSTANT_IV.hex()})
        else:
            fields[TENC + "/Default Per Sample IV Size"] = f"{options.get('iv_size', 8)} bytes"
        out.append(encrypted(tag, f"AAC LC protected with {scheme.decode()}, {text}", encrypted_aac_entry(*cenc_sinf(scheme, **options)), scheme_name=SCHEMES[scheme],
                             fields=fields, crosscheck={"ffprobe": True, "mp4box": True, "faad": None, "encryption": {"ffprobe": True, "mediainfo": "Encrypted"},
                                                        "skip": {"faad": "faad2 reads clear AAC only and rejects the encrypted sample entry."}}))
    return out


def clear_protected():
    out = []
    samples = units("stereo", 20)
    entry = encrypted_aac_entry(*cenc_sinf(is_protected=0, iv_size=0))
    track = Track(1, samples, 44100, 2, entry)
    data, records, offsets = movie([track])
    out.append(("cenc-clear-default", data, manifest(
        "AAC LC in an enca entry whose tenc box sets the default IsProtected to false and whose track has no sample group that protects samples, a track of a protected stream that is wholly in the clear: the samples are valid AAC access units, so they are read like those of an unprotected track, and the stream is "
        "reported protected but not encrypted.", data, records, [dict(aac_stream("AAC LC", 44100, 2, "Stereo", 20 * 1024, vbr=True), protection=protection(SCHEMES[b"cenc"], False))],
        sample_rows(offsets[0], samples, 10), {TENC + "/Default IsProtected": "false", TENC: "not protected by default"},
        [["mp4.protected-samples-clear", "no sample is protected"]], units_rows=unit_list(offsets[0], samples, 10),
        crosscheck={"ffprobe": True, "mp4box": True, "faad": None, "encryption": {"ffprobe": False, "mediainfo": "Encrypted"},
                    "skip": {"faad": "faad2 rejects the protected sample entry.", "faad_trace": "faad2 rejects the protected sample entry."}})))
    alac = Box(b"enca", mp4a(2, 44100).payload, [sinf(frma(b"alac"), *cenc_sinf(is_protected=0, iv_size=0))])
    name, data, result = encrypted(
        "cenc-clear-unparsed", "An enca entry for ALAC, a coding that has no parser here, whose tenc box sets the default to not protected: none of the samples is encrypted, "
        "but the track is covered by the scheme, so it is still a stream and is reported as protected but not encrypted.", alac, rate=44100, durations=[4096] * 30, codec="Apple Lossless",
        scheme_name=SCHEMES[b"cenc"], mode=None, layout=False,
        findings=[["mp4.codec-not-analyzed", "protected"], ["mp4.protected-samples-clear", "no sample is protected"]],
        crosscheck={"ffprobe": False, "mp4box": False, "faad": None, "encryption": {"ffprobe": None, "mediainfo": "Encrypted"},
                    "skip": {"all": "an ALAC entry without its alac box, which the tools read in their own ways"}})
    result["streams"][0]["protection"]["is_encrypted"] = False
    out.append((name, data, result))
    return out


def sample_groups():
    protected = seig_entry(1, 8, OTHER_KID)
    clear = seig_entry(0, 0, bytes(16))
    return [encrypted(
        "cenc-sample-group", "AAC LC in an enca entry whose tenc box sets the default to not protected but whose sample group description of type seig "
        "(Common Encryption's encryption parameters) protects some samples, so the track's samples are taken as encrypted.",
        encrypted_aac_entry(*cenc_sinf(is_protected=0, iv_size=0)), extra_stbl=[sgpd_seig([protected, clear]), sbgp_seig([(10, 1), (20, 2)])],
        scheme_name=SCHEMES[b"cenc"],
        fields={"MP4/moov/trak/mdia/minf/stbl/sgpd": "seig (encryption)", "MP4/moov/trak/mdia/minf/stbl/sgpd/Entry": "protected",
                "MP4/moov/trak/mdia/minf/stbl/sgpd/Entry/IsProtected": "true", "MP4/moov/trak/mdia/minf/stbl/sgpd/Entry/KID": OTHER_KID_TEXT,
                "MP4/moov/trak/mdia/minf/stbl/sgpd/Entry[1]/IsProtected": "false"},
        crosscheck={"ffprobe": True, "mp4box": True, "faad": None, "encryption": {"ffprobe": None, "mediainfo": "Encrypted"},
                    "skip": {"faad": "faad2 rejects the encrypted sample entry.", "ffprobe_encryption": "ffmpeg 6.1 marks packets encrypted from the tenc default only, so the packets of a track protected by a sample group alone carry no encryption info."}}),
    ]


def fragmented_file(entries, stbl_extra=(), traf_extra=lambda n: [], tfhd_present=True, track_id=1, trex_index=1):
    """A fragmented file of ciphertext samples: moov with an empty track of the given sample entries, then two moof and mdat pairs."""
    stbl = box("stbl", b"", stsd(*entries), stts([]), stsc([]), stsz([]), stco([]), *stbl_extra)
    trak = box("trak", b"", tkhd(track_id, 0), box("mdia", b"", mdhd(44100, 0), hdlr(b"soun"), box("minf", b"", fullbox("smhd", 0, 0, bytes(4)), dinf(), stbl)))
    mvex = box("mvex", b"", fullbox("trex", 0, 0, u32(track_id) + u32(trex_index) + u32(1024) + u32(0) + u32(0)))
    boxes = [ftyp(b"iso6", 0, (b"iso6", b"dash", b"mp41")), box("moov", b"", mvhd(1000, 0), trak, mvex)]
    samples = ciphertext(20)
    rows = []

    def fragment(sequence, chunk, decode_time, offset):
        def build(data_offset):
            sizes = b"".join(u32(len(s)) for s in chunk)
            trun = fullbox("trun", 0, 0x201, u32(len(chunk)) + struct.pack(">i", data_offset) + sizes)
            traf = box("traf", b"", *([fullbox("tfhd", 0, 0x20000, u32(track_id))] if tfhd_present else []), fullbox("tfdt", 1, 0, u64(decode_time)), *traf_extra(len(chunk)), trun)
            return box("moof", b"", fullbox("mfhd", 0, 0, u32(sequence)), traf)

        moof_bytes, _ = write([build(0)], offset)
        return [build(len(moof_bytes) + 8), box("mdat", b"".join(chunk))]

    decode = 0
    for sequence, start in enumerate(range(0, 20, 10)):
        offset = len(write(boxes)[0])
        chunk = samples[start:start + 10]
        moof, mdat = fragment(sequence + 1, chunk, decode, offset)
        boxes += [moof, mdat]
        position = offset + len(write([moof], offset)[0]) + 8
        for sample in chunk:
            rows.append([position, len(sample)])
            position += len(sample)
        decode += len(chunk) * 1024
    data, records = write(boxes)
    return data, records, rows


def ivs(n):
    return b"".join(bytes([0, i]) * 4 for i in range(n))


def fragmented_groups():
    """Fragmented files whose tenc box sets the default to not protected, but in which something says that samples are protected: a seig description that protects
    (in the sample table, or in a track fragment, in versions 0, 1 and 2 of the box), a sample encryption box, or a track fragment that does not say whose it is.
    A track counts as encrypted unless its key information says no sample is protected and nothing in the file says otherwise."""
    yes = seig_entry(1, 8, KID)
    no = seig_entry(0, 0, bytes(16))
    clear = encrypted_aac_entry(*cenc_sinf(is_protected=0, iv_size=0))
    plain = mp4a(2, 44100, esds(0x40, LC, 0, 0))
    # name, why, entries, the sample table's extra boxes, the track fragment's extra boxes, options, findings besides the info finding
    cases = [
        ("cenc-fragmented-table-group", "the sample table's sgpd box describes a protecting group, which the fragments' runs use", [clear], [sgpd_seig([yes])], lambda n: [sbgp_seig([(n, 1)])], {}, []),
        ("cenc-fragmented-fragment-group", "each track fragment's own sgpd box describes a protecting group", [clear], [], lambda n: [sgpd_seig([yes]), sbgp_seig([(n, 0x10001)])], {}, []),
        ("cenc-fragmented-fragment-group-v0", "each track fragment's own sgpd box, of version 0 whose entries state no length, describes a protecting group", [clear], [],
         lambda n: [sgpd_seig([yes], version=0), sbgp_seig([(n, 0x10001)])], {}, []),
        ("cenc-fragmented-fragment-group-v2", "each track fragment's own sgpd box, of version 2 (default length, default index, entry lengths), describes a protecting group", [clear], [],
         lambda n: [sgpd_seig([yes], version=2, default=1), sbgp_seig([(n, 0x10001)])], {}, []),
        ("cenc-fragmented-table-default", "the sample table's sgpd box of version 2 makes a protecting description the default", [clear], [sgpd_seig([yes], version=2, default=1)], lambda n: [], {}, []),
        ("cenc-fragmented-fragment-default", "a track fragment's sgpd box of version 2 makes a protecting description its default, while the sample table's description does not protect", [clear],
         [sgpd_seig([no])], lambda n: [sgpd_seig([yes], version=2, default=1)], {}, []),
        ("cenc-fragmented-senc", "each track fragment has a sample encryption box, though the tenc box says that no sample is protected", [clear], [], lambda n: [senc(n, ivs(n))], {}, []),
        ("cenc-fragmented-misplaced-group", "a protecting description lies in a udta box of the track fragment, where it describes nothing, next to a clear one in no group", [clear], [],
         lambda n: [box("udta", b"", sgpd_seig([yes])), sgpd_seig([no]), sbgp_seig([(n, 0x10001)])], {}, [["mp4.sample-group-misplaced", "udta box"]] * 2),
        ("cenc-fragmented-no-tfhd", "the track fragments have no tfhd box, so that the sample entry and the track they use is not known, and the track has a clear entry first", [plain, encrypted_aac_entry(*cenc_sinf())], [],
         lambda n: [], {"tfhd_present": False, "track_id": 0, "trex_index": 2}, [["mp4.missing-tfhd", "tfhd"]]),
    ]
    out = []
    for name, text, entries, stbl_extra, traf_extra, options, findings in cases:
        data, records, rows = fragmented_file(entries, stbl_extra, traf_extra, **options)
        out.append((name, data, manifest(
            f"A fragmented MP4 of cenc AAC whose tenc box sets the default to not protected, but in which something says that samples are protected: {text}. "
            "The samples are pseudo-random bytes and must never be read as AAC.", data, records,
            [dict(aac_stream("AAC LC", 44100, 2, "Stereo", 0, vbr=True), protection=protection(SCHEMES[b"cenc"], True))], rows,
            {"MP4/moof/traf/trun": "10 samples"}, [SAMPLES_ENCRYPTED] + findings,
            crosscheck={"ffprobe": False, "mp4box": not any(k in name for k in ("no-tfhd", "-senc")), "faad": None, "encryption": {"ffprobe": None, "mediainfo": "Encrypted"},
                        "skip": {**({"mp4box": "MP4Box reports a track fragment without a tfhd box as invalid."} if "no-tfhd" in name else {}),
                                 **({"mp4box": "MP4Box -diso exits with an error on the file."} if "-senc" in name else {}),
                                 "faad": "faad2's MP4 reader reads samples from the sample tables only, which a fragmented file leaves empty.",
                                 "ffprobe": "ffmpeg 6.1 marks packets encrypted from the tenc default only, and reads these samples as clear AAC.",
                                 "mediainfo_duration": "a fragmented file's track header states no duration"}})))
    return out


def sample_group_defaults():
    """Whole files whose tenc default is not protected: a version 2 sgpd whose default description protects and no run maps the samples."""
    return [encrypted(
        "cenc-sample-group-default", "AAC LC in an enca entry whose tenc box sets the default to not protected, and whose seig sample group description box of version 2 "
        "(no entry lengths) makes its protecting description the default of every sample that no sample-to-group run maps; there is none, so the samples are taken as encrypted.",
        encrypted_aac_entry(*cenc_sinf(is_protected=0, iv_size=0)), extra_stbl=[sgpd_seig([seig_entry(1, 8, OTHER_KID)], version=2, default=1)],
        scheme_name=SCHEMES[b"cenc"],
        crosscheck={"ffprobe": True, "mp4box": False, "faad": None, "encryption": {"ffprobe": None, "mediainfo": "Encrypted"},
                    "skip": {"mp4box": "MP4Box 2.2 rejects a sample group description box of version 2 (it reports the movie box as invalid).",
                             "faad": "faad2 rejects the encrypted sample entry.", "ffprobe_encryption": "ffmpeg 6.1 marks packets encrypted from the tenc default only."}})]


def encrypted_groups():
    """Whole files in which the tenc box and the sample groups disagree, and where each box lies. A track is encrypted unless its key information says that no
    sample is protected and nothing in the file says otherwise, so every one of them is encrypted."""
    yes = seig_entry(1, 8, KID)
    no = seig_entry(0, 0, bytes(16))
    protecting = encrypted_aac_entry(*cenc_sinf())
    clear = encrypted_aac_entry(*cenc_sinf(is_protected=0, iv_size=0))
    n = 30
    one = [SAMPLES_ENCRYPTED, ["mp4.sample-group-misplaced", "lies in a"]]
    two = [SAMPLES_ENCRYPTED] + [["mp4.sample-group-misplaced", "lies in a"]] * 2
    cases = [
        ("cenc-groups-clear-tenc-protects", "tenc protects by default, and a sample group puts every sample in a description that does not", protecting, dict(extra_stbl=[sgpd_seig([no]), sbgp_seig([(n, 1)])]), [SAMPLES_ENCRYPTED]),
        ("cenc-groups-index-zero", "tenc protects by default, and the sample group puts the first samples in no group (index 0) and the rest in a description that does not protect", protecting,
         dict(extra_stbl=[sgpd_seig([no]), sbgp_seig([(10, 0), (20, 1)])]), [SAMPLES_ENCRYPTED]),
        ("cenc-groups-two-sbgp", "tenc protects by default, and two seig sample-to-group boxes each put half of the samples in a description that does not protect", protecting,
         dict(extra_stbl=[sgpd_seig([no]), sbgp_seig([(15, 1)]), sbgp_seig([(15, 1)])]), [SAMPLES_ENCRYPTED]),
        ("cenc-sgpd-v2-entry-lengths", "tenc does not protect, and a sample group description box of version 2 (default length 0, default description 1, entry lengths) has a protecting description", clear,
         dict(extra_stbl=[sgpd_seig([yes], version=2, default=1), sbgp_seig([(n, 1)])]), [SAMPLES_ENCRYPTED]),
        ("cenc-sgpd-v2-default-length", "tenc protects, and a sample group description box of version 2 with a default length of 20 (no entry lengths) has a description that does not", protecting,
         dict(extra_stbl=[sgpd_seig([no], version=2, default_length=20), sbgp_seig([(n, 1)])]), [SAMPLES_ENCRYPTED]),
        ("cenc-sgpd-unknown-version", "tenc protects, and the sample group description box is of version 3, which ISO/IEC 14496-12 does not define", protecting,
         dict(extra_stbl=[fullbox("sgpd", 3, 0, b"seig" + u32(0) + u32(1) + no), sbgp_seig([(n, 1)])]), [SAMPLES_ENCRYPTED, ["mp4.sgpd-version", "version 3"]]),
        ("cenc-groups-in-edts", "tenc protects, and the sample groups that put every sample in a clear description lie in an edts box", protecting,
         dict(before_mdia=[box("edts", b"", sgpd_seig([no]), sbgp_seig([(n, 1)]))]), two),
        ("cenc-sbgp-in-udta", "tenc protects, and the sample-to-group box lies in a udta box of the track", protecting,
         dict(extra_stbl=[sgpd_seig([no])], after_mdia=[box("udta", b"", sbgp_seig([(n, 1)]))]), one),
        ("cenc-sbgp-in-trak", "tenc protects, and the sample-to-group box lies directly in the trak box", protecting,
         dict(extra_stbl=[sgpd_seig([no])], after_mdia=[sbgp_seig([(n, 1)])]), one),
        ("cenc-decoy-group-in-entry", "tenc does not protect, the sample table's description protects, and a description that does not lies inside the sample entry",
         encrypted_aac_entry(*cenc_sinf(is_protected=0, iv_size=0), extra=[sgpd_seig([no])]), dict(extra_stbl=[sgpd_seig([yes]), sbgp_seig([(n, 1)])]), one),
        ("cenc-decoy-group-in-edts", "tenc does not protect, the sample table's description protects, and a description that does not lies in an edts box first", clear,
         dict(extra_stbl=[sgpd_seig([yes]), sbgp_seig([(n, 1)])], before_mdia=[box("edts", b"", sgpd_seig([no]))]), one),
        ("cenc-senc-tenc-clear", "tenc does not protect, and a senc box with its saiz and saio boxes gives the samples initialization vectors", clear,
         dict(extra_stbl=[senc(n, ivs(n)), saiz(8, n), saio([0])]), [SAMPLES_ENCRYPTED]),
        ("cenc-two-tenc", "the sinf box holds two tenc boxes, the first of which does not protect and the second of which does", encrypted_aac_entry(schm(b"cenc"), schi(tenc(is_protected=0, iv_size=0), tenc(is_protected=1))), {}, [SAMPLES_ENCRYPTED]),
        ("cenc-two-sinf", "the entry holds two sinf boxes, the first of which says that no sample is protected", mp4a(2, 44100, esds(0x40, LC, 0, 0), sinf(frma(b"mp4a"), *cenc_sinf(is_protected=0, iv_size=0)),
                                                                                                                      sinf(frma(b"mp4a"), *cenc_sinf()), format=b"enca"), {}, [SAMPLES_ENCRYPTED]),
    ]
    out = []
    for name, text, entry, options, findings in cases:
        track_options = {k: options[k] for k in ("before_mdia", "after_mdia") if k in options}
        out.append(encrypted(name, f"AAC LC in an enca entry: {text}. The 30 samples are pseudo-random bytes and are not read as AAC.", entry, extra_stbl=options.get("extra_stbl", ()),
                             findings=findings, scheme_name=SCHEMES[b"cenc"], track_options=track_options,
                             crosscheck={"ffprobe": False, "mp4box": name != "cenc-two-tenc", "faad": None, "encryption": {"ffprobe": None, "mediainfo": "Encrypted"},
                                         "skip": {**({"mp4box": "MP4Box lists only the first tenc box of a schi box."} if name == "cenc-two-tenc" else {}), "faad": "faad2 rejects the encrypted sample entry.", "faad_trace": "faad2 rejects the encrypted sample entry.",
                                                  "ffprobe": "ffmpeg reads the samples of these files as clear AAC where it does not find them encrypted."}}))
    return out


def unused_entry_run():
    """A clear track whose stsc box also names a protected entry for chunks the chunk offset box does not have: no sample uses it."""
    plain = mp4a(2, 44100, esds(0x40, LC, 0, 0))
    samples = units("stereo", 30)
    track = Track(1, samples, 44100, 2, plain, more_entries=[encrypted_aac_entry(*cenc_sinf())], stsc_box=stsc([(1, 10, 1), (5, 10, 2)]))
    data, records, offsets = movie([track])
    return [("cenc-unused-entry-run", data, manifest(
        "A track with a clear mp4a entry and a cenc-protected enca entry, whose stsc box names the second for chunks from the 5th on, though the chunk offset box holds three: no sample uses it, "
        "so the track is clear and is read as usual, and the run is a finding.", data, records, [aac_stream("AAC LC", 44100, 2, "Stereo", 30 * 1024, vbr=True)],
        sample_rows(offsets[0], samples, 10), {}, [["mp4.stsc-chunk-past-end", "chunk 5"]], units_rows=unit_list(offsets[0], samples, 10),
        crosscheck={"ffprobe": False, "mp4box": False, "faad": None, "encryption": {"ffprobe": None, "mediainfo": None},
                    "skip": {"all": "a file whose stsc box names a protected entry for chunks it does not have, which the tools read in their own ways"}}))]


def two_entries():
    """Tracks with two sample entries whose chunks use them in turn: a clear mp4a and a protected enca, two enca entries of which only the second protects, and an
    index past the entries."""
    clear = mp4a(2, 44100, esds(0x40, LC, 0, 0))
    entry = encrypted_aac_entry(*cenc_sinf())
    entry_clear = encrypted_aac_entry(*cenc_sinf(is_protected=0, iv_size=0))
    out = []
    for name, first, second, stsc_runs, text in [
        ("cenc-second-entry", clear, entry, [(1, 10, 2)], "a clear mp4a first and a cenc-protected enca second, whose chunks (stsc) all use the second entry"),
        ("cenc-two-enca-entries", entry_clear, entry, [(1, 10, 1), (2, 10, 2)], "two enca entries, the first of which sets the default to not protected and the second to protected, used by the chunks in turn"),
        ("cenc-entry-out-of-range", clear, entry, [(1, 10, 1), (2, 10, 3)], "a clear mp4a first and a cenc-protected enca second, whose chunks use the first and then an index (3) past both"),
    ]:
        samples = ciphertext(30)
        track = Track(1, samples, 44100, 2, first, stsc_box=stsc(stsc_runs), more_entries=[second])
        data, records, offsets = movie([track])
        findings = [SAMPLES_ENCRYPTED] + ([["mp4.sample-description-index", "sample description 3"]] if "out-of-range" in name else [])
        out.append((name, data, manifest(
            f"A track with {text}: the samples are encrypted, though a clear entry is used too, and are never read as AAC.", data, records,
            [dict(coded_stream("AAC LC", 44100, 2, "Stereo", sum(track.durations)), protection=protection(SCHEMES[b"cenc"], True))],
            sample_rows(offsets[0], samples, track.chunk), {STSD + "/enca": "enca (encrypted audio)"}, findings,
            crosscheck={"ffprobe": False, "mp4box": True, "faad": None, "encryption": {"ffprobe": None, "mediainfo": "Encrypted"},
                        "skip": {"faad": "faad2 reads the first sample entry.", "ffprobe": "ffmpeg reads the first sample entry and its packets as AAC."}})))
    return out


def ciphertext_crosscheck(coding):
    """The tools read the sample entry's figures of a coding whose frames are ciphertext, but not its packets: ffmpeg's parser for it merges random bytes until it finds
    a sync word, and ffprobe attaches the encryption info to the packets only of a track whose tenc box protects by default and that carries senc boxes."""
    return {"ffprobe": True, "ffprobe_packets": False, "ffprobe_streams": False, "mp4box": True, "faad": None, "encryption": {"ffprobe": None, "mediainfo": "Encrypted"},
            "skip": {"faad": "faad2 reads AAC only.", "ffprobe_packets": f"ffmpeg's {coding} parser merges the random bytes of the samples into packets of its own.",
                     "ffprobe_streams": f"the random bytes give ffmpeg's {coding} parser no layout that the sample entry does not, so only the packets' side data is not compared."}}


def other_codecs():
    out = []
    frames = ciphertext(12, base=500, spread=40)
    dac3 = m.silent_dac3(0, 8, 0, 2, 0, 10)
    ac3 = Box(b"enca", mp4a(2, 48000).payload, [Box(b"dac3", dac3), sinf(frma(b"ac-3"), *cenc_sinf())])
    out.append(encrypted(
        "cenc-ac3", "AC-3 in an enca entry: the sinf box names ac-3, its dac3 box gives the 2/0 stream at 192 kbps, and the samples are pseudo-random bytes. The "
        "analyzer reads the configuration and no frames.", ac3, samples=frames, rate=48000, durations=[1536] * 12, codec="AC-3", scheme_name=SCHEMES[b"cenc"],
        mode=None, layout=False, crosscheck=ciphertext_crosscheck("AC-3")))
    eac3 = Box(b"enca", mp4a(2, 48000).payload, [Box(b"dec3", m.dec3(192, [(0, 16, 0, 0, 2, 0, 0, 0)])), sinf(frma(b"ec-3"), *cenc_sinf())])
    out.append(encrypted(
        "cenc-eac3", "E-AC-3 in an enca entry: the sinf box names ec-3 and the dec3 box gives one 2/0 independent substream; the samples are pseudo-random bytes.",
        eac3, samples=frames, rate=48000, durations=[1536] * 12, codec="E-AC-3", scheme_name=SCHEMES[b"cenc"], mode=None, layout=False, crosscheck=ciphertext_crosscheck("E-AC-3")))
    dts = Box(b"enca", mp4a(2, 48000).payload, [Box(b"ddts", dts_model.ddts(48000, 384000, 384000, duration=0, construction=1, core_layout=2, core_size=512)),
                                                sinf(frma(b"dtsc"), *cenc_sinf())])
    out.append(encrypted(
        "cenc-dts", "DTS in an enca entry: the sinf box names dtsc and the ddts box gives the stream; the samples are pseudo-random bytes.",
        dts, samples=frames, rate=48000, durations=[512] * 12, codec="DTS", scheme_name=SCHEMES[b"cenc"], mode=None, layout=False, crosscheck=ciphertext_crosscheck("DTS")))
    mp3 = ciphertext(12, base=417, spread=1)
    entry = mp4a(2, 44100, esds(0x6B, None, 128000, 128000), sinf(frma(b"mp4a"), *cenc_sinf()), format=b"enca")
    out.append(encrypted(
        "cenc-mpeg-audio", "MPEG-1 audio (object type 0x6B) in an enca entry: no configuration exists, so the stream is described from the sample entry alone.",
        entry, samples=mp3, durations=[1152] * 12, codec="MPEG-1 Audio", scheme_name=SCHEMES[b"cenc"], mode=None, layout=False, crosscheck=ciphertext_crosscheck("MPEG audio")))
    return out


def itunes():
    """A drms entry as protected iTunes files carry it: esds and a sinf box whose schi holds the DRM atoms user, key, iviv and priv."""
    atoms = schi(Box(b"user", bytes(range(16))), Box(b"key ", bytes(range(32, 48))), Box(b"iviv", bytes(range(64, 80))), Box(b"priv", bytes(range(100, 160))))
    entry = mp4a(2, 44100, esds(0x40, LC, 0, 0), sinf(frma(b"mp4a"), atoms), format=b"drms")
    items = [
        text_item(b"\xa9nam", "Purchased title"), text_item(b"\xa9ART", "Purchased artist"),
        item(b"apID", data_atom(1, b"buyer@example.invalid")), item(b"purd", data_atom(1, b"2009-03-14 15:09:26")),
        item(b"ownr", data_atom(1, b"Test Owner")), item(b"cnID", data_atom(21, u32(123456))),
    ]
    name, data, result = encrypted(
        "itunes-drms", "A drms sample entry in an M4P file, as purchases from iTunes carry it: esds, and a sinf box with the original format mp4a and a "
        "schi box that holds the DRM atoms user, key, iviv and priv. The iTunes metadata of the purchase (apID, purd, ownr, cnID) is metadata, not protection.",
        entry, scheme_name="iTunes protection (drms)", extra_moov=[udta(ilst_meta(*items))], brand=ftyp(b"M4P ", 0, (b"M4P ", b"mp42", b"isom")),
        fields={"MP4/moov/trak/mdia/minf/stbl/stsd/drms": "drms (iTunes protected audio)",
                "MP4/moov/trak/mdia/minf/stbl/stsd/drms/sinf/schi/key ": "", "MP4/ftyp": "M4P (Apple protected audio)",
                "MP4/moov/udta/meta/iTunes Metadata/Account/data/Value": "buyer@example.invalid", "MP4/moov/udta/meta/iTunes Metadata/Owner/data/Value": "Test Owner"},
        crosscheck={"ffprobe": False, "mp4box": False, "faad": None, "encryption": {"mediainfo": "iTunes"},
                    "skip": {"ffprobe": "ffmpeg 6.1 does not read a drms entry (it lists no audio stream).", "mp4box": "MP4Box lists the atoms of schi in its own way.",
                             "faad": "faad2 rejects the protected sample entry."}})
    result["fields"].pop("MP4/moov/trak/mdia/minf/stbl/stsd/drms/sinf/schi/key ")
    return [(name, data, result)]


def audible():
    adrm = Box(b"adrm", bytes(range(8)) + bytes(range(100, 156)) + bytes(4) + bytes(range(200, 220)))
    entry = mp4a(2, 44100, esds(0x40, LC, 0, 0), adrm, format=b"aavd")
    return [encrypted(
        "audible-aavd", "An aavd sample entry as Audible's AAX files carry it (GStreamer's qtdemux reads it with the layout of mp4a): an esds box and an adrm box of 88 "
        "bytes, which FFmpeg's mov_read_adrm splits into 8 bytes, a 56-byte blob, 4 bytes and 20 bytes of checksum. The ftyp brand is aax.",
        entry, scheme_name="Audible (aavd)", brand=ftyp(b"aax ", 0, (b"aax ", b"isom", b"mp42")),
        fields={"MP4/moov/trak/mdia/minf/stbl/stsd/aavd": "aavd (Audible protected audio)", "MP4/moov/trak/mdia/minf/stbl/stsd/aavd/adrm/DRM Blob": "56 bytes",
                "MP4/moov/trak/mdia/minf/stbl/stsd/aavd/adrm/File Checksum": "20 bytes"},
        crosscheck={"ffprobe": True, "mp4box": False, "faad": None, "encryption": {"ffprobe": False, "mediainfo": None},
                    "skip": {"mp4box": "MP4Box lists an unknown sample entry as a box of its own without its children.", "faad": "faad2 rejects the protected sample entry."}})]


def purchase_on_clear_file():
    samples = units("stereo", 20)
    items = [
        text_item(b"\xa9nam", "Bought title"), text_item(b"\xa9ART", "Bought artist"),
        item(b"apID", data_atom(1, b"buyer@example.invalid")), item(b"purd", data_atom(1, b"2012-07-04 09:00:00")),
        item(b"ownr", data_atom(1, b"Test Owner")), item(b"cnID", data_atom(22, u32(4000000000))), item(b"atID", data_atom(22, u32(99))),
        item(b"plID", data_atom(21, struct.pack(">q", 5000000000))), item(b"geID", data_atom(22, u32(5))), item(b"sfID", data_atom(22, u32(143441))),
        item(b"akID", data_atom(21, u8(0))),
    ]
    track = Track(1, samples, 44100, 2, mp4a(2, 44100, esds(0x40, LC, 0, 0)))
    data, records, offsets = movie([track], moov_extra=[udta(ilst_meta(*items))])
    return [("itunes-purchase-clear", data, manifest(
        "An unprotected M4A file with the metadata of an iTunes Store purchase: the account (apID), purchase date (purd), owner (ownr) and the catalog, artist, album, genre "
        "and storefront IDs. They are tags of the file, read as metadata; the stream is not protected.", data, records,
        [aac_stream("AAC LC", 44100, 2, "Stereo", 20 * 1024, vbr=True)], sample_rows(offsets[0], samples, 10),
        {"MP4/moov/udta/meta/iTunes Metadata": "11 items", "MP4/moov/udta/meta/iTunes Metadata/Account/data/Value": "buyer@example.invalid",
         "MP4/moov/udta/meta/iTunes Metadata/Purchase Date/data/Value": "2012-07-04 09:00:00", "MP4/moov/udta/meta/iTunes Metadata/Owner/data/Value": "Test Owner"},
        [], units_rows=unit_list(offsets[0], samples, 10),
        crosscheck={"ffprobe": True, "mp4box": True, "faad": None, "encryption": {"ffprobe": False, "mediainfo": None},
                    "skip": {"faad": "faad2's reader needs no checks of the tags."}}))]


def fragmented():
    """moov with an empty enca track, a pssh box and mvex, then two moof and mdat pairs whose track fragments carry senc, saiz and saio."""
    entry = encrypted_aac_entry(*cenc_sinf())
    stbl = box("stbl", b"", stsd(entry), stts([]), stsc([]), stsz([]), stco([]))
    trak = box("trak", b"", tkhd(1, 0), box("mdia", b"", mdhd(44100, 0), hdlr(b"soun"), box("minf", b"", fullbox("smhd", 0, 0, bytes(4)), dinf(), stbl)))
    mvex = box("mvex", b"", fullbox("trex", 0, 0, u32(1) + u32(1) + u32(1024) + u32(0) + u32(0)))
    moov = box("moov", b"", mvhd(1000, 0), trak, mvex, pssh(WIDEVINE, b"widevine test data"))
    head = [ftyp(b"iso6", 0, (b"iso6", b"dash", b"mp41")), moov]
    samples = ciphertext(20)
    boxes = list(head)
    rows = []

    def fragment(sequence, chunk, decode_time, offset):
        ivs = b"".join(bytes([sequence, i]) * 4 for i in range(len(chunk)))

        def build(data_offset):
            sizes = b"".join(u32(len(s)) for s in chunk)
            trun = fullbox("trun", 0, 0x201, u32(len(chunk)) + struct.pack(">i", data_offset) + sizes)
            traf = box("traf", b"", fullbox("tfhd", 0, 0x20000, u32(1)), fullbox("tfdt", 1, 0, u64(decode_time)), saiz(8, len(chunk), aux_type=b"cenc"),
                       saio([0], aux_type=b"cenc"), senc(len(chunk), ivs), trun)
            return box("moof", b"", fullbox("mfhd", 0, 0, u32(sequence)), traf)

        moof_bytes, _ = write([build(0)], offset)
        moof = build(len(moof_bytes) + 8)
        return [moof, box("mdat", b"".join(chunk))]

    decode = 0
    for sequence, start in enumerate(range(0, 20, 10)):
        offset = len(write(boxes)[0])
        chunk = samples[start:start + 10]
        moof, mdat = fragment(sequence + 1, chunk, decode, offset)
        boxes += [moof, mdat]
        position = offset + len(write([moof], offset)[0]) + 8
        for sample in chunk:
            rows.append([position, len(sample)])
            position += len(sample)
        decode += len(chunk) * 1024
    data, records = write(boxes)
    stream = dict(aac_stream("AAC LC", 44100, 2, "Stereo", 0, vbr=True), protection=protection(SCHEMES[b"cenc"], True, [WIDEVINE]))
    return [("cenc-fragmented", data, manifest(
        "A fragmented MP4 of cenc-protected AAC: moov with an empty enca track and a pssh box, then two moof and mdat pairs whose track fragments carry saiz, saio and senc "
        "boxes (cenc auxiliary information with 8-byte IVs) next to their track runs. The track header states no duration, so the stream has none.", data, records,
        [stream], rows, {"MP4/moof/traf/senc": "10 samples", "MP4/moof/traf/saiz": "10 samples", "MP4/moof/traf/saiz/Default Sample Info Size": "8 bytes",
                         "MP4/moof/traf/senc/UsesSubsampleEncryption": "false", "MP4/moov/pssh": "Widevine Content Protection"},
        [SAMPLES_ENCRYPTED], crosscheck={"ffprobe": True, "mp4box": True, "faad": None, "encryption": {"ffprobe": True, "mediainfo": "Encrypted"},
                                         "skip": {"faad": "faad2's MP4 reader reads samples from the sample tables only, which a fragmented file leaves empty.",
                                                  "mediainfo_duration": "a fragmented file's track header states no duration"}}))]


def corrupt():
    out = []
    base = encrypted_aac_entry
    skip_all = {"ffprobe": False, "mp4box": False, "faad": None, "encryption": {"ffprobe": None, "mediainfo": "Encrypted"},
                "skip": {"all": "a damaged file, which tools read in their own ways"}}

    def variant(name, description, entry, findings, fields=None, codec="AAC LC", scheme=SCHEMES[b"cenc"], extra_moov=(), mutate=None, systems=(), mediainfo="Encrypted", no_stream=False):
        samples = ciphertext(30)
        track = Track(1, samples, 44100, 2, entry)
        data, records, offsets = movie([track], moov_extra=list(extra_moov))
        if mutate:
            data = mutate(data, records)
        # An entry whose coding is unknown is still a stream, named by its sample entry, with no channel mode or layout.
        stream_entry = aac_stream(codec, 44100, 2, "Stereo", 30 * 1024, vbr=True) if codec == "AAC LC" else coded_stream(codec, 44100, 2, None, 30 * 1024, layout=False, vbr=False)
        result = manifest(description, data, records, [] if no_stream else [dict(stream_entry, protection=protection(scheme, True, systems))],
                          sample_rows(offsets[0], samples, 10), fields or {}, findings,
                          crosscheck=dict(skip_all, encryption={"ffprobe": None, "mediainfo": mediainfo}))
        out.append((name, data, result))
        return data

    variant("enca-no-sinf", "An enca sample entry without a sinf box: the scheme and the original format are not stated, so the track's coding is unknown; it is a stream of the sample entry's kind, and its samples are not analyzed.",
            mp4a(2, 44100, esds(0x40, LC, 0, 0), format=b"enca"), [["mp4.protected-entry-no-sinf", "has no sinf box"], ["mp4.codec-not-analyzed", "protected enca"], SAMPLES_ENCRYPTED],
            codec="encrypted audio", scheme="protection scheme not stated")
    variant("sinf-no-frma", "A sinf box with a scheme and tenc but no frma box: the original format is unknown, so the track's coding is unknown and its samples are not analyzed.",
            mp4a(2, 44100, esds(0x40, LC, 0, 0), sinf(*cenc_sinf()), format=b"enca"), [["mp4.protected-entry-no-frma", "has no frma box"], ["mp4.codec-not-analyzed", "protected enca"], SAMPLES_ENCRYPTED],
            codec="encrypted audio")
    variant("sinf-no-schm", "A sinf box with frma and a schi box holding tenc, but no schm box: the scheme is not stated; the samples are taken as encrypted.",
            mp4a(2, 44100, esds(0x40, LC, 0, 0), sinf(frma(b"mp4a"), schi(tenc())), format=b"enca"), [["mp4.protected-entry-no-schm", "has no schm box"], SAMPLES_ENCRYPTED],
            scheme="protection scheme not stated")
    variant("cenc-no-tenc", "A cenc sinf box without tenc: the track's default encryption is not stated, so its samples are taken as encrypted.",
            base(schm(b"cenc")), [["mp4.protected-entry-no-tenc", "has no tenc box"], SAMPLES_ENCRYPTED])
    variant("tenc-iv-size", "A tenc box with a per-sample IV size of 3 bytes, where 0, 8 and 16 are valid.", base(schm(b"cenc"), schi(tenc(iv_size=3))),
            [["mp4.tenc-iv-size", "size of 3 bytes"], SAMPLES_ENCRYPTED])
    variant("tenc-no-constant-iv", "A protected tenc box with no IV per sample and no constant IV.", base(schm(b"cenc"), schi(tenc(iv_size=0))),
            [["mp4.tenc-constant-iv", "holds no constant one"], SAMPLES_ENCRYPTED])
    variant("tenc-constant-iv-size", "A tenc box whose constant IV is 12 bytes long, where 8 and 16 are valid.",
            base(schm(b"cenc"), schi(tenc(iv_size=0, constant_iv=bytes(12)))), [["mp4.tenc-constant-iv", "constant initialization vector of 12 bytes"], SAMPLES_ENCRYPTED])
    truncated_tenc = Box(b"tenc", b"\0\0\0\0" + bytes(2) + bytes([1, 8]) + bytes(5))
    variant("tenc-truncated", "A tenc box that ends inside the KID: the box is reported cut short and the samples are taken as encrypted by default.",
            base(schm(b"cenc"), schi(truncated_tenc)), [["mp4.box-fields-truncated", "ends inside a field"], ["mp4.protected-entry-no-tenc", "has no tenc box"], SAMPLES_ENCRYPTED])
    variant("schm-truncated", "A schm box that ends inside the scheme version: the box is reported cut short; the scheme is not stated.",
            base(Box(b"schm", b"\0\0\0\0cen"), schi(tenc())), [["mp4.box-fields-truncated", "ends inside a field"], ["mp4.protected-entry-no-schm", "has no schm box"], SAMPLES_ENCRYPTED],
            scheme="protection scheme not stated")
    variant("pssh-data-overrun", "A pssh box that declares 4,000 bytes of data and holds 6.", base(*cenc_sinf()),
            [["mp4.pssh-data-exceeds-box", "declares 4000 bytes of data"], SAMPLES_ENCRYPTED],
            extra_moov=[fullbox("pssh", 0, 0, bytes.fromhex(WIDEVINE.replace("-", "")) + u32(4000) + b"abcdef")], systems=[WIDEVINE])
    variant("pssh-kids-overrun", "A version 1 pssh box that declares 1,000 key IDs and holds room for one.", base(*cenc_sinf()),
            [["mp4.pssh-kids-exceed-box", "declares 1000 key IDs"], SAMPLES_ENCRYPTED],
            extra_moov=[fullbox("pssh", 1, 0, bytes.fromhex(PLAYREADY.replace("-", "")) + u32(1000) + KID + u32(0))], systems=[PLAYREADY])
    variant("pssh-version-2", "A pssh box of version 2, which is not defined: its fields after the system ID are not read.", base(*cenc_sinf()),
            [["mp4.pssh-version", "has version 2"], SAMPLES_ENCRYPTED],
            extra_moov=[fullbox("pssh", 2, 0, bytes.fromhex(WIDEVINE.replace("-", "")) + u32(0))], systems=[WIDEVINE])
    variant("saio-count-exceeds", "A saio box that declares 5,000 offsets and holds two.", base(*cenc_sinf()),
            [["mp4.entries-exceed-box", "declares 5000 offsets, but holds room for 2"], SAMPLES_ENCRYPTED],
            extra_moov=[fullbox("saio", 0, 0, u32(5000) + u32(0x100) + u32(0x200))])
    variant("pssh-trailing-bytes", "A pssh box whose data is shorter than the bytes after the size field: the bytes after the data are shown as extra data.", base(*cenc_sinf()),
            [SAMPLES_ENCRYPTED], extra_moov=[fullbox("pssh", 0, 0, bytes.fromhex(WIDEVINE.replace("-", "")) + u32(3) + b"abcdefgh")], systems=[WIDEVINE],
            fields={"MP4/moov/pssh/Data Size": "3 bytes", "MP4/moov/pssh/Extra Data": "5 bytes"})

    def grow_sinf(data, records):
        at = next(offset for path, offset, size in records if path.endswith("/sinf"))
        return data[:at] + struct.pack(">I", struct.unpack(">I", data[at:at + 4])[0] + 2000) + data[at + 4:]

    variant("sinf-overruns-parent", "A sinf box that states 2,000 bytes more than its parent, the enca entry, holds: it is cut at the entry's end, with a warning.",
            base(*cenc_sinf()), [["mp4.box-overruns-parent", "2000 more than its parent"], SAMPLES_ENCRYPTED], mutate=grow_sinf)

    def cut_in_tenc(data, records):
        at = next(offset for path, offset, size in records if path.endswith("/tenc"))
        return data[:at + 20]

    variant("truncated-in-tenc", "A file cut in the middle of the tenc box: the boxes that hold it are cut at the end of the file, and tenc ends inside a field.",
            base(*cenc_sinf()),
            [["mp4.truncated", "The moov box"]] + [["mp4.box-overruns-parent", f"The {kind} box"] for kind in ("trak", "mdia", "minf", "stbl", "stsd", "enca", "sinf", "schi", "tenc")]
            + [["mp4.missing-" + kind, "The stbl box"] for kind in ("stts", "stsc", "stsz", "stco")]
            + [["mp4.box-fields-truncated", "The tenc box"], ["mp4.protected-entry-no-tenc", "has no tenc box"]], mutate=cut_in_tenc, no_stream=True)
    entry = mp4a(2, 44100, esds(0x40, LC, 0, 0), Box(b"adrm", bytes(40)), format=b"aavd")
    variant("adrm-short", "An adrm box of 40 bytes in an aavd entry, where FFmpeg's reader takes 88: its fields are not read.", entry,
            [["mp4.adrm-short", "fewer than the 88"], SAMPLES_ENCRYPTED], scheme="Audible (aavd)", mediainfo=None)
    return out


def main():
    fixtures = common_encryption() + clear_protected() + sample_groups() + other_codecs() + fragmented() + itunes() + fragmented_groups() + sample_group_defaults() + encrypted_groups() + unused_entry_run() + two_entries() + audible() + purchase_on_clear_file() + corrupt()
    for name, data, result in fixtures:
        extension = ".mp4" if name.startswith("cenc-fragmented") else ".m4a"
        MP4.mkdir(parents=True, exist_ok=True)
        (MP4 / (name + extension)).write_bytes(data)
        (MP4 / (name + ".expected.json")).write_text(json.dumps(result, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{len(fixtures)} protection fixtures in {MP4}")


if __name__ == "__main__":
    sys.exit(main())
