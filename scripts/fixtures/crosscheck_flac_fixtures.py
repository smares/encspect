#!/usr/bin/env python3
"""Checks the FLAC fixtures' manifests against independent tools.

For every manifest in tests/fixtures/flac (make_external_flac_fixtures.py, make_flac_fixtures.py):

- flac -t (flac 1.5.0 of Ubuntu 26.04): the file passes or fails as the manifest's crosscheck.flac_test says. It checks every frame's CRC-8
  and CRC-16 and, where STREAMINFO stores one, the MD5 of the decoded audio, so a fixture whose only fault is one of them must fail.
  A null there means the fixture's fault is none flac reports (frames out of order, an MD5 of 0 with frames missing, metadata): not compared.
- metaflac, on the fixtures flac -t accepts: STREAMINFO's block sizes, frame sizes, sample rate, channels, bits per sample, total samples and
  MD5, and the metadata blocks' types, last-block flags and lengths in order, against the manifest.
- flac -d, on the same fixtures: the decoded samples, written as little-endian signed integers of as many whole bytes as the bit depth
  needs, have the manifest's MD5 (decoded_pcm_md5, which flac_model.py's decoder gives and the analyzer's decoder must give).
- FFmpeg 8.0.1: the same fixtures decode to the same samples (read as 32-bit integers, each sample shifted up to fill them); ffprobe gives
  the codec, the sample rate, the channels, the bit depth and, where STREAMINFO counts them, the duration.
- MediaInfo 26.01 (mediainfo_tool.py): the audio stream's format, sample rate, channels and, where STREAMINFO counts the samples, duration.

- `flac -a -F` (analyze, continue through errors) lists the offset of every frame flac decodes. For a fixture `flac -t` accepts the offsets must equal
  the manifest's. For a damaged one flac reports its read position after an error and not always a frame start, and loses sync differently, so the notes
  show where the two readings part and are not evidence of frame starts: the frames the manifest calls intact that flac does not list, and the offsets
  flac lists that the manifest does not, are only printed ("note"), as the places where flac's and Encspect's rules differ (flac takes a header with
  a good CRC-8 for a frame wherever it is, and does not follow a SEEK tag). The manifests of damaged files are decided by the model's reading of
  docs/formats/flac.md, not by a tool (docs/development.md).

Every skipped check prints its reason. Disagreements fail the run (exit status 1).

    python3 scripts/fixtures/crosscheck_flac_fixtures.py [name ...]
"""

import hashlib
import json
import pathlib
import re
import subprocess
import sys
import tempfile

import flac_model as m
import mediainfo_tool as mi
import tool_versions

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "flac"
SKIPS = {}

# What ffprobe and MediaInfo do not report for a fixture, and why.
NO_DURATION = "STREAMINFO counts no samples, so the tools have no length to report"


def skip(name, check, reason):
    SKIPS.setdefault((check, reason), []).append(name)


def run(arguments, **kw):
    return subprocess.run(arguments, capture_output=True, **kw)


def find(manifest):
    stem = manifest.name[:-len(".expected.json")]
    return next(p for p in DIRECTORY.glob(stem + ".*") if not p.name.endswith(".json"))


def expected_samples(path):
    """The decoded samples of the file as the model gives them: (signed integers per channel-interleaved sample, bits, channels)."""
    stream = m.Stream(path.read_bytes() if path.read_bytes()[:4] != b"OggS" else native_of(path))
    pcm, _ = stream.pcm()
    bits = stream.info["bits"]
    width = (bits + 7) // 8
    values = [int.from_bytes(pcm[i:i + width], "little", signed=True) for i in range(0, len(pcm), width)]
    return values, bits, stream.info["channels"]


def native_of(path):
    """An Ogg FLAC file as a native stream: the marker, the header packets' blocks and the frames."""
    import ogg_model as o
    data = path.read_bytes()
    pages, _ = o.read_pages(data)
    packets = next(iter(o.stream_packets(data, pages).values()))["packets"]
    return b"fLaC" + packets[0]["data"][13:] + b"".join(p["data"] for p in packets[1:])


def check_flac_test(name, manifest, path, problems):
    expected = manifest["crosscheck"]["flac_test"]
    result = run(["flac", "-t", "-s", str(path)])
    passed = result.returncode == 0
    if expected is None:
        skip(name, "flac -t", "the fixture's fault is none that flac -t reports, or none it reports the same way in every version")
        return passed
    if passed != expected:
        problems.append(f"flac -t {'accepts' if passed else 'rejects'} the file (expected the opposite): {result.stderr.decode(errors='replace').strip()[:200]}")
    return passed


def check_metaflac(name, manifest, path, problems):
    if path.suffix == ".ogg":
        skip(name, "metaflac", "metaflac reads native FLAC files only")
        return
    info = manifest["stream_info"]
    show = {"--show-min-blocksize": "min_block", "--show-max-blocksize": "max_block", "--show-min-framesize": "min_frame", "--show-max-framesize": "max_frame",
            "--show-sample-rate": "sample_rate", "--show-channels": "channels", "--show-bps": "bits", "--show-total-samples": "total", "--show-md5sum": "md5"}
    for option, key in show.items():
        out = run(["metaflac", option, str(path)]).stdout.decode().strip()
        want = str(info[key])
        if out != want:
            problems.append(f"metaflac {option} says {out}, the manifest {want}")
    listing = run(["metaflac", "--list", str(path)]).stdout.decode()
    blocks = re.findall(r"METADATA block #(\d+)\s+type: (\d+) \((\w+)\)\s+is last: (\w+)\s+length: (\d+)", listing)
    expected = manifest["metadata"]
    if len(blocks) != len(expected):
        problems.append(f"metaflac lists {len(blocks)} metadata blocks, the manifest {len(expected)}")
        return
    for (number, kind, label, last, length), want in zip(blocks, expected):
        if int(length) != want["body_length"] or (last == "true") != want["is_last"] or (want["type"] in m.BLOCK_TYPES.values() and label != want["type"]):
            problems.append(f"metaflac block {number}: {label}, last {last}, {length} bytes; the manifest {want['type']}, last {want['is_last']}, {want['body_length']} bytes")


def wav_data(raw):
    """The data chunk of a RIFF/WAVE file, found by walking its chunks (a chunk ID can also occur inside another chunk)."""
    at = 12
    while at + 8 <= len(raw):
        size = int.from_bytes(raw[at + 4:at + 8], "little")
        if raw[at:at + 4] == b"data":
            return raw[at + 8:at + 8 + size] if size != 0xFFFFFFFF else raw[at + 8:]
        at += 8 + size + (size & 1)
    raise ValueError("the WAVE file has no data chunk")


def check_decode(name, manifest, path, work, problems):
    if "decoded_pcm_md5" not in manifest:
        return
    bits = manifest["stream_info"]["bits"]
    width = (bits + 7) // 8
    if bits in (8, 16, 24, 32):
        out = work / "flac.raw"
        options = ["--force-raw-format", "--endian=little", "--sign=signed"]
    else:
        out = work / "flac.wav"
        options = []
    result = run(["flac", "-d", "-s", "-f", *options, "-o", str(out), str(path)])
    if result.returncode != 0:
        problems.append(f"flac -d fails: {result.stderr.decode(errors='replace').strip()[:200]}")
        return
    raw = out.read_bytes()
    if out.suffix == ".wav":
        # flac writes the bit depths raw output refuses as WAVE_FORMAT_EXTENSIBLE with the samples left-aligned in whole bytes.
        raw = wav_data(raw)
        shift = width * 8 - bits
        raw = b"".join((int.from_bytes(raw[i:i + width], "little", signed=True) >> shift).to_bytes(width, "little", signed=True) for i in range(0, len(raw) - width + 1, width))
    digest = hashlib.md5(raw).hexdigest()
    if digest != manifest["decoded_pcm_md5"]:
        problems.append(f"flac -d decodes to {digest}, the manifest says {manifest['decoded_pcm_md5']}")


def check_ffmpeg(name, manifest, path, problems):
    if "decoded_pcm_md5" not in manifest:
        return
    values, bits, channels = expected_samples(path)
    shift = 32 - bits
    expected = b"".join(((v << shift) & 0xFFFFFFFF).to_bytes(4, "little") for v in values)
    result = run(["ffmpeg", "-v", "error", "-i", str(path), "-map", "0:a:0", "-f", "s32le", "-"])
    if result.returncode != 0:
        problems.append(f"ffmpeg fails: {result.stderr.decode(errors='replace').strip()[:200]}")
        return
    if result.stdout != expected:
        problems.append(f"ffmpeg decodes {len(result.stdout)} bytes that differ from the manifest's samples ({len(expected)} bytes)")
    probe = run(["ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", str(path)])
    document = json.loads(probe.stdout or b"{}")
    stream = next((s for s in document.get("streams", []) if s.get("codec_type") == "audio"), None)
    info = manifest["stream_info"]
    if stream is None:
        problems.append("ffprobe finds no audio stream")
        return
    for key, want in (("codec_name", "flac"), ("sample_rate", str(info["sample_rate"])), ("channels", info["channels"])):
        if stream.get(key) != want:
            problems.append(f"ffprobe {key} is {stream.get(key)}, the manifest {want}")
    if stream.get("bits_per_raw_sample") is not None and str(stream.get("bits_per_raw_sample")) != str(info["bits"]):
        problems.append(f"ffprobe bits_per_raw_sample is {stream.get('bits_per_raw_sample')}, the manifest {info['bits']}")
    if info["total"] == 0:
        skip(name, "ffprobe duration", NO_DURATION)
    else:
        duration = float(stream.get("duration") or document.get("format", {}).get("duration") or -1)
        if abs(duration - info["total"] / info["sample_rate"]) > 0.001:
            problems.append(f"ffprobe duration is {duration}, STREAMINFO counts {info['total'] / info['sample_rate']}")


def check_mediainfo(name, manifest, path, problems):
    info = manifest["stream_info"]
    tracks = mi.read_or_problem(path, problems)
    if tracks is None:
        return
    audio = tracks["Audio"][0] if tracks["Audio"] else None
    if audio is None:
        problems.append("MediaInfo finds no audio stream")
        return
    for key, want in (("Format", "FLAC"), ("SamplingRate", str(info["sample_rate"])), ("Channels", str(info["channels"]))):
        if audio.get(key) != want:
            problems.append(f"MediaInfo {key} is {audio.get(key)}, the manifest {want}")
    if info["total"] == 0:
        skip(name, "MediaInfo duration", NO_DURATION)
    else:
        if audio.get("Duration") is None:
            skip(name, "MediaInfo duration", "MediaInfo reports no duration")
            return
        duration = float(audio.get("Duration"))
        if abs(duration - info["total"] / info["sample_rate"]) > 0.002:
            problems.append(f"MediaInfo duration is {duration}, STREAMINFO counts {info['total'] / info['sample_rate']}")


def check_frames(name, manifest, path, work, problems):
    if path.suffix == ".ogg":
        skip(name, "flac -a", "flac reads native FLAC files only")
        return
    copy = work / "analyze.flac"
    copy.write_bytes(path.read_bytes())
    analysis = work / "analyze.ana"
    analysis.unlink(missing_ok=True)
    run(["flac", "-a", "-F", "-s", str(copy)])
    if not analysis.exists():
        skip(name, "flac -a", "flac reads no frame of the file")
        return
    seen = [int(x) for x in re.findall(r"^frame=\d+\toffset=(\d+)", analysis.read_text(), re.M)]
    frames = {f["offset"]: f for f in manifest["frames"]}
    intact = sorted(o for o, f in frames.items() if not f["status"])
    if manifest["crosscheck"]["flac_test"] is True:
        if sorted(set(seen)) != intact:
            problems.append(f"flac lists the frames at {sorted(set(seen))}, the manifest {intact}")
        return
    missing = [o for o in intact if o not in seen]
    extra = sorted(set(o for o in seen if o not in frames))
    if missing or extra:
        print(f"note {name}: intact in the manifest but not decoded by flac {missing[:4]}; decoded by flac but not in the manifest {extra[:4]}")


def main():
    tool_versions.require("flac", "metaflac", "ffmpeg", "ffprobe")
    mi.require()
    chosen = set(a for a in sys.argv[1:] if not a.startswith("-"))
    manifests = sorted(DIRECTORY.glob("*.expected.json"))
    failures = 0
    checked = 0
    with tempfile.TemporaryDirectory() as folder:
        work = pathlib.Path(folder)
        for manifest_path in manifests:
            name = manifest_path.name[:-len(".expected.json")]
            if chosen and name not in chosen:
                continue
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            path = find(manifest_path)
            problems = []
            passed = check_flac_test(name, manifest, path, problems)
            check_frames(name, manifest, path, work, problems)
            valid = manifest["crosscheck"]["flac_test"] is True and passed
            if valid:
                check_metaflac(name, manifest, path, problems)
                check_decode(name, manifest, path, work, problems)
                if manifest["crosscheck"].get("ffmpeg", True):
                    check_ffmpeg(name, manifest, path, problems)
                else:
                    skip(name, "FFmpeg", "FFmpeg refuses frames larger than STREAMINFO's maximum block size")
                check_mediainfo(name, manifest, path, problems)
            else:
                skip(name, "decoders and header readers", "the fixture is damaged or unusual, so the tools do not agree on what it holds")
            checked += 1
            if problems:
                failures += 1
                print(f"FAIL {name}")
                for problem in problems:
                    print(f"    {problem}")
    for (check, reason), names in sorted(SKIPS.items()):
        print(f"skipped {check} for {len(names)} fixtures: {reason}")
    print(f"{checked} fixtures checked, {failures} with disagreements.")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
