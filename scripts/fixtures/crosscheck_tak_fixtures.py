#!/usr/bin/env python3
"""Checks the TAK fixtures' manifests against independent tools and against the records of the machine that wrote them.

For every manifest in tests/fixtures/tak (import_windows_tak_fixtures.py, make_tak_fixtures.py), with FFmpeg 8.0.1 and MediaInfo 26.01 of Ubuntu 26.04 as black boxes
(docs/design/tak-optimfrog-headers.md, "Cross-checks"; no source of either is read for the parser: FFmpeg's TAK decoder is LGPL, MediaInfo's File_Tak.cpp is BSD):

- The records of the Windows run (the `.provenance.json` files: `Takc -fi` and the SDK library's stream info and encoder info): the codec, profile, preset, evaluation,
  encoder version, frame length, sample count, rate, bits, channels, the wave data sizes and the MD5 must equal the manifest's.
- ffprobe opens an encoder-written file and gives the codec "tak", the rate, the channels, the bits per raw sample and the duration (the stream info's samples over the rate).
  For a hand-built file whose CRC-24 of an object fails, its message names the object ("<id> metadata block CRC error"): the CRC-24 placement of the manifest is the one a reader
  checks. Other hand-built files are opened or refused as ffprobe pleases, and the outcome is printed, not asserted.
- FFmpeg decodes each encoder-written file with `-err_detect crccheck` and no error, and the samples equal the signal that was encoded (tak_cases.py regenerates it; the lossyWAV
  file's input is lossyWAV's output and is not regenerated). A control flips one bit of the last byte of the last frame (its data CRC-24) and FFmpeg must report "CRC error".
- MediaInfo on the encoder-written files: the format "TAK", the sample rate, the bit depth, the channels where they are 1 or 2, the duration (to a millisecond), and the encoder:
  Encoded_Library "TAK", Encoded_Library_Version and Encoded_Library_Settings, which File_Tak.cpp reads from the encoder info object ("-p2e"). Its expected differences, each from its
  reading of the stream info: the channel count is read as one bit (mono or stereo), so a file of more channels is skipped; the samples are worked out from the duration and
  are not compared.

Every skipped check prints its reason. Disagreements fail the run (exit status 1).

    python3 scripts/fixtures/crosscheck_tak_fixtures.py [name ...]
"""

import json
import pathlib
import subprocess
import sys
import tempfile

import mediainfo_tool as mi
import tak_cases
import tool_versions

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "tak"
SKIPS = {}
NOTES = []
PCM_FORMATS = {8: ("u8", "u8"), 16: ("s16le", "s16le"), 24: ("s24le", "s24le")}


def skip(name, check, reason):
    SKIPS.setdefault((check, reason), []).append(name)


def run(arguments, **kw):
    return subprocess.run(arguments, capture_output=True, **kw)


def loads(output):
    try:
        return json.loads(output or b"{}")
    except json.JSONDecodeError:
        return {}


def check_records(name, manifest, provenance, problems):
    """The Windows run's records against the manifest: what Takc and the SDK library read from the committed bytes."""
    sdk = provenance["sdk"]
    stream = manifest["stream_info"]
    pairs = [("codec", sdk["codec"], stream["codec"]), ("profile", sdk["profile"], stream["profile"]), ("frame length", sdk["frameSizeInSamples"], stream["frame_length"]),
             ("frame size type", sdk["frameSizeType"], stream["frame_size_type"]), ("samples", sdk["sampleCount"], stream["samples"]), ("rate", sdk["sampleRate"], stream["sample_rate"]),
             ("bits per sample", sdk["bitsPerSample"], stream["bits_per_sample"]), ("channels", sdk["channels"], stream["channels"])]
    encoder = manifest["encoder_info"]
    if encoder is not None:
        pairs += [("encoder version", sdk["encoderVersion"], encoder["version"]), ("preset", sdk["preset"], encoder["preset"]), ("evaluation", sdk["evaluation"], encoder["evaluation"])]
    wave = manifest["wave_data"]
    if wave is None:
        pairs.append(("wave data result", sdk["waveDataResult"], 1))
    else:
        pairs += [("wave header size", sdk["waveHeaderSize"], wave["header_size"]), ("wave footer size", sdk["waveFooterSize"], wave["footer_size"])]
    pairs.append(("MD5", sdk.get("md5"), manifest["md5"]))
    if stream.get("speakers"):
        pairs.append(("speaker assignment", sdk["speakerAssignment"], stream["speakers"]))
    for label, found, expected in pairs:
        if found != expected:
            problems.append(f"the Windows run's record says {label} {found}; the manifest {expected}")
    compact = provenance["info"]["compactRaw"].split(";")
    # Takc -fim5: sample type; rate; bits; channels; samples; MD5; encoder version; codec; preset; frame size; wave meta data present.
    if compact[1:5] != [str(stream["sample_rate"]), str(stream["bits_per_sample"]), str(stream["channels"]), str(stream["samples"])] or int(compact[7]) != stream["codec"] \
            or int(compact[9]) != stream["frame_length"]:
        problems.append(f"Takc -fi -fim5 says {provenance['info']['compactRaw']}, which differs from the manifest's stream info")
    if encoder is not None and compact[6] != encoder["version"]:
        problems.append(f"Takc -fi -fim5 gives the encoder version {compact[6]}, the manifest {encoder['version']}")
    if encoder is not None and compact[8] != encoder["name"][2:]:
        problems.append(f"Takc -fi -fim5 gives the preset {compact[8]}, the manifest {encoder['name'][2:]}")


def check_ffprobe(name, manifest, path, problems):
    probe = run(["ffprobe", "-v", "warning", "-show_streams", "-show_format", "-of", "json", str(path)])
    document = loads(probe.stdout)
    stream = next((s for s in document.get("streams", []) if s.get("codec_type") == "audio"), None)
    stderr = probe.stderr.decode(errors="replace")
    findings = {f["code"] for f in manifest["findings"]}
    if "tak.object-crc-mismatch" in findings:
        bad = [o for o in manifest["objects"] if o["crc_ok"] is False]
        for o in bad:
            if f"{o['id']} metadata block CRC error" not in stderr:
                problems.append(f"ffprobe does not report a CRC error for object {o['id']}: {stderr.strip()[:200]}")
    if not name.startswith("external-"):
        NOTES.append(f"{name}: ffprobe {'opens the file' if stream else 'refuses the file'}")
        return stream is not None and not findings
    if stream is None:
        problems.append(f"ffprobe refuses the file: {stderr.strip()[:200]}")
        return False
    info = manifest["stream_info"]
    for key, want in (("codec_name", "tak"), ("sample_rate", str(info["sample_rate"])), ("channels", info["channels"])):
        if stream.get(key) != want:
            problems.append(f"ffprobe {key} is {stream.get(key)}, the manifest {want}")
    if str(stream.get("bits_per_raw_sample")) != str(info["bits_per_sample"]):
        problems.append(f"ffprobe bits_per_raw_sample is {stream.get('bits_per_raw_sample')}, the manifest {info['bits_per_sample']}")
    duration = float(stream.get("duration") or document.get("format", {}).get("duration") or -1)
    if abs(duration - info["samples"] / info["sample_rate"]) > 0.001:
        problems.append(f"ffprobe duration is {duration}, the stream info counts {info['samples'] / info['sample_rate']}")
    return True


def signal_of(provenance):
    """The WAV data of the signal that was encoded, or None for the lossyWAV file."""
    name = provenance["input"]["name"]
    if provenance["input"].get("lossywav") or name not in tak_cases.INPUTS:
        return None
    rate, channels, bits, frames, kind, options = tak_cases.INPUTS[name]
    wav = tak_cases.wav_bytes(rate, channels, bits, frames, kind, options)
    at = wav.index(b"data") + 8
    return wav[at:at + frames * channels * bits // 8]


def check_decode(name, manifest, provenance, path, work, problems):
    result = run(["ffmpeg", "-v", "error", "-err_detect", "crccheck", "-i", str(path), "-f", "null", "-"])
    if result.returncode != 0 or result.stderr.strip():
        problems.append(f"ffmpeg does not decode the file cleanly: {result.stderr.decode(errors='replace').strip()[:200]}")
        return
    expected = signal_of(provenance)
    if expected is None:
        skip(name, "FFmpeg samples", "the input is lossyWAV's output, which this script does not regenerate")
    else:
        codec, container = PCM_FORMATS[manifest["stream_info"]["bits_per_sample"]]
        out = work / "decoded.raw"
        out.unlink(missing_ok=True)
        result = run(["ffmpeg", "-v", "error", "-i", str(path), "-f", container, "-acodec", f"pcm_{codec}", "-y", str(out)])
        if not out.exists() or out.read_bytes() != expected:
            problems.append(f"ffmpeg decodes samples other than the signal that Takc encoded: {result.stderr.decode(errors='replace').strip()[:200]}")
    # One bit flipped in the last byte of the last frame, the end of its data CRC-24, is a CRC error to FFmpeg: the frame carries a CRC-24 over its coded bytes at its end.
    data = bytearray(path.read_bytes())
    data[manifest["end"] - 1] ^= 1
    flipped = work / "flipped.tak"
    flipped.write_bytes(data)
    result = run(["ffmpeg", "-v", "warning", "-err_detect", "crccheck", "-i", str(flipped), "-f", "null", "-"])
    if b"CRC error" not in result.stderr:
        problems.append(f"ffmpeg does not report a CRC error for a flipped bit in the last frame's CRC-24: {result.stderr.decode(errors='replace').strip()[:200]}")


def check_mediainfo(name, manifest, path, problems):
    tracks = mi.read_or_problem(path, problems)
    if tracks is None:
        return
    audio = tracks["Audio"][0] if tracks["Audio"] else None
    if audio is None:
        problems.append("MediaInfo finds no audio stream")
        return
    info = manifest["stream_info"]
    expected = [("Format", "TAK"), ("SamplingRate", str(info["sample_rate"])), ("BitDepth", str(info["bits_per_sample"]))]
    if info["channels"] <= 2:
        expected.append(("Channels", str(info["channels"])))
    else:
        skip(name, "MediaInfo channels", "File_Tak.cpp reads the channel count as one bit (mono or stereo)")
    encoder = manifest["encoder_info"]
    if encoder is not None:
        expected += [("Encoded_Library", "TAK"), ("Encoded_Library_Version", encoder["version"]), ("Encoded_Library_Settings", encoder["name"])]
    for key, want in expected:
        if audio.get(key) != want:
            problems.append(f"MediaInfo {key} is {audio.get(key)}, the manifest {want}")
    duration = float(audio.get("Duration", -1))
    if abs(duration - info["samples"] / info["sample_rate"]) > 0.002:
        problems.append(f"MediaInfo duration is {duration}, the stream info counts {info['samples'] / info['sample_rate']}")
    skip(name, "MediaInfo samples", "MediaInfo works the sample count out from the duration, in milliseconds")


def main():
    tool_versions.require("ffmpeg", "ffprobe")
    mi.require()
    chosen = set(a for a in sys.argv[1:] if not a.startswith("-"))
    failures = 0
    checked = 0
    with tempfile.TemporaryDirectory() as folder:
        work = pathlib.Path(folder)
        for manifest_path in sorted(DIRECTORY.glob("*.expected.json")):
            name = manifest_path.name[:-len(".expected.json")]
            if chosen and name not in chosen:
                continue
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            path = DIRECTORY / (name + ".tak")
            problems = []
            is_external = name.startswith("external-")
            opened = check_ffprobe(name, manifest, path, problems)
            if is_external:
                provenance = json.loads((DIRECTORY / (name + ".tak.provenance.json")).read_text(encoding="utf-8"))
                check_records(name, manifest, provenance, problems)
                check_decode(name, manifest, provenance, path, work, problems)
                if opened:
                    check_mediainfo(name, manifest, path, problems)
            else:
                skip(name, "FFmpeg decoding, MediaInfo and the Windows records", "the file is hand-built from an encoder-written one, so only the objects it changes are checked")
            checked += 1
            if problems:
                failures += 1
                print(f"FAIL {name}")
                for problem in problems:
                    print(f"    {problem}")
    for note in NOTES:
        print(note)
    for (check, reason), names in sorted(SKIPS.items()):
        print(f"skipped {check} for {len(names)} fixtures: {reason}")
    print(f"{checked} fixtures checked, {failures} with disagreements.")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
