#!/usr/bin/env python3
"""Writes the FLAC, Dolby TrueHD and PCM fixtures that ffmpeg's MP4 and QuickTime muxers make, into tests/fixtures/mp4 (external-ffmpeg-*).

ffmpeg 6.1 (libavformat's mov muxer, movenc.c; the ffmpeg package of Ubuntu 24.04) writes, from a few hundredths of a second of a sine tone made by its aevalsrc source:

- FLAC as an `fLaC` sample entry with a `dfLa` box, one frame per sample (`-c:a flac -strict -2`);
- Dolby TrueHD as an `mlpa` sample entry with a `dmlp` box, one access unit per sample (`-c:a truehd -strict -2`);
- PCM in MP4 as `ipcm` (integers) and `fpcm` (floats) with a `pcmC` box, and in QuickTime files (.mov) as `sowt`, `twos`, `in24`, `in32`, `fl32`, `fl64`, `raw ` (8-bit
  unsigned), `alaw` and `ulaw` entries, the 24-bit and wider ones with a `wave` box that holds the `enda` box.

Each <name>.expected.json manifest has the command, the tool's version and what the analyzer must report, worked out from independent readings of the file:

- "boxes": the boxes MP4Box -diso lists, as path and size (the offset is null: MP4Box gives none).
- "units": the samples of the coded streams (FLAC and Dolby TrueHD) as offset and size, from the sample tables read here (stsz, stsc and stco) and compared with the packets ffprobe
  lists. PCM has no unit nodes, so its list is empty; its samples are counted from the bytes.
- "streams": the codec, rate, channels and layout ffprobe reports, and the samples the media header's duration gives.
- "pcm": what ffmpeg decodes the file to, as the MD5 of the canonical layout the analyzer's decoder outputs (ffmpeg -c:a pcm_<format> -f md5), and the samples.
- "fields": the values MP4Box reads from the movie, track and media headers.
- "findings" and "crosscheck" as in make_mp4_fixtures.py.

    python3 scripts/fixtures/make_external_mp4_fixtures.py [name ...]
    python3 scripts/fixtures/make_external_mp4_fixtures.py --manifests-only [name ...]

The second form rewrites the manifests from the committed files without ffmpeg. Needs ffmpeg 6.1, MP4Box (gpac 2.2) and ffprobe for the manifests:

    apt-get install ffmpeg gpac
"""

import json
import pathlib
import struct
import subprocess
import sys
import tempfile


OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "mp4"
# Provenance: the version (Ubuntu 24.04) that wrote the committed files. The cross-check jobs run newer tools and assert their own versions (tool_versions.py);
# the committed files and goldens stay as written, so this string stays as it is.
FFMPEG_VERSION = "ffmpeg version 6.1.1-3ubuntu5"
LAVF = "FFmpeg (Lavf60.16.100)"
MOOV_AFTER = ["mp4.moov-after-mdat", "not optimized for streaming"]

TWO = "aevalsrc=0.5*sin(2*PI*440*t)|0.3*sin(2*PI*660*t):s={rate}:d={seconds}:c=stereo"
ONE = "aevalsrc=0.5*sin(2*PI*440*t):s={rate}:d={seconds}:c=mono"
FIVE_ONE = ("aevalsrc=0.4*sin(2*PI*440*t)|0.4*sin(2*PI*550*t)|0.4*sin(2*PI*660*t)|0.2*sin(2*PI*80*t)|0.3*sin(2*PI*770*t)|0.3*sin(2*PI*880*t):s={rate}:d={seconds}:c=5.1")

# The speakers of the formats' own channel orders in the analyzer's words: FLAC's, Dolby TrueHD's and a chnl or chan box's six channels are the front pair, the center, the
# LFE and the back pair (ffmpeg names FLAC's and TrueHD's 5.1(side), which the cross-check does not compare).
SPEAKERS = {
    1: ["front center"],
    2: ["front left", "front right"],
    6: ["front left", "front right", "front center", "LFE", "back left", "back right"],
}
MODES = {1: "Mono", 2: "Stereo", 6: "Multichannel"}

# name: (extension, source, rate, seconds, ffmpeg's codec options, the codec as the analyzer names it, the layout of 6 channels where the entry states one, description)
FIXTURES = {
    "external-ffmpeg-flac-in-mp4": (".mp4", TWO, 22050, 0.5, ["-sample_fmt", "s16", "-c:a", "flac", "-strict", "-2"], "FLAC", None,
                                    "FLAC in MP4 as ffmpeg's muxer writes it: an fLaC sample entry with a dfLa box that holds the STREAMINFO block, one frame to a sample."),
    "external-ffmpeg-flac-24-bit-51-in-mp4": (".mp4", FIVE_ONE, 8000, 0.05, ["-sample_fmt", "s32", "-bits_per_raw_sample", "24", "-c:a", "flac", "-strict", "-2"], "FLAC", None,
                                              "24-bit FLAC of 5.1 channels in MP4 as ffmpeg's muxer writes it."),
    "external-ffmpeg-truehd-in-mp4": (".mp4", ONE, 48000, 0.1, ["-sample_fmt", "s32p", "-c:a", "truehd", "-strict", "-2"], "Dolby TrueHD", None,
                                      "Dolby TrueHD in MP4 as ffmpeg's muxer writes it: an mlpa sample entry whose sampling rate is a 32-bit integer, with a dmlp box, one access unit to a sample."),
    "external-ffmpeg-truehd-51-in-mp4": (".mp4", FIVE_ONE, 48000, 0.05, ["-sample_fmt", "s32p", "-c:a", "truehd", "-strict", "-2"], "Dolby TrueHD", None,
                                         "Dolby TrueHD of 5.1 channels in MP4 as ffmpeg's muxer writes it."),
    "external-ffmpeg-ipcm-16-in-mp4": (".mp4", TWO, 8000, 0.1, ["-c:a", "pcm_s16le", "-strict", "-2"], "PCM", None,
                                       "16-bit little-endian integer PCM in MP4 as ffmpeg's muxer writes it: an ipcm sample entry with a pcmC box."),
    "external-ffmpeg-ipcm-16-51-in-mp4": (".mp4", FIVE_ONE, 8000, 0.05, ["-c:a", "pcm_s16le", "-strict", "-2"], "PCM", None,
                                          "16-bit little-endian integer PCM of 5.1 channels in MP4: an ipcm sample entry with a pcmC box and a chnl box."),
    "external-ffmpeg-ipcm-24-in-mp4": (".mp4", TWO, 8000, 0.05, ["-c:a", "pcm_s24le", "-strict", "-2"], "PCM", None,
                                       "24-bit little-endian integer PCM in MP4 as ffmpeg 6.1.1's muxer writes it: the samples are 3 bytes long, and the pcmC box says 32 bits."),
    "external-ffmpeg-ipcm-32-big-endian-in-mp4": (".mp4", TWO, 8000, 0.1, ["-c:a", "pcm_s32be", "-strict", "-2"], "PCM", None,
                                                  "32-bit big-endian integer PCM in MP4: the pcmC box's format flags say big-endian."),
    "external-ffmpeg-fpcm-32-in-mp4": (".mp4", TWO, 8000, 0.1, ["-c:a", "pcm_f32le", "-strict", "-2"], "PCM (floating-point)", None,
                                       "32-bit floating-point PCM in MP4 as ffmpeg's muxer writes it: an fpcm sample entry with a pcmC box."),
    "external-ffmpeg-fpcm-64-big-endian-in-mp4": (".mp4", TWO, 8000, 0.1, ["-c:a", "pcm_f64be", "-strict", "-2"], "PCM (floating-point)", None,
                                                  "64-bit big-endian floating-point PCM in MP4."),
    "external-ffmpeg-sowt-in-quicktime": (".mov", TWO, 8000, 0.1, ["-c:a", "pcm_s16le"], "PCM", None,
                                          "16-bit little-endian PCM in a QuickTime file as ffmpeg's muxer writes it: a sowt sample entry."),
    "external-ffmpeg-sowt-51-in-quicktime": (".mov", FIVE_ONE, 8000, 0.05, ["-c:a", "pcm_s16le"], "PCM", None,
                                             "16-bit little-endian PCM of 5.1 channels in a QuickTime file: a sowt sample entry with a chan box."),
    "external-ffmpeg-twos-in-quicktime": (".mov", TWO, 8000, 0.1, ["-c:a", "pcm_s16be"], "PCM", None,
                                          "16-bit big-endian PCM in a QuickTime file: a twos sample entry."),
    "external-ffmpeg-in24-little-endian-in-quicktime": (".mov", TWO, 8000, 0.1, ["-c:a", "pcm_s24le"], "PCM", None,
                                                        "24-bit little-endian PCM in a QuickTime file: an in24 sample entry whose enda box says little-endian."),
    "external-ffmpeg-in24-big-endian-in-quicktime": (".mov", TWO, 8000, 0.1, ["-c:a", "pcm_s24be"], "PCM", None,
                                                     "24-bit big-endian PCM in a QuickTime file: an in24 sample entry whose enda box says big-endian."),
    "external-ffmpeg-in32-in-quicktime": (".mov", TWO, 8000, 0.1, ["-c:a", "pcm_s32le"], "PCM", None,
                                          "32-bit little-endian PCM in a QuickTime file: an in32 sample entry."),
    "external-ffmpeg-fl32-in-quicktime": (".mov", TWO, 8000, 0.1, ["-c:a", "pcm_f32le"], "PCM (floating-point)", None,
                                          "32-bit little-endian floating-point PCM in a QuickTime file: an fl32 sample entry."),
    "external-ffmpeg-fl64-big-endian-in-quicktime": (".mov", TWO, 8000, 0.1, ["-c:a", "pcm_f64be"], "PCM (floating-point)", None,
                                                     "64-bit big-endian floating-point PCM in a QuickTime file: an fl64 sample entry."),
    "external-ffmpeg-raw-in-quicktime": (".mov", ONE, 8000, 0.1, ["-c:a", "pcm_u8"], "PCM", None,
                                         "8-bit unsigned PCM in a QuickTime file: a raw sample entry."),
    "external-ffmpeg-alaw-in-quicktime": (".mov", ONE, 8000, 0.1, ["-c:a", "pcm_alaw"], "A-law", None,
                                          "A-law samples in a QuickTime file: an alaw sample entry."),
}
# ffmpeg 6.1.1 writes 24-bit samples in 3 bytes and a pcmC box of 32 bits (ffmpeg itself then reads the track as 32-bit samples): the analyzer follows the box and the stream's
# sample frames are 8 bytes of 2 channels, which the track's samples of 6 bytes do not fill (mp4.pcm-sample-size-differs).
SIZE_DIFFERS = {"external-ffmpeg-ipcm-24-in-mp4"}
# The codings whose samples are coded streams: their units are the samples.
CODED = {"FLAC", "Dolby TrueHD"}
MEDIAINFO_FORMAT_NOTE = "MediaInfo 24.01 names an fpcm entry by its code (\"fpcm\") and not as PCM; the cross-check compares the format of the integer PCM entries only."


def tables(data):
    """The sample offsets and sizes of the first track, from stsz, stsc and stco as ISO/IEC 14496-12 8.7 lays them out, the media header's timescale and duration, and the
    duration of each sample from stts."""
    def children(start, end):
        while start + 8 <= end:
            size, kind = struct.unpack(">I4s", data[start:start + 8])
            if size < 8:
                return
            yield kind.decode("latin1"), start, start + size
            start += size

    def find(start, end, path):
        for kind, begin, finish in children(start, end):
            if kind == path[0]:
                return find(begin + 8, finish, path[1:]) if len(path) > 1 else (begin, finish)
        return None

    trak = find(0, len(data), ["moov", "trak"])
    stbl = find(trak[0] + 8, trak[1], ["mdia", "minf", "stbl"])
    mdhd = find(trak[0] + 8, trak[1], ["mdia", "mdhd"])
    stsz = find(stbl[0] + 8, stbl[1], ["stsz"])
    stsc = find(stbl[0] + 8, stbl[1], ["stsc"])
    stco = find(stbl[0] + 8, stbl[1], ["stco"])
    uniform, count = struct.unpack(">II", data[stsz[0] + 12:stsz[0] + 20])
    sizes = [uniform] * count if uniform else list(struct.unpack(f">{count}I", data[stsz[0] + 20:stsz[0] + 20 + 4 * count]))
    runs = struct.unpack(f">{struct.unpack('>I', data[stsc[0] + 12:stsc[0] + 16])[0] * 3}I", data[stsc[0] + 16:stsc[1]])
    runs = [runs[i:i + 3] for i in range(0, len(runs), 3)]
    chunks = struct.unpack(f">{struct.unpack('>I', data[stco[0] + 12:stco[0] + 16])[0]}I", data[stco[0] + 16:stco[1]])
    units = []
    index = 0
    for number, offset in enumerate(chunks, 1):
        per = [r for r in runs if r[0] <= number][-1][1]
        for _ in range(per):
            if index >= len(sizes):
                break
            units.append([offset, sizes[index]])
            offset += sizes[index]
            index += 1
    timescale, duration = struct.unpack(">II", data[mdhd[0] + 20:mdhd[0] + 28])
    stts = find(stbl[0] + 8, stbl[1], ["stts"])
    entries = struct.unpack(f">{struct.unpack('>I', data[stts[0] + 12:stts[0] + 16])[0] * 2}I", data[stts[0] + 16:stts[1]])
    durations = [entries[i + 1] for i in range(0, len(entries), 2) for _ in range(entries[i])]
    return units, timescale, duration, durations


CONTAINERS = {"moov", "trak", "mdia", "minf", "stbl", "dinf", "edts", "udta", "wave", "ilst"}
ENTRIES = {"fLaC", "mlpa", "ipcm", "fpcm", "sowt", "twos", "in24", "in32", "fl32", "fl64", "raw ", "alaw", "ulaw"}


def boxes(data):
    """Every box with its offset and size, read from the file's own bytes: containers by their type, sample entries after their sound description (version 0: 28 bytes,
    QuickTime's version 1: 44, version 2: 64), and the full boxes stsd, dref and meta after their version, flags and counts. The paths are those MP4Box prints."""
    rows = []

    def walk(start, end, path, is_item_list=False):
        while start + 8 <= end:
            size, kind = struct.unpack(">I4s", data[start:start + 8])
            if size < 8 or start + size > end:
                return
            name = "".join(chr(b) if 0x20 <= b < 0x7F else "©" if b == 0xA9 else "." for b in kind)
            here = path + [name]
            rows.append(["/".join(here), start, size])
            body = start + 8
            if name in CONTAINERS:
                walk(body, start + size, here)
            elif name == "meta":
                walk(body + 4, start + size, here)
            elif name in ("stsd", "dref"):
                walk(body + 8, start + size, here)
            elif name in ENTRIES:
                version = struct.unpack(">H", data[body + 8:body + 10])[0]
                walk(body + 28 + {0: 0, 1: 16, 2: 36}[version], start + size, here)
            elif path and path[-1] == "ilst":
                walk(body, start + size, here)
            start += size

    walk(0, len(data), [])
    return rows


def canonical(options, name=None):
    """The PCM layout the analyzer's decoder outputs for a coding, as the ffmpeg codec that writes it, its bits and its sample format: the container's value as stored, A-law
    expanded to 16 bits, unsigned 8 bits as signed (docs/design/spectral-analysis.md, "WAV, Wave64 and AIFF: a PCM decoder"), and Dolby TrueHD as 24 bits."""
    codec = options[options.index("-c:a") + 1]
    if name in SIZE_DIFFERS:
        return "pcm_s32le", 32, "integer"
    if codec == "flac":
        return ("pcm_s24le", 24, "integer") if "24" in options else ("pcm_s16le", 16, "integer")
    if codec == "truehd":
        return "pcm_s24le", 24, "integer"
    kinds = {"pcm_s16le": ("pcm_s16le", 16, "integer"), "pcm_s16be": ("pcm_s16le", 16, "integer"), "pcm_s24le": ("pcm_s24le", 24, "integer"), "pcm_s24be": ("pcm_s24le", 24, "integer"),
             "pcm_s32le": ("pcm_s32le", 32, "integer"), "pcm_s32be": ("pcm_s32le", 32, "integer"), "pcm_f32le": ("pcm_f32le", 32, "float"), "pcm_f64be": ("pcm_f64le", 64, "float"),
             "pcm_u8": ("pcm_s8", 8, "integer"), "pcm_alaw": ("pcm_s16le", 16, "integer")}
    return kinds[codec]


def decoded(path, options, samples, name=None):
    """What ffmpeg decodes the file to, as the MD5 of the canonical layout (ffmpeg -c:a pcm_<format> -f md5), which the analyzer's decoder must give back."""
    codec, bits, kind = canonical(options, name)
    result = subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-map", "0:a:0", "-c:a", codec, "-f", "md5", "-"], capture_output=True, text=True, check=True)
    return {"samples": samples, "bits": bits, "format": kind, "md5": result.stdout.strip().removeprefix("MD5=")}


def flac_mode(frames):
    """The channel mode of FLAC frames of two channels from the channel assignment in each frame header (RFC 9639, section 9.1.3: 0 to 7 are independent channels, 8 to 10
    are left/side, side/right and mid/side): "Stereo" where all are independent, "Joint stereo" where none is, else "Stereo, joint in some frames"."""
    independent = sum(1 for f in frames if f[3] >> 4 < 8)
    return "Stereo" if independent == len(frames) else "Joint stereo" if independent == 0 else "Stereo, joint in some frames"


def make(name, extension, source, rate, seconds, codec_options, path):
    command = ["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", source.format(rate=rate, seconds=seconds), *codec_options, str(path)]
    subprocess.run(command, check=True, capture_output=True)
    return ["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", source.format(rate=rate, seconds=seconds), *codec_options, "{out}"]


def manifest_for(name, spec, path, command):
    from crosscheck_mp4_fixtures import ffprobe_packets, ffprobe_streams, mp4box_boxes, mp4box_fields

    extension, source, rate, seconds, options, codec, _, description = spec
    data = path.read_bytes()
    units, timescale, duration, _ = tables(data)
    probed = ffprobe_streams(path)[0]
    channels = int(probed["channels"])
    fields = mp4box_fields(path) if mp4box_boxes(path) is not None else {}
    coded = codec in CODED
    packets = ffprobe_packets(path)
    _, _, _, durations = tables(data)
    rates = {u[1] * 8 / d for u, d in zip(units, durations)}
    mode = flac_mode([data[o:o + 4] for o, _ in units]) if codec == "FLAC" and channels == 2 else MODES[channels]
    stream = {"codec": codec, "sample_rate": int(probed["sample_rate"]), "channels": channels, "channel_mode": mode, "channel_layout": SPEAKERS[channels],
              "samples": round(duration / timescale * int(probed["sample_rate"])), "is_vbr": coded and len(rates) > 1}
    if name in SIZE_DIFFERS:
        # The analyzer counts the bytes the samples hold in sample frames of the box's 32 bits: 2 channels of 4 bytes.
        stream["samples"] = sum(u[1] for u in units) // 8
    # ffmpeg's muxer names itself in a tool atom, which a codec parser that weighs writer claims takes as the writer; the Dolby TrueHD parser weighs none.
    if codec != "Dolby TrueHD":
        stream["writer"] = [LAVF, "Low"]
    crosscheck_skip = None
    if channels == 6 and codec in CODED:
        stream["ffprobe_channels"] = 6
        crosscheck_skip = "ffmpeg names the layout of six FLAC or Dolby TrueHD channels 5.1(side) (its decoders' maps); the analyzer follows the formats' own tables (back left and right)"
    crosscheck = {"ffprobe": True, "mp4box": True, "faad": None, "decode": True, "skip": {"faad": "faad2 reads AAC only."}}
    if name in SIZE_DIFFERS:
        crosscheck["decode"] = False
        crosscheck["skip"]["decode"] = "ffmpeg reads the 3-byte samples as the 32-bit samples the pcmC box states, so their packets are not whole sample frames and its decoder may say so"
    if name.startswith("external-ffmpeg-fpcm"):
        crosscheck["skip"]["mediainfo"] = ("MediaInfo 24.01 names an fpcm entry by its code and reads no channels from it; the version of the cross-check jobs (26.01) was not at hand "
                                           "when the fixtures were written, so this skip is to be checked against it")
    if crosscheck_skip:
        crosscheck["skip"]["ffprobe_layout"] = crosscheck_skip
    if mp4box_boxes(path) is None:
        crosscheck["mp4box"] = False
        crosscheck["skip"]["mp4box"] = "MP4Box (GPAC 2.2.1) refuses the file (\"Invalid IsoMedia File\") at the sample entry that holds a chnl box"
    if not coded:
        crosscheck["ffprobe_packets"] = False
        crosscheck["skip"]["ffprobe_packets"] = "ffmpeg's demuxer lists a PCM track's samples as packets of its own size, not as the sample table's one-frame samples"
    elif packets != units:
        sys.exit(f"{name}: ffprobe lists other packets than the sample tables")
    return {
        "description": description,
        "encoder": {"tool": f"{FFMPEG_VERSION} (ffmpeg package of Ubuntu 24.04)", "command": command},
        "length": len(data), "format": "MP4" if extension == ".mp4" else "MP4", "boxes": boxes(data),
        "streams": [stream], "units": units if coded else [], "fields": fields, "pcm": decoded(path, options, stream["samples"], name),
        "findings": [MOOV_AFTER] + ([["mp4.pcm-sample-size-differs", "are 6 bytes each"]] if name in SIZE_DIFFERS else []),
        "crosscheck": crosscheck,
    }


def main(arguments):
    only_manifests = arguments[:1] == ["--manifests-only"]
    chosen = [a for a in arguments if not a.startswith("--")]
    OUT.mkdir(parents=True, exist_ok=True)
    for name, spec in FIXTURES.items():
        if chosen and name not in chosen:
            continue
        path = OUT / (name + spec[0])
        if only_manifests:
            command = json.loads((OUT / (name + ".expected.json")).read_text(encoding="utf-8"))["encoder"]["command"]
        else:
            version = subprocess.run(["ffmpeg", "-version"], capture_output=True, text=True).stdout.splitlines()[0]
            if not version.startswith(FFMPEG_VERSION):
                sys.exit(f"{FFMPEG_VERSION} is required, found {version}")
            command = make(name, spec[0], spec[1], spec[2], spec[3], spec[4], path)
        (OUT / (name + ".expected.json")).write_text(json.dumps(manifest_for(name, spec, path, command), indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"{path.name}: {path.stat().st_size} bytes")


if __name__ == "__main__":
    main(sys.argv[1:])
