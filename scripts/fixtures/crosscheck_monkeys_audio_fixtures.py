#!/usr/bin/env python3
"""Checks the Monkey's Audio fixtures' manifests against independent tools.

For every manifest in tests/fixtures/monkeys-audio (make_external_monkeys_audio_fixtures.py, make_interim_monkeys_audio_fixture.py,
make_legacy_monkeys_audio_fixtures.py, make_monkeys_audio_fixtures.py):

- mac -V (the console tool of the Monkey's Audio SDK 13.42, built from the pinned source by monkeys_audio_tool.py): verifies the file, decoding every frame and
  comparing its CRC. It succeeds or fails as the manifest's crosscheck.mac_verify says; a null there means the fixture's fault is none that mac -V reports
  the way the analyzer does (the descriptor MD5, which mac -V does not compare, or a header the SDK refuses where the analyzer still reads the file): not compared.
  The SDK's own decoder skips the seventh channel of a seven-channel file, so mac -V fails for it, as the analyzer's decoder does.
- mac -d, on the fixtures mac -V accepts: the decoded samples, in the layout of the manifest's decoded_pcm_md5 (little-endian signed integers of as many whole
  bytes as the bit depth needs; the unsigned bytes of an 8-bit WAV file made signed), have that MD5, which the signal that went into the encoder gives.
- FFmpeg 8.0.1: the same fixtures decode to the same samples where FFmpeg reads them (mono and stereo of 8, 16 and 24 bits, files of versions 3.93 to 3.99 that
  start with their descriptor or, before 3.98, with the old header); ffprobe gives the codec, the sample rate, the channels and the duration.
- MediaInfo 26.01 (mediainfo_tool.py): the audio stream's format, sample rate, channels, bit depth and duration, where it reads the file.
- The descriptor MD5 is worked out again here with hashlib in the SDK's order (MACLib.cpp, VerifyFileW2) and compared with the manifest's.

Every skipped check prints its reason. Disagreements fail the run (exit status 1).

    python3 scripts/fixtures/crosscheck_monkeys_audio_fixtures.py [name ...]
"""

import hashlib
import json
import pathlib
import subprocess
import sys
import tempfile

import mediainfo_tool as mi
import monkeys_audio_model as m
import monkeys_audio_tool
import tool_versions

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "monkeys-audio"
SKIPS = {}


def skip(name, check, reason):
    SKIPS.setdefault((check, reason), []).append(name)


def run(arguments, **kw):
    return subprocess.run(arguments, capture_output=True, **kw)


def find(manifest):
    stem = manifest.name[:-len(".expected.json")]
    return next(p for p in DIRECTORY.glob(stem + ".*") if not p.name.endswith(".json"))


def wav_data(raw):
    """The data chunk of a RIFF/WAVE file, found by walking its chunks (a chunk ID can also occur inside another chunk)."""
    at = 12
    while at + 8 <= len(raw):
        size = int.from_bytes(raw[at + 4:at + 8], "little")
        if raw[at:at + 4] == b"data":
            return raw[at + 8:at + 8 + size] if size != 0xFFFFFFFF else raw[at + 8:]
        at += 8 + size + (size & 1)
    raise ValueError("the WAVE file has no data chunk")


def aiff_data(raw, bits):
    """The samples of the SSND chunk of an AIFF file (mac writes the format the audio came from), made little-endian."""
    at = 12
    while at + 8 <= len(raw):
        size = int.from_bytes(raw[at + 4:at + 8], "big")
        if raw[at:at + 4] == b"SSND":
            body = raw[at + 16:at + 8 + size]
            width = (bits + 7) // 8
            return b"".join(body[i:i + width][::-1] for i in range(0, len(body), width))
        at += 8 + size + (size & 1)
    raise ValueError("the AIFF file has no SSND chunk")


def canonical(raw, bits, is_float):
    """The samples of WAV data in the layout of decoded_pcm_md5: signed, little-endian, ceil(bits / 8) bytes. 8-bit WAV data is unsigned."""
    if bits == 8 and not is_float:
        return bytes((b - 128) & 0xFF for b in raw)
    return raw


def check_md5(name, manifest, path, problems):
    data = path.read_bytes()
    a = m.parse(data)
    if a.terminating_end > len(data):
        skip(name, "MD5 again", "the file ends before the data the descriptor counts")
        return
    computed = m.compute_md5(data, a).hex()
    if computed != manifest["descriptor"]["md5_computed"]:
        problems.append(f"the MD5 computed again is {computed}, the manifest {manifest['descriptor']['md5_computed']}")


def check_verify(name, manifest, path, mac, problems):
    expected = manifest["crosscheck"]["mac_verify"]
    code, text = monkeys_audio_tool.run(mac, path, "-V")
    passed = code == 0
    if expected is None:
        skip(name, "mac -V", "the fixture's fault is none that mac -V reports the way the analyzer does")
        return passed
    if passed != expected:
        problems.append(f"mac -V {'accepts' if passed else 'rejects'} the file (expected the opposite): {text.strip().splitlines()[-1] if text.strip() else code}")
    return passed


def check_decode(name, manifest, path, mac, work, problems):
    if name == "built-seek-table-bytes-odd":
        skip(name, "mac -d", "the SDK reads the WAV header data from the end of the whole entries of the seek table, a byte before the analyzer does when the table's size is not a multiple of four; the audio is the same")
        return
    if "decoded_pcm_md5" not in manifest:
        skip(name, "mac -d", "the manifest holds no decoded audio")
        return
    out = work / "mac.wav"
    out.unlink(missing_ok=True)
    code, text = monkeys_audio_tool.run(mac, path, out, "-d")
    if code != 0 or not out.exists():
        problems.append(f"mac -d fails: {text.strip().splitlines()[-1] if text.strip() else code}")
        return
    info = manifest["stream_infos"][0]
    is_float = bool(manifest["header"]["flags"] & 4096)
    try:
        written = out.read_bytes()
        raw = canonical(aiff_data(written, info["bits_per_sample"]) if written[:4] == b"FORM" else wav_data(written), info["bits_per_sample"], is_float)
    except ValueError as error:
        problems.append(f"mac -d writes a file that is no WAVE file with samples: {error}")
        return
    digest = hashlib.md5(raw).hexdigest()
    if digest != manifest["decoded_pcm_md5"]:
        problems.append(f"mac -d decodes to {digest}, the manifest says {manifest['decoded_pcm_md5']}")


def ffmpeg_reads(manifest, data):
    """Why FFmpeg is not asked to decode the fixture, or None where it is."""
    info = manifest["stream_infos"][0]
    if info["channels"] > 2:
        return "FFmpeg decodes mono and stereo only"
    if info["bits_per_sample"] not in (8, 16, 24):
        return "FFmpeg decodes 8, 16 and 24 bits only"
    if manifest["header"]["flags"] & 4096 or data[:4] == b"MACF":
        return "FFmpeg does not read floating-point files"
    version = manifest["descriptor"]["version"] if "descriptor" in manifest else manifest["file_version"]
    if version < 3930 or 3990 < version:
        return "FFmpeg decodes files of versions 3.93 to 3.99 (the versions the decoder reads) and refuses the others it is given, or decodes other audio than the SDK"
    if info["sample_rate"] == 0:
        return "FFmpeg refuses a file with no sample rate"
    if "descriptor" in manifest and (manifest["descriptor"]["descriptor_bytes"] != 52 or manifest["descriptor"]["header_bytes"] != 24):
        return "FFmpeg decodes other audio than the SDK from a file whose descriptor states a size for itself or the header that the format does not define (seen with FFmpeg 6.1.1 on two of the three such fixtures; not examined further)"
    if manifest["regions"][0]["name"] == "Junk":
        return "FFmpeg does not look for the descriptor past bytes in front of it"
    return None


def check_ffmpeg(name, manifest, path, problems):
    info = manifest["stream_infos"][0]
    bits = info["bits_per_sample"]
    reason = ffmpeg_reads(manifest, path.read_bytes())
    if reason:
        skip(name, "FFmpeg", reason)
        return
    result = run(["ffmpeg", "-v", "error", "-i", str(path), "-map", "0:a:0", "-f", "s32le", "-"])
    if result.returncode != 0:
        problems.append(f"ffmpeg fails: {result.stderr.decode(errors='replace').strip()[:200]}")
        return
    shift = 32 - bits
    width = (bits + 7) // 8
    values = [int.from_bytes(result.stdout[i:i + 4], "little", signed=True) >> shift for i in range(0, len(result.stdout), 4)]
    raw = b"".join(v.to_bytes(width, "little", signed=True) for v in values)
    if hashlib.md5(raw).hexdigest() != manifest["decoded_pcm_md5"]:
        problems.append("ffmpeg decodes samples that differ from the manifest's")
    probe = run(["ffprobe", "-v", "error", "-show_streams", "-of", "json", str(path)])
    stream = next((s for s in json.loads(probe.stdout or b"{}").get("streams", []) if s.get("codec_type") == "audio"), None)
    if stream is None:
        problems.append("ffprobe finds no audio stream")
        return
    for key, want in (("codec_name", "ape"), ("sample_rate", str(info["sample_rate"])), ("channels", info["channels"])):
        if stream.get(key) != want:
            problems.append(f"ffprobe {key} is {stream.get(key)}, the manifest {want}")
    if stream.get("duration") is not None and abs(float(stream["duration"]) - info["duration"]) > 0.002:
        problems.append(f"ffprobe duration is {stream['duration']}, the manifest {info['duration']}")


def check_mediainfo(name, manifest, path, problems):
    if not manifest["stream_infos"]:
        skip(name, "MediaInfo", "the file has no stream to report")
        return
    info = manifest["stream_infos"][0]
    try:
        tracks = mi.read(path)
    except mi.Failed:
        skip(name, "MediaInfo", "MediaInfo reads nothing of the file")
        return
    audio = tracks["Audio"][0] if tracks["Audio"] else None
    if audio is None:
        skip(name, "MediaInfo", "MediaInfo finds no audio stream (it looks for the descriptor at the start of the file only, and refuses a header with no sample rate or channels)")
        return
    if audio.get("Format") != "Monkey's Audio":
        problems.append(f"MediaInfo Format is {audio.get('Format')}")
    for key, want in (("SamplingRate", str(info["sample_rate"])), ("Channels", str(info["channels"])), ("BitDepth", str(info["bits_per_sample"]))):
        if audio.get(key) != want:
            problems.append(f"MediaInfo {key} is {audio.get(key)}, the manifest {want}")
    if manifest.get("header", {}).get("total_frames") == 0:
        skip(name, "MediaInfo duration", "the header counts no frame, and MediaInfo works the duration out from a count that is not there")
    elif audio.get("Duration") is None:
        skip(name, "MediaInfo duration", "MediaInfo reports no duration")
    elif abs(float(audio["Duration"]) - info["duration"]) > 0.002:
        problems.append(f"MediaInfo duration is {audio['Duration']}, the manifest {info['duration']}")


def main():
    tool_versions.require("ffmpeg", "ffprobe")
    mi.require()
    chosen = set(a for a in sys.argv[1:] if not a.startswith("-"))
    manifests = sorted(DIRECTORY.glob("*.expected.json"))
    failures = 0
    checked = 0
    mac = monkeys_audio_tool.build_mac(pathlib.Path(tempfile.gettempdir()) / "monkeys-audio-sdk")
    with tempfile.TemporaryDirectory() as folder:
        work = pathlib.Path(folder)
        for manifest_path in manifests:
            name = manifest_path.name[:-len(".expected.json")]
            if chosen and name not in chosen:
                continue
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            path = find(manifest_path)
            problems = []
            if "descriptor" in manifest and manifest["descriptor"].get("md5_computed") is not None:
                check_md5(name, manifest, path, problems)
            passed = check_verify(name, manifest, path, mac, problems)
            if manifest["crosscheck"]["mac_verify"] is True and passed:
                check_decode(name, manifest, path, mac, work, problems)
                check_ffmpeg(name, manifest, path, problems)
            else:
                skip(name, "mac -d and FFmpeg", "the fixture is damaged or unusual, so the tools do not agree on what it holds")
            check_mediainfo(name, manifest, path, problems)
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
