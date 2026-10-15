#!/usr/bin/env python3
"""Checks the ASF and WMA fixtures' manifests against independent tools.

For every manifest in tests/fixtures/asf:

- ffprobe (FFmpeg 6.1, the reference ASF demuxer asfdec_f.c): the streams' codec, channels and rate,
  and the packets it reads. For a hand-built fixture the packets are the media objects the model
  works out from the payloads: the same count, and for each one the stream, the size and the
  presentation time, which also covers fragmented objects put together and compressed payloads split.
  For an ffmpeg-encoded WMA file they are the superframes the manifest records.
- ffmpeg decoding to PCM: no error, and for the WMA files the sample count the manifest records. For
  the Lossless fixtures of Microsoft's encoder (external-windows-*), the decoded samples equal the
  input signal bit for bit. For Windows Media Audio Voice, the samples ffmpeg decodes equal the
  analyzer's (the golden report's) for every fixture: the hand-built ones (voice-*, from
  voice_model.py) too, damaged as they are, where ffmpeg must print the errors the manifest names
  (a configuration it rejects, a music superframe, a frame type code or a sample count it refuses) and
  drop the same packets and superframes as the analyzer does; that agreement of the sample counts is
  the independent check of the superframe boundaries, the spillover and the packets' resync.
- MediaInfo (24.01): the format, the codec ID, the channels, the sampling rate and the duration of
  the WMA files against ffprobe's; and against the analyzer's golden report: the bit rate mode (the
  IsVBR attribute), the description of the codec (the Codec List), the format profile, title and
  performer, the encoded date (the File Properties' creation date) and the maximum overall bit rate.
  For Voice, MediaInfo's words map to the analyzer's: Format "WMA" with the Format_Profile "Voice" and the
  CodecID "A" are the format tag 0x000A (Windows Media Audio Voice), its BitRate is the nominal bitrate
  the container states (the statistic "Nominal Bitrate"), and its SamplingCount the container's duration
  times the rate, which the analyzer's samples equal within a hundredth of a second.

- the protection objects, on every hand-built fixture (those without any are the negative control): ffmpeg's ASF demuxer (asfdec_f.c) warns "DRM
  protected stream detected" for a Content Encryption Object and "Ext DRM protected stream detected" for an Extended Content Encryption Object, and
  never for the Advanced one, which it does not read; it exports the Content Encryption Object's protection type, key ID and license URL as the tags
  ASF_Protection_Type, ASF_Key_ID and ASF_License_URL, which equal the analyzer's nodes; MediaInfo reports the Encryption field "Encrypted" of an audio
  track exactly where its Stream Properties Object sets the Encrypted Content Flag, whatever the encryption objects say. The analyzer's protected
  streams are those of the golden report, which the tests hold to the manifest's "protection".

Fixtures whose manifest says ffprobe does not apply say why; every skipped check prints its reason.
Disagreements fail the run (exit status 1).

    python3 scripts/fixtures/crosscheck_asf_fixtures.py [name ...]

What no tool at hand gives: the frame boundaries inside a WMA superframe. ffmpeg's decoder does not
expose them and no other decoder is available, so they rest on the encoder's own padding (ffmpeg's
fills each superframe with 'N' bytes, which WmaFixtureTests checks) and, for Microsoft's files, on
the decoder reading every superframe without error.
"""

import json
import pathlib
import shutil
import subprocess
import sys
import tempfile

import wma_signals

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "asf"
SKIPS = []


def skip(name, what, reason):
    SKIPS.append((name, what, reason))
    print(f"  skipped {what}: {reason}")


def tool(name):
    return shutil.which(name)


def run(command, **kw):
    return subprocess.run(command, capture_output=True, **kw)


def ffprobe_json(path, *args):
    result = run(["ffprobe", "-v", "error", "-of", "json", *args, str(path)], text=True)
    return result.returncode, (json.loads(result.stdout) if result.stdout.strip() else {})


def decode(path, fmt="s16le", level="error"):
    result = run(["ffmpeg", "-v", level, "-i", str(path), "-map", "0:a", "-f", fmt, "-"])
    # The raw PCM muxer warns about timestamps of the decoder's packets for some Lossless files; the samples are what is checked.
    errors = "\n".join(l for l in result.stderr.decode("utf-8", "replace").splitlines() if "non monotonically increasing dts" not in l).strip()
    return result.returncode, result.stdout, errors


def analyzer_samples(path):
    report = golden_report(path)
    return report["streams"][0]["samples"] if report and report["streams"] else None


def analyzer_statistic(path, name):
    """A statistic of the Voice stream in the golden report."""
    report = golden_report(path)
    voice = next((n for n in nodes_of(report["root"]) if n["name"] == "Windows Media Audio Voice"), None) if report else None
    stats = next((c for c in voice["children"] if c["name"] == "Statistics"), None) if voice else None
    return next((c.get("value") for c in stats["children"] if c["name"] == name), None) if stats else None


def check_voice_frames(path, problems, pinned=None):
    """ffprobe -count_frames decodes the stream: each superframe it decodes is a frame, so the count is the analyzer's superframes (and
    the manifest's, where it pins the count)."""
    code, data = ffprobe_json(path, "-count_frames", "-select_streams", "a:0", "-show_entries", "stream=nb_read_frames")
    counted = int(data["streams"][0].get("nb_read_frames", 0)) if code == 0 and data.get("streams") else None
    if counted != analyzer_statistic(path, "Superframes"):
        problems.append(f"ffprobe decodes {counted} frames, the analyzer's golden report has {analyzer_statistic(path, 'Superframes')} superframes")
    if pinned is not None and counted != pinned:
        problems.append(f"ffprobe decodes {counted} frames, the manifest pins {pinned}")


def check_voice_decode(name, manifest, path, problems):
    """ffmpeg's decoder against the manifest and the analyzer's report, for a hand-built Voice fixture."""
    crosscheck = manifest["crosscheck"]
    mode = crosscheck["decode"]
    code, pcm, errors = decode(path, level="warning")
    samples = len(pcm) // 2
    analyzed = analyzer_samples(path)
    pins = crosscheck.get("ffmpeg", {})
    if mode != "error":
        check_voice_frames(path, problems, pins.get("superframes"))
    if "samples" in pins and samples != pins["samples"]:
        problems.append(f"ffmpeg decodes {samples} samples, the manifest pins {pins['samples']}")
    if mode == "clean":
        # No error at all, though the stream is not what the writer put in: the analysis must agree with what the decoder makes of it.
        if errors:
            problems.append(f"ffmpeg decoding: {errors.splitlines()[0]}")
        if analyzed != samples:
            problems.append(f"the analyzer's golden report has {analyzed} samples, ffmpeg decodes {samples}")
        return
    if mode == "ok":
        if errors:
            problems.append(f"ffmpeg decoding: {errors.splitlines()[0]}")
        if samples != manifest["voice"]["decoded_samples"]:
            problems.append(f"ffmpeg decodes {samples} samples, the writer put in {manifest['voice']['decoded_samples']}")
        if analyzed != samples:
            problems.append(f"the analyzer's golden report has {analyzed} samples, ffmpeg decodes {samples}")
        return
    for message in crosscheck.get("messages", []):
        if message not in errors:
            problems.append(f"ffmpeg does not report {message!r}; it says {errors.splitlines()[:2]}")
    if mode == "error":
        # The decoder does not open: nothing is decoded, and the analyzer states what the container says.
        if samples != 0 or code == 0:
            problems.append(f"ffmpeg decodes {samples} samples (exit status {code}) where it should refuse the configuration")
    elif analyzed != samples:
        problems.append(f"the analyzer's golden report has {analyzed} samples, ffmpeg decodes {samples}")


def check_protection(name, manifest, path, problems):
    """What ffmpeg and MediaInfo report of the protection objects and the Encrypted Content Flag, against the manifest's objects and the golden report."""
    objects = [o[0] for o in manifest["header_objects"]]
    result = run(["ffprobe", "-v", "warning", "-show_format", "-of", "json", str(path)], text=True)
    for message, object_name in (("Ext DRM protected stream detected", "Extended Content Encryption Object"), ("DRM protected stream detected", "Content Encryption Object")):
        heard = any(line.endswith(message + ", decoding will likely fail!") for line in result.stderr.splitlines())
        # The first message is a part of the second's text only in its tail ("Ext DRM ..." ends with "DRM protected stream detected ...").
        if message == "DRM protected stream detected":
            heard = any("] DRM protected stream detected" in line for line in result.stderr.splitlines())
        if heard != (object_name in objects):
            problems.append(f"ffprobe {'warns' if heard else 'does not warn'} {message!r}, the manifest {'lacks' if heard else 'has'} the {object_name}")
    try:
        tags = json.loads(result.stdout).get("format", {}).get("tags", {}) if result.stdout.strip() else {}
    except json.JSONDecodeError:
        skip(name, "ffprobe's ASF_ tags", "ffprobe fails on the damaged file")
        tags = None
    report = golden_report(path)
    encryption = next((n for n in nodes_of(report["root"]) if n["name"] == "Content Encryption Object"), None) if report else None
    exported = {k: v for k, v in (tags or {}).items() if k.startswith("ASF_")}
    if tags is None:
        pass
    elif encryption is None:
        if exported:
            problems.append(f"ffprobe exports {sorted(exported)} without a Content Encryption Object")
    else:
        for tag, field in (("ASF_Protection_Type", "Protection Type"), ("ASF_Key_ID", "Key ID"), ("ASF_License_URL", "License URL")):
            wanted = next((c.get("value") for c in encryption["children"] if c["name"] == field), None)
            if exported.get(tag) != wanted:
                problems.append(f"ffprobe's {tag} is {exported.get(tag)!r}, the analyzer's {field} {wanted!r}")
    if tool("mediainfo"):
        try:
            tracks = json.loads(run(["mediainfo", "--Output=JSON", str(path)], text=True).stdout)["media"]["track"]
        except (json.JSONDecodeError, KeyError):
            skip(name, "MediaInfo's Encryption field", "MediaInfo's output for the damaged file is not JSON")
            return
        audio = [t for t in tracks if t["@type"] == "Audio"]
        flagged = [s["encrypted"] for s in manifest["streams"] if "tag" in s]
        if len(audio) == len(flagged):
            for track, flag in zip(audio, flagged):
                if (track.get("Encryption") == "Encrypted") != flag:
                    problems.append(f"MediaInfo's Encryption is {track.get('Encryption')!r} for a stream whose flag is {flag}")
        else:
            problems.append(f"MediaInfo finds {len(audio)} audio tracks, the manifest {len(flagged)}")


def check_hand_built(name, manifest, path, problems):
    crosscheck = manifest.get("crosscheck", {})
    check_protection(name, manifest, path, problems)
    if "voice" in manifest or crosscheck.get("decode"):
        check_voice_decode(name, manifest, path, problems)
    if not crosscheck.get("ffprobe"):
        skip(name, "ffprobe" if crosscheck.get("decode") else "ffprobe and ffmpeg", crosscheck.get("reason", "the fixture is damaged on purpose"))
        return
    code, data = ffprobe_json(path, "-show_streams", "-show_packets")
    if code != 0:
        problems.append("ffprobe failed")
        return
    streams = {s["number"]: s for s in manifest["streams"]}
    audio = [s for s in manifest["streams"] if "tag" in s]
    # An attached picture (WM/Picture) is a video stream of its own to ffmpeg.
    data["streams"] = [e for e in data.get("streams", []) if not e.get("disposition", {}).get("attached_pic")]
    if len(data["streams"]) != len(manifest["streams"]):
        problems.append(f"ffprobe finds {len(data['streams'])} streams, the manifest {len(manifest['streams'])}")
    # ffmpeg numbers the streams in the order of their Stream Properties Objects.
    order = {e["index"]: manifest["streams"][e["index"]]["number"] for e in data.get("streams", []) if e["index"] < len(manifest["streams"])}
    for entry in data.get("streams", []):
        number = order.get(entry["index"])
        expected = streams.get(number)
        if expected and "tag" in expected and entry.get("codec_type") == "audio":
            if int(entry["channels"]) != expected["channels"] or int(entry["sample_rate"]) != expected["rate"]:
                problems.append(f"stream {number}: ffprobe says {entry['channels']} channels at {entry['sample_rate']} Hz, the manifest {expected['channels']} at {expected['rate']}")
    got = sorted((order[p["stream_index"]], int(p["size"]), round(float(p.get("pts_time", 0)) * 1000)) for p in data.get("packets", []) if p["stream_index"] in order)
    want = sorted((o[0], o[2], o[3] - manifest["file"]["preroll"]) for o in manifest["media_objects"])
    if len(got) != len(want):
        problems.append(f"ffprobe reads {len(got)} packets, the model {len(want)} media objects")
    else:
        for g, w in zip(got, want):
            if g != w:
                problems.append(f"packet of stream {g[0]}: ffprobe has size {g[1]} at {g[2]} ms, the model {w[1]} at {w[2]} ms")
                break
    if audio and all(s["tag"] == 1 for s in audio) and len(audio) == len(manifest["streams"]):
        code, pcm, errors = decode(path)
        if code != 0 or errors:
            problems.append(f"ffmpeg decoding: {errors or code}")
    if audio and all(s["tag"] == 0x000A for s in audio):
        check_mediainfo(name, path, data["streams"][0], problems, golden_report(path), count_samples=crosscheck.get("decode") == "ok")


def check_external_ffmpeg(name, manifest, path, problems, with_mediainfo=True):
    expected = manifest["ffprobe"]
    code, data = ffprobe_json(path, "-show_streams")
    if code != 0:
        problems.append("ffprobe failed")
        return
    stream = data["streams"][0]
    for key, field in (("codec", "codec_name"), ("channels", "channels"), ("rate", "sample_rate"), ("bit_rate", "bit_rate")):
        if str(expected[key]) != str(stream[field]):
            problems.append(f"ffprobe {field} is {stream[field]}, the manifest {expected[key]}")
    code, packets = ffprobe_json(path, "-select_streams", "0", "-show_packets")
    sizes = sorted({int(p["size"]) for p in packets["packets"]})
    if len(packets["packets"]) != expected["packets"] or sizes != expected["packet_sizes"]:
        problems.append(f"ffprobe reads {len(packets['packets'])} packets of {sizes}, the manifest {expected['packets']} of {expected['packet_sizes']}")
    code, pcm, errors = decode(path)
    samples = len(pcm) // (2 * expected["channels"])
    if code != 0 or errors:
        problems.append(f"ffmpeg decoding: {errors or code}")
    if samples != expected["decoded_samples"]:
        problems.append(f"ffmpeg decodes {samples} samples, the manifest {expected['decoded_samples']}")
    if with_mediainfo:
        check_mediainfo(name, path, stream, problems, golden_report(path))


def golden_report(path):
    """The report the analyzer wrote for the fixture (checked by the tests), which carries what
    the parser says about the file: MediaInfo's figures are compared with it."""
    golden = path.with_suffix(".report.json")
    return json.loads(golden.read_text(encoding="utf-8"))[0] if golden.exists() else None


def nodes_of(node):
    yield node
    for child in node.get("children", []):
        yield from nodes_of(child)


def named(report, name, under=None):
    """The value of the first node called name (below the first node called under)."""
    start = next((n for n in nodes_of(report["root"]) if n["name"] == under), None) if under else report["root"]
    return next((n.get("value") for n in nodes_of(start) if n["name"] == name), None) if start else None


def check_against_report(audio, general, report, problems):
    """MediaInfoLib's File_Wm_Elements.cpp (BSD-2-Clause) reads the same header fields; every
    figure it states is compared with the parser's node for it."""
    stream = report["streams"][0]
    is_vbr = named(report, "IsVBR")
    if is_vbr is not None:
        parser = "VBR" if stream["isVbr"] else "CBR"
        for kind, track in (("audio", audio), ("overall", general)):
            mode = track.get("BitRate_Mode" if kind == "audio" else "OverallBitRate_Mode")
            if mode != parser:
                problems.append(f"MediaInfo's {kind} bit rate mode is {mode}, the parser's {parser} (IsVBR {is_vbr})")
    codec, description = named(report, "Codec Name"), named(report, "Codec Description")
    if codec is not None:
        wanted = f"{codec} - {description}" if description else codec
        if audio.get("CodecID_Description") != wanted:
            problems.append(f"MediaInfo's description of the codec is {audio.get('CodecID_Description')!r}, the Codec List's {wanted!r}")
    profile = {"Windows Media Audio Professional": "Pro", "Windows Media Audio Lossless": "Lossless", "Windows Media Audio Voice": "Voice"}.get(stream["codec"])
    if audio.get("Format_Profile") != profile:
        problems.append(f"MediaInfo's format profile is {audio.get('Format_Profile')}, the parser's codec {stream['codec']}")
    title, author = named(report, "Title", "Content Description Object"), named(report, "Author", "Content Description Object")
    if title and general.get("Title") != title:
        problems.append(f"MediaInfo's title is {general.get('Title')!r}, the parser's {title!r}")
    if author and general.get("Performer") != author:
        problems.append(f"MediaInfo's performer is {general.get('Performer')!r}, the parser's author {author!r}")
    created = named(report, "Creation Date", "File Properties Object")
    if isinstance(created, str) and created.startswith("20") and general.get("Encoded_Date") != created.split(".")[0] + " UTC":
        problems.append(f"MediaInfo's encoded date is {general.get('Encoded_Date')}, the File Properties' creation date {created}")
    maximum = named(report, "Maximum Bitrate", "File Properties Object")
    if maximum and general.get("OverallBitRate_Maximum") != str(maximum):
        problems.append(f"MediaInfo's maximum overall bit rate is {general.get('OverallBitRate_Maximum')}, the File Properties' {maximum} bps")


def check_mediainfo(name, path, stream, problems, report=None, count_samples=True):
    if not tool("mediainfo"):
        skip(name, "MediaInfo", "mediainfo is not installed")
        return
    result = run(["mediainfo", "-f", "--Output=JSON", str(path)], text=True)
    tracks = json.loads(result.stdout)["media"]["track"]
    general = next(t for t in tracks if t["@type"] == "General")
    audio = next(t for t in tracks if t["@type"] == "Audio")
    if general.get("Format") != "Windows Media":
        problems.append(f"MediaInfo's container is {general.get('Format')}")
    if int(audio["Channels"]) != int(stream["channels"]) or int(audio["SamplingRate"]) != int(stream["sample_rate"]):
        problems.append(f"MediaInfo says {audio['Channels']} channels at {audio['SamplingRate']} Hz, ffprobe {stream['channels']} at {stream['sample_rate']}")
    tags = {"wmav1": "160", "wmav2": "161", "wmapro": "162", "wmalossless": "163", "wmavoice": "A"}
    if tags.get(stream["codec_name"]) != audio.get("CodecID"):
        problems.append(f"MediaInfo's codec ID is {audio.get('CodecID')}, ffprobe's codec {stream['codec_name']}")
    if abs(float(audio["Duration"]) - float(stream.get("duration", audio["Duration"]))) > 0.01:
        problems.append(f"MediaInfo's duration is {audio['Duration']}, ffprobe's {stream['duration']}")
    if report is not None:
        check_against_report(audio, general, report, problems)
        if stream["codec_name"] == "wmavoice":
            check_voice_against_report(audio, report, problems, count_samples)


def check_voice_against_report(audio, report, problems, count_samples=True):
    """MediaInfoLib reads a Voice stream's format tag and the container's figures only: Format "WMA", Format_Profile "Voice" and the
    CodecID "A" are the tag 0x000A (checked above), BitRate the nominal bitrate, SamplingCount the container's duration times the rate."""
    stream = report["streams"][0]
    nominal = named(report, "Nominal Bitrate", "Windows Media Audio Voice")
    if nominal is not None and audio.get("BitRate") != str(round(nominal * 1000)):
        problems.append(f"MediaInfo's bit rate is {audio.get('BitRate')}, the analyzer's nominal bitrate {nominal} kbps")
    if audio.get("Format") != "WMA":
        problems.append(f"MediaInfo's format is {audio.get('Format')}, expected WMA for the format tag 0x000A")
    count = int(audio.get("SamplingCount", 0))
    # The superframes a damaged stream loses are in the container's duration and not in the analyzer's samples.
    if count_samples and stream.get("samples") and abs(count - stream["samples"]) > stream["sampleRate"] // 100:
        problems.append(f"MediaInfo's sampling count is {count}, the analyzer's samples {stream['samples']}")


def check_windows(name, provenance, path, problems):
    code, data = ffprobe_json(path, "-show_streams")
    if code != 0:
        problems.append("ffprobe failed")
        return
    stream = data["streams"][0]
    # The manifest's ffprobe block (packets, packet sizes, decoded samples, bit rate) is re-derived from the file.
    check_external_ffmpeg(name, json.loads((DIRECTORY / (name + ".expected.json")).read_text(encoding="utf-8")), path, problems, with_mediainfo=False)
    request = provenance["input"]
    actual = provenance["actual"]
    if int(stream["channels"]) != actual["channels"] or int(stream["sample_rate"]) != actual["rate"]:
        problems.append(f"ffprobe says {stream['channels']} channels at {stream['sample_rate']} Hz, the provenance {actual['channels']} at {actual['rate']}")
    if provenance["encoder"]["path"] == "wmf-sdk" and (int(stream["channels"]) != request["channels"] or int(stream["sample_rate"]) != request["rate"]):
        problems.append(f"ffprobe says {stream['channels']} channels at {stream['sample_rate']} Hz, the request to the SDK was {request['channels']} at {request['rate']}")
    if stream["codec_name"] == "wmalossless":
        bits = request["bits"]
        code, pcm, errors = decode(path, "s16le" if bits == 16 else "s32le")
        if code != 0 or errors:
            problems.append(f"ffmpeg decoding: {errors or code}")
        else:
            width = 2 if bits == 16 else 4
            with tempfile.TemporaryDirectory() as tmp:
                wav = pathlib.Path(tmp) / "input.wav"
                wma_signals.write(wav, request["rate"], request["channels"], bits, request["seconds"], request["signal"])
                import wave
                with wave.open(str(wav), "rb") as w:
                    original = w.readframes(w.getnframes())
            if bits == 24:
                original = b"".join(b"\0" + original[i:i + 3] for i in range(0, len(original), 3))
            if len(pcm) != len(original):
                problems.append(f"the decoded length is {len(pcm)} bytes, the input signal's {len(original)}")
            elif pcm != original:
                problems.append("the decoded samples differ from the input signal")
    else:
        code, pcm, errors = decode(path)
        if code != 0 or errors:
            problems.append(f"ffmpeg decoding: {errors or code}")
        elif stream["codec_name"] == "wmavoice":
            if analyzer_samples(path) != len(pcm) // 2:
                problems.append(f"the analyzer's golden report has {analyzer_samples(path)} samples, ffmpeg decodes {len(pcm) // 2}")
            check_voice_frames(path, problems)
    check_mediainfo(name, path, stream, problems, golden_report(path))


def main():
    if not tool("ffprobe") or not tool("ffmpeg"):
        print("ffmpeg and ffprobe are needed.")
        return 1
    wanted = set(sys.argv[1:])
    failures = 0
    checked = 0
    for manifest_path in sorted(DIRECTORY.glob("*.expected.json")):
        name = manifest_path.name[:-len(".expected.json")]
        if wanted and name not in wanted or name.startswith("external-windows-"):
            continue
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        path = next(DIRECTORY.glob(name + ".[aw][sm][fa]"), None)
        print(name)
        problems = []
        if name.startswith("external-ffmpeg-"):
            check_external_ffmpeg(name, manifest, path, problems)
        else:
            check_hand_built(name, manifest, path, problems)
        checked += 1
        for problem in problems:
            print(f"  FAIL {problem}")
        failures += bool(problems)
    for provenance_path in sorted(DIRECTORY.glob("external-windows-*.provenance.json")):
        name = provenance_path.name[:-len(".provenance.json")]
        if wanted and name not in wanted:
            continue
        print(name)
        problems = []
        check_windows(name, json.loads(provenance_path.read_text(encoding="utf-8")), DIRECTORY / (name + ".wma"), problems)
        checked += 1
        for problem in problems:
            print(f"  FAIL {problem}")
        failures += bool(problems)
    if not any(DIRECTORY.glob("external-windows-*.provenance.json")):
        skip("external-windows-*", "ffprobe, ffmpeg and MediaInfo", "no Microsoft-encoded fixtures are committed yet (import_windows_wma_fixtures.py)")
    print(f"{checked} fixtures checked, {failures} failed, {len(SKIPS)} checks skipped.")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
