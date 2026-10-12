#!/usr/bin/env python3
"""Checks the Musepack fixtures' manifests against independent tools.

For every manifest in tests/fixtures/musepack (make_musepack_fixtures.py,
make_external_musepack_fixtures.py):

- mpcdec -i (musepack-tools 0.1~r495): the stream version, the encoder as libmpcdec names it, the
  profile, mid/side, true gapless, the sample rate, the channels and the length in samples; and the
  samples mpcdec writes when it decodes the file.
- ffprobe: the codec, sample rate and channels, and the packets ffmpeg's demuxers read: for SV7 one
  per frame the header counts, at the 32-bit word that holds its first bit; for SV8 one per audio
  packet, at its payload and of its payload's size.
- mpcdec_frames (musepack_tool.py: libmpcdec r495 built from Debian's pinned source): for SV7, each
  frame's bits, used subbands, mid/side subbands and noise-filled subband channels as the decoder
  reads them.
- mpc2sv8: each SV7 fixture converted to SV8 decodes to the manifest's samples.

Fixtures that are not "valid" say why the others do not apply; every skipped check prints its
reason. Disagreements fail the run.
"""

import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile

import musepack_model as m
import musepack_tool

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "musepack"
SKIPS = []

# Hand-built fixtures that the tools read as they would a valid file, and what each damaged one
# should make mpcdec do.
READABLE = {"sv8-tags", "sv7-tags", "sv8-unknown-key", "sv8-no-replaygain", "sv8-data-after-end", "sv8-end-size", "sv7-trailing-data",
            "sv8-seek-mismatch"}
REFUSED = {"sv8-bad-crc": "libmpcdec refuses a stream header whose CRC fails", "sv8-rate-reserved": "libmpcdec refuses a reserved sample rate",
           "sv7-header-fields": "libmpcdec refuses a max band of 0", "sv7-header-truncated": "the header is cut short"}
SHORT = {"sv7-truncated", "sv8-truncated", "sv7-damaged-length"}


def skip(name, check, reason):
    SKIPS.append(f"{name}: {check} skipped: {reason}")


def run(arguments):
    return subprocess.run(arguments, capture_output=True)


def mpcdec_info(path):
    result = run(["mpcdec", "-i", str(path)])
    text = (result.stdout + result.stderr).decode("utf-8", "replace")
    fields = dict(re.findall(r"^([a-z/ ]+): (.*)$", text, re.M))
    if version := re.search(r"^stream version (\d+)$", text, re.M):
        fields["stream version"] = version.group(1)
    return result.returncode, fields


def mpcdec_samples(path):
    with tempfile.TemporaryDirectory() as work:
        out = pathlib.Path(work) / "out.wav"
        result = run(["mpcdec", str(path), str(out)])
        if not out.exists() or out.stat().st_size < 44:
            return None
        data = out.read_bytes()
        channels = int.from_bytes(data[22:24], "little")
        return (len(data) - 44) // (2 * channels), result.returncode


def ffprobe(path):
    result = run(["ffprobe", "-v", "error", "-show_streams", "-show_packets", "-of", "json", str(path)])
    return json.loads(result.stdout or b"{}")


def check_info(name, manifest, path, problems):
    code, info = mpcdec_info(path)
    stream = manifest["stream_infos"][0]
    header = manifest.get("header", {})
    expected = {"stream version": str(manifest["version"]), "samplerate": f"{stream['sample_rate']} Hz", "channels": str(stream["channels"])}
    data = path.read_bytes()
    start = data.find(b"MPCK" if manifest["version"] == 8 else b"MP+")
    if manifest["version"] == 7:
        h = m.sv7_header(m.Words(data[start:]))
        expected["encoder"] = m.libmpcdec_name(h["encoder"]).replace("Alpha", "--Alpha--")
        expected["profile"] = f"'{m.PROFILES[h['profile']]}' (q={h['profile'] - 5:.2f})"
        expected["gapless"] = "on" if h["gapless"] else "off"
        expected["mid/side stereo"] = "on" if h["ms"] else "off"
    else:
        for offset, length, key, declared, size in m.packets(data[start:])[0]:
            if key == "EI":
                p = data[start + offset + size:start + offset + length]
                major, minor, build = p[1], p[2], p[3]
                name = m.ei_name(major, minor, build)
                expected["encoder"] = name.replace("stable ", "--Stable-- ").replace("unstable ", "--Unstable-- ")
        expected["mid/side stereo"] = "on" if header.get("Mid/Side Stereo") == "true" else "off"
    for key, value in expected.items():
        if info.get(key) != value:
            problems.append(f"mpcdec -i {key}: {info.get(key)!r}, manifest {value!r}")
    length = re.search(r"\((\d+) samples\)", info.get("length", ""))
    if not length or int(length.group(1)) != stream["samples"]:
        problems.append(f"mpcdec -i length: {info.get('length')!r}, manifest {stream['samples']} samples")


def check_decode(name, manifest, path, problems, exact=True):
    decoded = mpcdec_samples(path)
    samples = manifest["stream_infos"][0]["samples"]
    if not exact:
        # A decoder also decodes the frames of a cut packet or frame; it may not pass what the header announces.
        header = manifest["header"]
        samples = int(header["Sample Count"].split()[0]) if "Sample Count" in header else int(header["Frame Count"].split()[0]) * m.FRAME
    if decoded is None:
        problems.append("mpcdec wrote no audio")
    elif exact and decoded[0] != samples:
        problems.append(f"mpcdec decoded {decoded[0]} samples, manifest {samples}")
    elif not exact and decoded[0] > samples:
        problems.append(f"mpcdec decoded {decoded[0]} samples from a cut stream, more than the header's {samples}")


def check_ffprobe(name, manifest, path, problems):
    probe = ffprobe(path)
    streams = probe.get("streams", [])
    if len(streams) != 1:
        problems.append(f"ffprobe found {len(streams)} streams")
        return
    stream, info = streams[0], manifest["stream_infos"][0]
    codec = "musepack8" if manifest["version"] == 8 else "musepack7"
    for key, value in (("codec_name", codec), ("sample_rate", str(info["sample_rate"])), ("channels", info["channels"])):
        if stream.get(key) != value:
            problems.append(f"ffprobe {key}: {stream.get(key)!r}, manifest {value!r}")
    packets = probe.get("packets", [])
    data = path.read_bytes()
    start = data.find(b"MPCK" if manifest["version"] == 8 else b"MP+")
    if manifest["version"] == 8:
        expected = [(o + h, d - h) for o, l, k, d, h in m.packets(data[start:])[0] if k == "AP"]
        expected = [(start + o, s) for o, s in expected]
    else:
        s = m.sv7(data[start:])
        count = s["header"]["frames"]
        # ffmpeg's packet is the words that hold the frame, from the one its length starts in, and 4
        # bytes of its own (libavformat/mpc.c mpc_read_packet); it gives no position.
        expected = [(None, ((f["length"] + (f["bit"] & 31) + 20 + 31) & ~31) // 8 + 4) for f in s["frames"][:count]]
    actual = [(int(p["pos"]) if "pos" in p else None, int(p["size"])) for p in packets]
    if manifest["version"] == 8:
        found, stop = m.packets(data[start:])
        past = [a for a in actual if a[0] >= start + stop]
        if past:
            # ffmpeg 6.1 stops at the stream end packet, but takes up reading after it and reads the
            # "AP" of an APE tag's "APETAGEX" as an audio packet where the tag does not end the file.
            skip(name, f"ffprobe's {len(past)} packets after the stream end", "ffmpeg reads the APE tag after the stream end packet as an audio packet")
            actual = [a for a in actual if a[0] < start + stop]
    if len(actual) != len(expected):
        problems.append(f"ffprobe read {len(actual)} packets, manifest {len(expected)}")
    for i, ((pos, size), (want_pos, want_size)) in enumerate(zip(actual, expected)):
        if pos != want_pos or size != want_size:
            problems.append(f"ffprobe packet {i} at {pos} of {size} bytes, manifest at {want_pos} of {want_size}")
            break


def check_frames(name, manifest, path, frames_tool, problems):
    out = run([str(frames_tool), str(path)]).stdout.decode().split("\n")
    decoded = [line.split() for line in out if line]
    expected = manifest["frames"]
    count = int(manifest["header"]["Frame Count"].split()[0])
    if len(decoded) != len(expected):
        problems.append(f"libmpcdec read {len(decoded)} frames, manifest {len(expected)}")
    for i, (want, got) in enumerate(zip(expected, decoded)):
        bits = want[0] + (11 if i == count - 1 else 0)
        if [bits, want[1], want[2], want[3]] != [int(got[0]), int(got[1]), got[2], int(got[3])]:
            problems.append(f"frame {i}: libmpcdec {got}, manifest {want}")
            break


def check_mpc2sv8(name, manifest, path, problems):
    with tempfile.TemporaryDirectory() as work:
        out = pathlib.Path(work) / "sv8.mpc"
        run(["mpc2sv8", str(path), str(out)])
        if not out.exists():
            problems.append("mpc2sv8 wrote nothing")
            return
        decoded = mpcdec_samples(out)
        if decoded is None or decoded[0] != manifest["stream_infos"][0]["samples"]:
            problems.append(f"mpc2sv8's SV8 decodes to {decoded and decoded[0]} samples, manifest {manifest['stream_infos'][0]['samples']}")


def main():
    frames_tool = musepack_tool.build_frames(os.environ.get("MUSEPACK_TOOLS_DIR") and pathlib.Path(os.environ["MUSEPACK_TOOLS_DIR"]) / "frames")
    failures = 0
    for manifest_path in sorted(DIRECTORY.glob("*.expected.json")):
        name = manifest_path.name[:-len(".expected.json")]
        path = DIRECTORY / (name + ".mpc")
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        problems = []
        valid = manifest.get("crosscheck", {}).get("valid") or name in READABLE
        if manifest["version"] not in (7, 8):
            skip(name, "every tool", f"stream version {manifest['version']}: mpcdec, ffmpeg and libmpcdec r495 read only SV7 and SV8")
        elif name in REFUSED:
            code, info = mpcdec_info(path)
            if code == 0 and "samplerate" in info:
                problems.append(f"mpcdec read the stream, though {REFUSED[name]}")
            skip(name, "ffprobe, mpcdec_frames and mpc2sv8", REFUSED[name])
        elif name in SHORT:
            check_decode(name, manifest, path, problems, exact=False)
            skip(name, "mpcdec -i, ffprobe and mpc2sv8", "the stream is cut short or damaged, so the tools stop at different points; mpcdec must not decode more than the header announces")
        elif valid:
            check_info(name, manifest, path, problems)
            check_decode(name, manifest, path, problems)
            check_ffprobe(name, manifest, path, problems)
            if manifest["version"] == 7:
                check_frames(name, manifest, path, frames_tool, problems)
                check_mpc2sv8(name, manifest, path, problems)
        else:
            skip(name, "every tool", "the damage (a tag signature inside the audio, a key libmpcdec stops at, a sample count past the audio or a missing stream end) makes the tools' readings differ by design")
        if problems:
            failures += 1
            print(f"FAIL {name}: " + "; ".join(problems))
        else:
            print(f"ok   {name}")
    for line in SKIPS:
        print(line)
    if failures:
        raise SystemExit(f"{failures} fixtures disagree with the tools")


if __name__ == "__main__":
    main()
