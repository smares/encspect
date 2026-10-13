#!/usr/bin/env python3
"""Checks the Ogg fixtures' manifests against independent tools.

For every manifest in tests/fixtures/ogg (make_ogg_fixtures.py, make_external_ogg_fixtures.py):

- oggz validate (oggz-tools): a fixture marked "valid", or with "oggz_validate": "ok", must pass
  without a word; one that lists messages must fail with each of them. oggz checks page order,
  beginning- and end-of-stream pages and granule positions, and libogg underneath it checks every
  CRC: a page that fails it is dropped before oggz sees it, so CRC damage shows in the packets
  below, not in validate's verdict.
- oggz info and oggz dump: each logical stream's page and packet counts, and the size of every
  packet, must be the manifest's.
- ogginfo (vorbis-tools): each Speex stream's version, mode, channels, rate, vendor string and
  comments.
- ffprobe: each Speex stream's sample rate and channels, its channel layout's speakers where ffmpeg
  names a layout (channel_layouts.py; ffmpeg 6.1 leaves Speex's unspecified), the size of every
  audio packet, and the duration in samples, which ffmpeg counts from the start the first granule position implies to
  the last granule position: the manifest's samples plus the encoder delay.
- speexdec -V (speex): the header line ("Decoding 16000 Hz audio using wideband mode (mono)"), the
  vendor string, the bitrate it prints for every frame it decodes, which follows from each frame's
  submodes (ogg_model.speexdec_bitrate), and the samples it writes (ogg_model.speexdec_samples).

For Vorbis and Opus streams (make_vorbis_opus_fixtures.py, make_external_vorbis_opus_fixtures.py):

- ogginfo and vorbiscomment -l: each Vorbis stream's version, channels, rate, nominal bitrate,
  vendor string and comments.
- opusinfo: each Opus stream's pre-skip, playback gain, channels, input sample rate, stream and
  coupled counts, vendor string and comments, the playback length, and whether it calls the
  stream hard CBR, which the manifest's IsVbr must contradict.
- ffprobe: codec, sample rate (48 kHz for Opus) and channels, the speakers of its channel layout
  where the stream names any (channel_layouts.py), the size of every audio packet, and
  for Opus each packet's duration, the samples its TOC gives (vorbis_opus_model.declared_samples),
  the last one's less the end trimming.
- oggdec and opusdec: the samples they write, after their start and end trimming, which must be
  the manifest's samples.

MediaInfo 24.01 (mediainfo_tool.py, check_mediainfo) is compared with every fixture's golden report:
each logical stream of the first chain link's codec, channels, sample rate (Opus: the input rate),
the duration of its last granule position, the encoder (Speex: the header's version, Vorbis and
Opus: the vendor string), Speex's header bitrate and mode, Vorbis's nominal bitrate, mode and floor
type, Opus's channel layout, and the Vorbis comments' title, artist, album and ENCODER. MediaInfo
reads no Speex comments (it misses the comment count), so those are skipped with that reason.

Fixtures that are not "valid" say which of these still apply and why the others do not, and
valid ones list in "skip" the checks a tool cannot make; every check that is skipped prints its
reason. Disagreements fail the run.
"""

import json
import math
import pathlib
import re
import subprocess
import sys
import tempfile

import channel_layouts
import mediainfo_tool as mi
import ogg_model as m
import vorbis_opus_model as vo

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "ogg"
SKIPS = []


def skip(name, check, reason):
    SKIPS.append(f"{name}: {check} skipped: {reason}")


# The tools print comments in the locale's encoding.
ENVIRONMENT = {**__import__("os").environ, "LC_ALL": "C.UTF-8"}


def run(arguments):
    return subprocess.run(arguments, capture_output=True, env=ENVIRONMENT)


def oggz_validate(path):
    result = run(["oggz", "validate", str(path)])
    return result.returncode, (result.stdout + result.stderr).decode("utf-8", "replace")


def oggz_streams(path):
    """Per serial number: (packets, pages) from oggz info, and the packet sizes from oggz dump."""
    info = run(["oggz", "info", str(path)]).stdout.decode()
    counts = {}
    for serial, packets, pages in re.findall(r"serialno (\d+)\n\t(\d+) packets in (\d+) pages", info):
        counts[int(serial) % (1 << 32)] = (int(packets), int(pages))
    sizes = {}
    # oggz prints sizes of 1024 bytes and more in kB, to three decimals.
    for serial, size, unit in re.findall(r"serialno (\d+), .*?: ([\d.]+) (bytes?|kB)$", run(["oggz", "dump", str(path)]).stdout.decode(), re.M):
        sizes.setdefault(int(serial) % (1 << 32), []).append(round(float(size) * 1024) if unit == "kB" else int(size))
    return counts, sizes


def ogginfo(path):
    """The fields ogginfo prints for each Speex stream when it reads its header, by serial number,
    and the vendor string and comments it prints when it reads a comment packet, in file order."""
    lines = run(["ogginfo", str(path)]).stdout.decode("utf-8", "replace").splitlines()
    streams, comments, serial, in_comments = {}, [], None, False
    for line in lines:
        if line.startswith("New logical stream"):
            serial = int(line.split("serial: ")[1][:8], 16)
            streams[serial] = {}
        elif line.startswith("Vendor: "):
            comments.append([line[len("Vendor: "):], []])
            in_comments = False
        elif line == "User comments section follows...":
            in_comments = True
        elif in_comments and line.startswith("\t"):
            comments[-1][1].append(line[1:])
        else:
            in_comments = False
            if serial is not None and ": " in line and line.split(": ")[0] in ("Version", "Mode", "Channels", "Rate"):
                streams[serial][line.split(": ")[0]] = line.split(": ", 1)[1]
    return streams, comments


def ffprobe(path):
    result = run(["ffprobe", "-v", "error", "-show_streams", "-show_packets", "-show_format", "-of", "json", str(path)])
    try:
        return json.loads(result.stdout or b"{}")
    except json.JSONDecodeError:
        return {}


def speexdec(path, channels):
    with tempfile.TemporaryDirectory() as work:
        out = pathlib.Path(work) / "out.raw"
        result = run(["speexdec", "-V", str(path), str(out)])
        text = result.stderr.decode("utf-8", "replace").replace("\r", "\n")
        samples = out.stat().st_size // (2 * channels) if out.exists() else None
    bitrates = [int(b) for b in re.findall(r"Bitrate is use: (\d+) bps", text)]
    header = re.search(r"Decoding (\d+) Hz audio using (\S+) (?:\(.*?\) )?mode \((mono|stereo)", text)
    return samples, bitrates, header.groups() if header else None, text


def number(text):
    return int(text.split()[0])


def speex_streams(manifest):
    return [s for s in manifest["streams"] if "speex" in s]


def check_packets(name, manifest, problems, pages=True):
    counts, sizes = oggz_streams(DIRECTORY / name)
    for stream in manifest["streams"]:
        serial = int(stream["serial"], 16)
        if "opus" in stream:
            skip(name, f"oggz info and dump of {stream['serial']}", "oggz 1.1.1 does not know Opus and lists only a stream's first two packets; ffprobe's packets are compared where it reads the stream")
            continue
        if counts.get(serial) is None:
            problems.append(f"oggz info lists no stream {stream['serial']}")
            continue
        # Pages that fail their CRC or are cut short never reach oggz.
        if counts[serial][0] != stream["packets"] or (pages and counts[serial][1] != stream["pages"]):
            problems.append(f"oggz info: {stream['serial']} has {counts[serial][0]} packets in {counts[serial][1]} pages, the manifest {stream['packets']} in {stream['pages']}")
        for codec in ("speex", "vorbis", "opus"):
            if codec in stream:
                audio = [p[1] for p in stream[codec]["packets"]]
                if audio and sizes.get(serial, [])[-len(audio):] != audio:
                    problems.append(f"oggz dump: the audio packet sizes of {stream['serial']} differ from the manifest's")


def check_ogginfo(name, manifest, problems):
    streams, comments = ogginfo(DIRECTORY / name)
    expected = speex_streams(manifest)
    # Comment packets come in the order of the streams' beginning-of-stream pages in every fixture.
    speex_comments = comments[:len(expected)]
    for stream, (vendor, texts) in zip(expected, speex_comments + [[None, None]] * (len(expected) - len(speex_comments))):
        fields = streams.get(int(stream["serial"], 16), {})
        mode, rate, channels = header_fields(name, manifest, stream)
        header = stream["speex"]["header"]
        wanted = {"Version": f"{header['Version ID']} ({header['Speex Version']})", "Mode": f"{mode} ({header['Mode'].lower()})",
                  "Channels": header["Channels"], "Rate": header["Sample Rate"].split()[0]}
        for key, value in wanted.items():
            if fields.get(key) != value:
                problems.append(f"ogginfo: {stream['serial']} {key} is {fields.get(key)!r}, the manifest's {value!r}")
        if vendor != stream["speex"]["comment"]["vendor"] or texts != stream["speex"]["comment"]["comments"]:
            problems.append(f"ogginfo: {stream['serial']} has the vendor {vendor!r} and comments {texts}, the manifest {stream['speex']['comment']}")


def ogg_pages(name, manifest):
    """The pages of the fixture's Ogg region."""
    data = (DIRECTORY / name).read_bytes()
    region = next(r for r in manifest["regions"] if r["name"] == "Ogg")
    return data, m.read_pages(data, region["offset"], region["offset"] + region["length"])[0]


def header_fields(name, manifest, stream):
    """Mode, rate and channels of a Speex stream, from its header packet in the file."""
    data, pages = ogg_pages(name, manifest)
    for p in pages:
        if p["serial"] == int(stream["serial"], 16) and p["flags"] & m.BOS:
            rate, mode, _, channels = m.struct.unpack_from("<4i", data, p["body"] + 36)
            return mode, rate, channels
    raise SystemExit(f"{name}: no header for {stream['serial']}")


def ffmpeg_start(name, manifest, stream):
    """Where ffmpeg starts a Speex stream (libavformat/oggparsespeex.c): the granule position of its
    first page that ends an audio packet less the samples of the audio packets up to it, and its
    duration, to the last granule position."""
    data, pages = ogg_pages(name, manifest)
    serial = int(stream["serial"], 16)
    mode, _, _ = header_fields(name, manifest, stream)
    nframes = max(1, m.struct.unpack_from("<i", data, next(p for p in pages if p["serial"] == serial)["body"] + 64)[0])
    own = [p for p in pages if p["serial"] == serial]
    packets, start, last = 0, None, None
    for p in own:
        packets += sum(1 for v in p["lacing"] if v < 255)
        audio = packets - 2
        if audio > 0 and p["granule"] != -1:
            if start is None:
                start = p["granule"] - audio * nframes * (160 << mode)
            last = p["granule"]
    return start, last - start


def check_ffprobe(name, manifest, problems, chained):
    probe = ffprobe(DIRECTORY / name)
    streams = [s for s in probe.get("streams", []) if s.get("codec_name") == "speex"]
    expected = speex_streams(manifest)
    if chained:
        # ffmpeg reads a chain as one stream with the first link's parameters, and hands each
        # later link's header and comment packets on as two more packets of it.
        skip(name, "ffprobe per-stream figures", "ffmpeg reads the links of a chain as one stream; only its packet count is compared")
        total = sum(len(s["speex"]["packets"]) for s in expected) + 2 * (len(expected) - 1)
        count = sum(1 for p in probe.get("packets", []) if p["stream_index"] == streams[0]["index"])
        if count != total:
            problems.append(f"ffprobe counts {count} packets in the chain, the manifest {total}")
        return
    if len(streams) != len(expected):
        problems.append(f"ffprobe finds {len(streams)} Speex streams, the manifest {len(expected)}")
        return
    for stream, entry in zip(streams, expected):
        info = next(i for i in manifest["stream_infos"] if i["sample_rate"] == int(stream["sample_rate"]) and i["codec"].startswith("Speex"))
        if int(stream["sample_rate"]) != info["sample_rate"] or stream["channels"] != info["channels"]:
            problems.append(f"ffprobe: {stream['sample_rate']} Hz, {stream['channels']} channels differ from the manifest's")
        if "channel_layout" in stream and channel_layouts.same_speakers(stream["channel_layout"], info["channel_layout"]) is not True:
            problems.append(f"ffprobe's channel layout {stream.get('channel_layout')} differs from the manifest's {info['channel_layout']}")
        sizes = [int(p["size"]) for p in probe["packets"] if p["stream_index"] == stream["index"]]
        if sizes != [p[1] for p in entry["speex"]["packets"]]:
            problems.append(f"ffprobe: the audio packet sizes of {entry['serial']} differ from the manifest's")
        if any(s["codec"] == "Skeleton" for s in manifest["streams"]):
            skip(name, "ffprobe start and duration", "ffmpeg takes the start time from the Skeleton stream's fisbone, not from the granule positions")
            continue
        start, duration = ffmpeg_start(name, manifest, entry)
        if stream.get("duration_ts") != duration or stream.get("start_pts") != start:
            problems.append(f"ffprobe: {entry['serial']} runs from {stream.get('start_pts')} for {stream.get('duration_ts')} samples, "
                            f"the pages give {start} and {duration}")


def check_speexdec(name, manifest, problems):
    entry = speex_streams(manifest)[0]
    mode, rate, channels = header_fields(name, manifest, entry)
    samples, bitrates, header, text = speexdec(DIRECTORY / name, min(max(channels, 1), 2))
    if header != (str(rate), m.MODE_NAMES[mode].lower(), "mono" if channels == 1 else "stereo"):
        problems.append(f"speexdec's header line {header} differs from the manifest's stream")
    vendor = entry["speex"]["comment"]["vendor"]
    if vendor and vendor not in text:
        problems.append(f"speexdec does not print the vendor string {vendor!r}")
    frames = [tuple([f[3], f[0], f[1], f[2]]) for packet in entry["speex"]["frames"] for f in packet]
    expected = [m.speexdec_bitrate(f, mode, rate) for f in frames]
    if bitrates != expected:
        problems.append(f"speexdec prints {len(bitrates)} frame bitrates {bitrates[:6]}..., the manifest's frames give {len(expected)} {expected[:6]}...")
    if samples != entry["speex"]["speexdec_samples"]:
        problems.append(f"speexdec writes {samples} samples, the manifest expects {entry['speex']['speexdec_samples']}")


def stream_pages(name, manifest, serial):
    """The packets of one stream of the fixture, as (bytes) in order."""
    data, pages = ogg_pages(name, manifest)
    streams = m.stream_packets(data, pages)
    return [p["data"] for p in streams[serial]["packets"]]


def wav_samples(path, channels):
    data = path.read_bytes() if path.exists() else b""
    at = data.find(b"data")
    return (len(data) - at - 8) // (2 * channels) if at >= 0 else None


def check_layout(name, stream, info, problems):
    """ffprobe's channel layout against the manifest's speakers, where both name one."""
    if "channel_layout" not in stream:
        return
    if not info["channel_layout"]:
        skip(name, "ffprobe channel layout", f"ffmpeg calls it {stream['channel_layout']}, where the stream names no speakers")
    elif channel_layouts.same_speakers(stream["channel_layout"], info["channel_layout"]) is not True:
        problems.append(f"ffprobe's channel layout {stream['channel_layout']} differs from the manifest's {info['channel_layout']}")


def check_vorbis(name, manifest, problems, skips):
    """ogginfo, vorbiscomment, ffprobe and oggdec on a file of one Vorbis stream."""
    stream = next(st for st in manifest["streams"] if "vorbis" in st)
    vorbis, info = stream["vorbis"], manifest["stream_infos"][0]
    header = vorbis["header"]
    path = DIRECTORY / name
    fields, comments = ogginfo(path)
    found = fields.get(int(stream["serial"], 16), {})
    for key, wanted in (("Version", header["Vorbis Version"]), ("Channels", header["Channels"]), ("Rate", header["Sample Rate"].split()[0])):
        if found.get(key) != wanted:
            problems.append(f"ogginfo: {key} is {found.get(key)!r}, the manifest's {wanted!r}")
    text = run(["ogginfo", str(path)]).stdout.decode("utf-8", "replace")
    nominal = header["Bitrate Nominal"]
    wanted = f"Nominal bitrate: {int(nominal.split()[0]) / 1000:f} kb/s" if nominal.endswith("bps") else "Nominal bitrate not set"
    if wanted not in text:
        problems.append(f"ogginfo does not print {wanted!r}")
    if "comment" in vorbis:
        if not comments or comments[0][0] != vorbis["comment"]["vendor"] or comments[0][1] != vorbis["comment"]["comments"]:
            problems.append(f"ogginfo: the vendor and comments {comments[:1]} differ from the manifest's {vorbis['comment']}")
        listed = run(["vorbiscomment", "-l", str(path)]).stdout.decode("utf-8", "replace").splitlines()
        if listed != vorbis["comment"]["comments"]:
            problems.append(f"vorbiscomment lists {listed}, the manifest {vorbis['comment']['comments']}")

    if "ffprobe" in skips:
        probe = {"streams": [{"codec_name": "vorbis", "sample_rate": info["sample_rate"], "channels": info["channels"], "index": 0}], "packets": [
            {"stream_index": 0, "size": pk[1]} for pk in vorbis["packets"]]}
    else:
        probe = ffprobe(path)
    streams = [st for st in probe.get("streams", []) if st.get("codec_name") == "vorbis"]
    if len(streams) != 1 or int(streams[0]["sample_rate"]) != info["sample_rate"] or streams[0]["channels"] != info["channels"]:
        problems.append(f"ffprobe reads {[(st.get('sample_rate'), st.get('channels')) for st in streams]}, the manifest {info['sample_rate']} Hz, {info['channels']} channels")
    if len(streams) == 1:
        check_layout(name, streams[0], info, problems)
    packets = [pk for pk in probe.get("packets", []) if streams and pk["stream_index"] == streams[0]["index"]]
    if [int(pk["size"]) for pk in packets] != [pk[1] for pk in vorbis["packets"]]:
        problems.append("ffprobe: the audio packet sizes differ from the manifest's")
    skip(name, "ffprobe's packet durations", "ffmpeg's Vorbis parser gives a short block after a long one 128 samples where the overlap rule "
         "returns 576, and corrects its timestamps from the granule positions; libvorbis counts a quarter of each block "
         "(lib/block.c, vorbis_synthesis_blockin), which every page's granule position confirms where the manifest has no granule finding")

    if "oggdec" in skips:
        return
    with tempfile.TemporaryDirectory() as work:
        out = pathlib.Path(work) / "out.raw"
        run(["oggdec", "-Q", "-R", "-o", str(out), str(path)])
        samples = out.stat().st_size // (2 * info["channels"]) if out.exists() else None
    if samples != info["samples"]:
        problems.append(f"oggdec writes {samples} samples, the manifest expects {info['samples']}")


def check_opus(name, manifest, problems, skips):
    """opusinfo, ffprobe and opusdec on a file of one Opus stream."""
    stream = next(st for st in manifest["streams"] if "opus" in st)
    opus, info = stream["opus"], manifest["stream_infos"][0]
    header = opus["header"]
    path = DIRECTORY / name
    if "opusinfo" not in skips:
        text = run(["opusinfo", str(path)]).stdout.decode("utf-8", "replace")
        gain = float(header["Output Gain"].split()[0])
        wanted = [f"Pre-skip: {header['Pre-skip'].split()[0]}", f"Channels: {header['Channels']}", f"Playback gain: {gain:g} dB"]
        rate = header["Input Sample Rate"]
        wanted.append(f"Original sample rate: {rate.split()[0]} Hz" if rate.endswith("Hz") else "Original sample rate")
        if "Stream Count" in header:
            wanted.append(f"Streams: {header['Stream Count']}, Coupled: {header['Coupled Count']}")
        if "comment" in opus:
            wanted.append(f"Encoded with {opus['comment']['vendor']}")
            wanted += [f"\t{c}" for c in opus["comment"]["comments"]]
        statistics = opus["statistics"]
        if statistics.get("Start Offset", "0 samples") == "0 samples":
            samples = info["samples"]
            wanted.append(f"Playback length: {samples // 48000 // 60}m:{samples / 48000 % 60:06.3f}s")
        for line in wanted:
            if line not in text:
                problems.append(f"opusinfo does not print {line!r}")
        if ("(hard-CBR)" in text) == info["is_vbr"]:
            problems.append(f"opusinfo {'calls' if '(hard-CBR)' in text else 'does not call'} the stream hard CBR, where IsVbr is {info['is_vbr']}")

    if "ffprobe" not in skips:
        probe = ffprobe(path)
        streams = [st for st in probe.get("streams", []) if st.get("codec_name") == "opus"]
        if len(streams) != 1 or int(streams[0]["sample_rate"]) != 48000 or streams[0]["channels"] != info["channels"]:
            problems.append(f"ffprobe reads {[(st.get('sample_rate'), st.get('channels')) for st in streams]}, the manifest 48000 Hz, {info['channels']} channels")
        if len(streams) == 1:
            check_layout(name, streams[0], info, problems)
        packets = [pk for pk in probe.get("packets", []) if streams and pk["stream_index"] == streams[0]["index"]]
        if [int(pk["size"]) for pk in packets] != [pk[1] for pk in opus["packets"]]:
            problems.append("ffprobe: the audio packet sizes differ from the manifest's")
        # The last packet's duration is less the end trimming.
        audio = stream_pages(name, manifest, int(stream["serial"], 16))[2:]
        durations = [vo.declared_samples(packet) for packet in audio]
        if durations:
            durations[-1] -= int(opus["statistics"].get("End Trimming", "0").split()[0])
        if [int(pk.get("duration", -1)) for pk in packets] != durations:
            problems.append("ffprobe: the packet durations differ from what the packets' TOC bytes give")

    if "opusdec" not in skips:
        with tempfile.TemporaryDirectory() as work:
            out = pathlib.Path(work) / "out.wav"
            run(["opusdec", "--quiet", "--rate", "48000", str(path), str(out)])
            samples = wav_samples(out, info["channels"])
        if samples != info["samples"]:
            problems.append(f"opusdec writes {samples} samples, the manifest expects {info['samples']}")


def check(name, manifest):
    problems = []
    checks = manifest["crosscheck"]
    path = DIRECTORY / name
    if "format" in manifest:
        probe = ffprobe(path)
        if probe.get("format", {}).get("format_name") != "mp3" or len(probe.get("packets", [])) != checks["mpeg_frames"]:
            problems.append(f"ffprobe reads {probe.get('format', {}).get('format_name')} with {len(probe.get('packets', []))} packets, not MPEG audio of {checks['mpeg_frames']} frames")
        code, output = oggz_validate(path)
        if code == 0:
            problems.append("oggz validate takes the MP3 for Ogg")
        return problems

    code, output = oggz_validate(path)
    verdict = "ok" if checks.get("valid") else checks.get("oggz_validate")
    if verdict == "ok":
        if code != 0 or output.strip():
            problems.append(f"oggz validate fails: {output.strip()[:200]}")
    elif verdict is None:
        skip(name, "oggz validate", checks["reason"])
    else:
        if code == 0:
            problems.append("oggz validate passes the file")
        for text in verdict:
            if text.lower() not in output.lower():
                problems.append(f"oggz validate does not report {text!r}: {output.strip()[:200]}")

    if checks.get("valid"):
        chained = len({s["link"] for s in manifest["streams"]}) > 1
        check_packets(name, manifest, problems)
        if speex_streams(manifest):
            check_ogginfo(name, manifest, problems)
            check_ffprobe(name, manifest, problems, chained)
            if len(manifest["streams"]) > 1 and not chained:
                skip(name, "speexdec", "speexdec 1.2.1 reads one logical stream; a second one in the same link makes it misread the comment packet")
            else:
                if chained:
                    skip(name, "speexdec after the first link", "speexdec stops at the first end-of-stream page")
                check_speexdec(name, manifest, problems)
        skips = checks.get("skip", {})
        for check_name, reason in skips.items():
            skip(name, check_name, reason)
        if any("vorbis" in st for st in manifest["streams"]):
            check_vorbis(name, manifest, problems, skips)
        if any("opus" in st for st in manifest["streams"]):
            check_opus(name, manifest, problems, skips)
    else:
        if checks.get("packets"):
            check_packets(name, manifest, problems, pages=False)
        if checks.get("speexdec"):
            check_speexdec(name, manifest, problems)
        for check_name, reason in checks["not_compared"].items():
            skip(name, check_name, reason)
    return problems


# MediaInfo reads a Speex comment header as a list of length-prefixed strings without the comment
# count (Audio/File_Speex.cpp, Comment), so it shows the vendor string and garbage, and none of the
# comments.
MEDIAINFO_SPEEX_COMMENTS = "MediaInfo reads the Speex comment header as length-prefixed strings without the comment count (File_Speex.cpp, Comment), so it reads none of the comments"
# The Vorbis I specification (4.2.4, setup header) stores the floor count less one; MediaInfo reads
# that many floor types and skips no floor configuration between them (File_Vorbis.cpp, Setup), so
# it reads no type of a single floor and garbage after the first of several.
MEDIAINFO_FLOORS = "MediaInfo reads the stored floor count, one less than the floors (Vorbis I 4.2.4), as the count of floor types, and skips no floor configuration between them (File_Vorbis.cpp, Setup)"
# Fixtures on which MediaInfo reads a field differently, with the evidence.
MEDIAINFO_READS_OTHERWISE = {
    "header-invalid.spx": dict.fromkeys(["channels", "sample rate", "encoder", "bitrate mode", "duration"], "MediaInfo reads the Speex header's fields only with version ID 1 (File_Speex.cpp, Identification), which this header breaks"),
    "header-short.spx": {"streams": "MediaInfo names the codec from the 8-byte signature of a header cut short (File_Speex.cpp, Identification), where the analyzer reports no stream"},
    "vorbis-ident-invalid.ogg": {"format": "MediaInfo reads the identification header only with Vorbis version 0 (File_Vorbis.cpp, Identification: \"Not supported\"), which this one breaks"},
    "huge-lacing.spx": {"duration": "MediaInfo takes the granule position of every page (File_Ogg.cpp, Header_Parse), also the -1 that RFC 3533 section 6 sets where no packet ends on the page, so its duration is 2^64 - 1 samples"},
}


def mediainfo_streams(report):
    """The logical streams of the report's first chain link, as MediaInfo reports only those
    (Multiple/File_Ogg.cpp keeps one parser per serial number and stops at the first link's end)."""
    found = []
    for stream in report["streams"]:
        node = mi.stream_node(report, stream)
        if mi.value(node, "Chain Link") in (None, 1):
            found.append((stream, node))
    return found


def check_mediainfo(name, path, tally):
    """MediaInfo must report each logical stream's codec, channels, sample rate (Opus: the input
    rate), the duration its last granule position gives, the encoder (Speex: the header's version,
    Vorbis and Opus: the vendor string), Speex's header bitrate and mode, Vorbis's nominal bitrate,
    mode and floor type, Opus's channel layout, and the comments' title, artist, album and encoder."""
    problems = tally.start(name)
    report = mi.golden(path)
    streams = mediainfo_streams(report)
    info = mi.read(path)
    audio = info["Audio"] if info else []
    tally.compare("streams", len(audio), len(streams))
    for (stream, node), track in zip(streams, audio):
        if node["name"] != "Logical Stream":
            tally.compare("format", track.get("Format"), "MPEG Audio" if stream["codec"].startswith("MPEG") else stream["codec"])
            continue
        codec = mi.value(node, "Codec")
        tally.compare("format", track.get("Format"), codec)
        if track.get("Format") != codec:
            continue
        tally.compare("channels", track.get("Channels"), str(stream["channels"]))
        granule = mi.value(node, "Last Granule Position")
        if codec == "Speex":
            header = mi.child(node, "Speex Header")
            tally.compare("sample rate", track.get("SamplingRate"), str(stream["sampleRate"]))
            tally.compare("encoder", track.get("Encoded_Library"), mi.value(header, "Speex Version"))
            tally.compare("bitrate mode", track.get("BitRate_Mode"), "VBR" if mi.value(header, "IsVbr") else "CBR")
            bitrate = mi.leading_number(mi.value(header, "Bitrate"))
            tally.compare("bitrate", track.get("BitRate"), None if bitrate == -1 else str(bitrate))
            rate = stream["sampleRate"]
        elif codec == "Vorbis":
            header = mi.child(node, "Identification Header")
            tally.compare("sample rate", track.get("SamplingRate"), str(stream["sampleRate"]))
            tally.compare("encoder", track.get("Encoded_Library"), mi.value(node, "Vorbis Comment", "Vendor"))
            rates = [mi.leading_number(mi.value(header, f"Bitrate {n}")) for n in ("Maximum", "Nominal", "Minimum")]
            tally.compare("bitrate", track.get("BitRate"), str(rates[1]) if rates[1] else None)
            # File_Vorbis.cpp, Identification: CBR where all three bitrates are the same and set.
            tally.compare("bitrate mode", track.get("BitRate_Mode"), "CBR" if rates[1] and rates[0] == rates[1] == rates[2] else "VBR")
            floors = re.match(r"(\d+) floors?, type (\d)", str(mi.value(node, "Setup Header", "Floors")))
            if floors is None or floors.group(1) == "1":
                tally.skip("floor type", MEDIAINFO_FLOORS + "; with one floor it reads none")
            else:
                tally.compare("floor type", (track.get("Format_Settings_Floor") or "").split(" / ")[0], floors.group(2))
            rate = stream["sampleRate"]
        else:
            head = mi.child(node, "Opus Head")
            tally.compare("input sample rate", track.get("SamplingRate"), str(mi.value(head, "Input Sample Rate") or 48000))
            tally.compare("encoder", track.get("Encoded_Library"), mi.value(node, "Vorbis Comment", "Vendor"))
            family = mi.leading_number(mi.value(head, "Mapping Family"))
            if family in (0, 1) and stream["channelLayout"]:
                tally.compare("channel layout", mi.speakers(track.get("ChannelLayout")), mi.normalized(stream["channelLayout"]))
            else:
                tally.skip("channel layout", "MediaInfo names the speakers of mapping families 0 and 1 only (File_Opus.cpp, Identification)")
            rate = 48000
        if granule:
            # File_Ogg.cpp, Streams_Fill: the last granule position over the sample rate, 48 kHz for
            # Opus, rounded to the millisecond, without Opus's pre-skip or Speex's encoder delay.
            tally.compare("duration", track.get("Duration"), f"{math.floor(granule * 1000 / rate + 0.5) / 1000:.3f}")
        tally.skip("samples", "MediaInfo derives the sample count from its duration, rounded to the millisecond (File_Ogg.cpp, Streams_Fill)")
    general = info["General"] if info else {}
    if streams and streams[0][0]["codec"].startswith("Speex"):
        tally.skip("tags", MEDIAINFO_SPEEX_COMMENTS)
    else:
        mi.compare_tags(tally, general, report)
        comments = [c for n in mi.walk(report["root"]) if n["name"] == "Vorbis Comment" for c in n.get("children", [])]
        encoders = [c.get("value") for c in comments if c["name"].upper() in ("ENCODER", "ENCODED_USING")]
        tally.compare("tag encoder", general.get("Encoded_Application"), encoders[0] if encoders else None)
    return problems


def main():
    mi.require()
    tally = mi.Tally(MEDIAINFO_READS_OTHERWISE)
    for tool in ("oggz", "ogginfo", "vorbiscomment", "oggdec", "ffprobe", "speexdec", "opusinfo", "opusdec"):
        if subprocess.run(["which", tool], capture_output=True).returncode:
            raise SystemExit(f"{tool} is missing: install oggz-tools, vorbis-tools, ffmpeg, speex and opus-tools.")
    failed = checked = 0
    for manifest_path in sorted(DIRECTORY.glob("*.expected.json")):
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        stem = manifest_path.name[: -len(".expected.json")]
        name = next(p.name for p in DIRECTORY.glob(stem + ".*") if not p.name.endswith(".json"))
        problems = check(name, manifest)
        problems += check_mediainfo(name, DIRECTORY / name, tally)
        checked += 1
        for problem in problems:
            failed += 1
            print(f"{name}: {problem}")
    for line in SKIPS:
        print(line, file=sys.stderr)
    for problem in tally.unused():
        print(problem)
        failed += 1
    tally.print()
    print(f"Cross-checked {checked} Ogg fixtures, {failed} problems, {len(SKIPS)} checks skipped with their reasons.", file=sys.stderr)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
