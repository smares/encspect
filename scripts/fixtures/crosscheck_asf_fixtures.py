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
  input signal bit for bit.
- MediaInfo (24.01): the format, the codec ID, the channels, the sampling rate and the duration of
  the WMA files against ffprobe's.

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


def decode(path, fmt="s16le"):
    result = run(["ffmpeg", "-v", "error", "-i", str(path), "-map", "0:a", "-f", fmt, "-"])
    return result.returncode, result.stdout, result.stderr.decode("utf-8", "replace").strip()


def check_hand_built(name, manifest, path, problems):
    crosscheck = manifest.get("crosscheck", {})
    if not crosscheck.get("ffprobe"):
        skip(name, "ffprobe and ffmpeg", crosscheck.get("reason", "the fixture is damaged on purpose"))
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


def check_external_ffmpeg(name, manifest, path, problems):
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
    check_mediainfo(name, path, stream, problems)


def check_mediainfo(name, path, stream, problems):
    if not tool("mediainfo"):
        skip(name, "MediaInfo", "mediainfo is not installed")
        return
    result = run(["mediainfo", "--Output=JSON", str(path)], text=True)
    tracks = json.loads(result.stdout)["media"]["track"]
    general = next(t for t in tracks if t["@type"] == "General")
    audio = next(t for t in tracks if t["@type"] == "Audio")
    if general.get("Format") != "Windows Media":
        problems.append(f"MediaInfo's container is {general.get('Format')}")
    if int(audio["Channels"]) != int(stream["channels"]) or int(audio["SamplingRate"]) != int(stream["sample_rate"]):
        problems.append(f"MediaInfo says {audio['Channels']} channels at {audio['SamplingRate']} Hz, ffprobe {stream['channels']} at {stream['sample_rate']}")
    tags = {"wmav1": "160", "wmav2": "161", "wmapro": "162", "wmalossless": "163", "wmavoice": "10"}
    if tags.get(stream["codec_name"]) != audio.get("CodecID"):
        problems.append(f"MediaInfo's codec ID is {audio.get('CodecID')}, ffprobe's codec {stream['codec_name']}")
    if abs(float(audio["Duration"]) - float(stream.get("duration", audio["Duration"]))) > 0.01:
        problems.append(f"MediaInfo's duration is {audio['Duration']}, ffprobe's {stream['duration']}")


def check_windows(name, provenance, path, problems):
    code, data = ffprobe_json(path, "-show_streams")
    if code != 0:
        problems.append("ffprobe failed")
        return
    stream = data["streams"][0]
    request = provenance["input"]
    if int(stream["channels"]) != request["channels"] or int(stream["sample_rate"]) != request["rate"]:
        problems.append(f"ffprobe says {stream['channels']} channels at {stream['sample_rate']} Hz, the request {request['channels']} at {request['rate']}")
    if stream["codec_name"] == "wmalossless":
        bits = request["bits"]
        code, pcm, errors = decode(path, "s16le" if bits == 16 else "s32le")
        if code != 0 or errors:
            problems.append(f"ffmpeg decoding: {errors or code}")
        else:
            width = 2 if bits == 16 else 4
            with tempfile.TemporaryDirectory() as tmp:
                wav = pathlib.Path(tmp) / "input.wav"
                wma_signals.write(wav, request["rate"], request["channels"], bits, request["seconds"], request["kind"])
                import wave
                with wave.open(str(wav), "rb") as w:
                    original = w.readframes(w.getnframes())
            if bits == 24:
                original = b"".join(b"\0" + original[i:i + 3] for i in range(0, len(original), 3))
            if pcm[:len(original)] != original:
                problems.append("the decoded samples differ from the input signal")
    else:
        code, pcm, errors = decode(path)
        if code != 0 or errors:
            problems.append(f"ffmpeg decoding: {errors or code}")
    check_mediainfo(name, path, stream, problems)


def main():
    if not tool("ffprobe") or not tool("ffmpeg"):
        print("ffmpeg and ffprobe are needed.")
        return 1
    wanted = set(sys.argv[1:])
    failures = 0
    checked = 0
    for manifest_path in sorted(DIRECTORY.glob("*.expected.json")):
        name = manifest_path.name[:-len(".expected.json")]
        if wanted and name not in wanted:
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
