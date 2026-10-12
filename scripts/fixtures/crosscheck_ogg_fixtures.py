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
- ffprobe: each Speex stream's sample rate and channels, the size of every audio packet, and the
  duration in samples, which ffmpeg counts from the start the first granule position implies to
  the last granule position: the manifest's samples plus the encoder delay.
- speexdec -V (speex): the header line ("Decoding 16000 Hz audio using wideband mode (mono)"), the
  vendor string, the bitrate it prints for every frame it decodes, which follows from each frame's
  submodes (ogg_model.speexdec_bitrate), and the samples it writes (ogg_model.speexdec_samples).

Fixtures that are not "valid" say which of these still apply and why the others do not; every
check that is skipped prints its reason. Disagreements fail the run.
"""

import json
import pathlib
import re
import subprocess
import sys
import tempfile

import ogg_model as m

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "ogg"
SKIPS = []


def skip(name, check, reason):
    SKIPS.append(f"{name}: {check} skipped: {reason}")


def run(arguments):
    return subprocess.run(arguments, capture_output=True)


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
    for serial, size in re.findall(r"serialno (\d+), .*?: (\d+) bytes?$", run(["oggz", "dump", str(path)]).stdout.decode(), re.M):
        sizes.setdefault(int(serial) % (1 << 32), []).append(int(size))
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
    return json.loads(result.stdout or b"{}")


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
        if counts.get(serial) is None:
            problems.append(f"oggz info lists no stream {stream['serial']}")
            continue
        # Pages that fail their CRC or are cut short never reach oggz.
        if counts[serial][0] != stream["packets"] or (pages and counts[serial][1] != stream["pages"]):
            problems.append(f"oggz info: {stream['serial']} has {counts[serial][0]} packets in {counts[serial][1]} pages, the manifest {stream['packets']} in {stream['pages']}")
        if "speex" in stream:
            audio = [p[1] for p in stream["speex"]["packets"]]
            if sizes.get(serial, [])[-len(audio):] != audio:
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
        check_ogginfo(name, manifest, problems)
        check_ffprobe(name, manifest, problems, chained)
        if len(manifest["streams"]) > 1 and not chained:
            skip(name, "speexdec", "speexdec 1.2.1 reads one logical stream; a second one in the same link makes it misread the comment packet")
        else:
            if chained:
                skip(name, "speexdec after the first link", "speexdec stops at the first end-of-stream page")
            check_speexdec(name, manifest, problems)
    else:
        if checks.get("packets"):
            check_packets(name, manifest, problems, pages=False)
        if checks.get("speexdec"):
            check_speexdec(name, manifest, problems)
        for check_name, reason in checks["not_compared"].items():
            skip(name, check_name, reason)
    return problems


def main():
    for tool in ("oggz", "ogginfo", "ffprobe", "speexdec"):
        if subprocess.run(["which", tool], capture_output=True).returncode:
            raise SystemExit(f"{tool} is missing: install oggz-tools, vorbis-tools, ffmpeg and speex.")
    failed = checked = 0
    for manifest_path in sorted(DIRECTORY.glob("*.expected.json")):
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        stem = manifest_path.name[: -len(".expected.json")]
        name = next(p.name for p in DIRECTORY.glob(stem + ".*") if not p.name.endswith(".json"))
        problems = check(name, manifest)
        checked += 1
        for problem in problems:
            failed += 1
            print(f"{name}: {problem}")
    for line in SKIPS:
        print(line, file=sys.stderr)
    print(f"Cross-checked {checked} Ogg fixtures, {failed} problems, {len(SKIPS)} checks skipped with their reasons.", file=sys.stderr)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
