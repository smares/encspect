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
- mpcdec_frames126 (musepack_tool.py: the same program over libmpcdec 1.2.6, which mpcdec, ffmpeg and
  libmpcdec r495 do not reach, as they read only SV7 and SV8): for SV4 to SV6, the header fields
  and whether libmpcdec accepts the stream, then each frame's bits, used subbands and mid/side
  subbands and the samples it outputs.
- mpcdec_frames_portable and mpcdec_portable (musepack_tool.py: the Portable Musepack decoder, the
  C++ code libmpcdec 1.2.6 was ported to C from, so the same reading in an earlier form, not an
  independent one): the same lines as mpcdec_frames126, line for line, and the samples the archive's
  own driver decodes. It catches errors in building either, and in the portability patch. Its
  archive comes from the private repository only, so without ENCSPECT_PRIVATE_TOKEN it is skipped.
- mppdec (Buschmann's decoder of June 2000, Rarewares' Win32 build, under Wine where it is
  installed), the one separate decoder at hand: the frames it decodes from each SV4 to SV6 fixture,
  against the line or count recorded when the fixture was written (in the manifest). For SV6, of which
  no encoder was found, it confirms that the last counted frame is decoded, but not the 11 bits after
  it or the sample count; those rest on libmpcdec 1.2.6's reading alone.

- MediaInfo 24.01 (mediainfo_tool.py, check_mediainfo), on every fixture, against its golden
  report: the format and sample rate; for SV7 the channels and the header's frame count, which it
  gives as 1,152 samples a frame; for SV8 the stream header's sample count; and the APE tag's
  title, artist and album. Its own readings are asserted as such: no format for SV4 to SV6 (it takes
  external-mppsv4-superframes for MPEG-2.5 Layer II) or for a stream after a leading ID3v2 tag, the
  SV7 encoder version formatted from byte 24, no channels or encoder for SV8, and the duration and
  bitrate it derives; fixtures on which it reads otherwise name their evidence.

Fixtures that are not "valid" say why the others do not apply; every skipped check prints its
reason. Disagreements fail the run.
"""

import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile

import downloads
import mediainfo_tool as mi
import musepack_model as m
import musepack_tool
import tool_versions

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "musepack"
SKIPS = []

# Hand-built fixtures that the tools read as they would a valid file, and what each damaged one
# should make mpcdec do.
READABLE = {"sv8-tags", "sv7-tags", "sv8-unknown-key", "sv8-no-replaygain", "sv8-data-after-end", "sv8-end-size", "sv7-trailing-data",
            "sv8-seek-mismatch", "sv8-many-unknown-first", "sv7-encoder-claim",
            "sv8-replaygain-version", "sv6-last-frame-samples-invalid"}
# Hand-built fixtures that mpcdec's decoded samples and libmpcdec's frames confirm, and why the other checks do not apply.
DECODE_ONLY = {"sv7-gapless-frame-missing-ape": "mpcdec -i gives the header's length, which counts the missing frame, and ffmpeg shows the APE tag's cover as a second stream",
               "sv7-last-frame-samples-differ": "mpcdec -i gives the header's length, while decoding takes the 11 bits after the last frame"}
# Hand-built fixtures whose damage the tools meet in their own ways, and why they are not compared.
DAMAGED = {
    "sv7-last-frame-samples-invalid": "libmpcdec refuses a last frame of more than 1,152 samples",
    "sv7-no-frames": "the header counts no frames, so there is nothing to decode",
    "sv7-version-unknown": "mpcdec and ffmpeg refuse a version other than 7",
    "sv7-resolution-invalid": "the first frame is damaged on purpose",
    "sv7-data-after-tag": "a tag inside the stream breaks it, and the tools do not follow the SEEK frame",
    "sv5-seek-tag": "the SV4 to SV6 tools take no ID3v2 tag before the stream",
    "sv6-seek-tag": "the SV4 to SV6 tools take no ID3v2 tag before the stream",
    "sv8-channels-unsupported": "libmpcdec refuses more than 2 channels",
    "sv8-max-band-32": "libmpcdec refuses 32 bands",
    "sv8-sample-count-unknown": "libmpcdec decodes no samples where the count is 0",
    "sv8-header-crc-zero": "ffmpeg refuses a CRC of 0",
    "sv8-header-missing": "decoders cannot decode without a stream header",
    "sv8-audio-before-header": "an audio packet before the stream header",
    "sv8-audio-packets-extra": "decoders stop at the sample count, so they read fewer packets than the file holds",
    "sv8-after-audio": "packets after the audio that the tools skip or reread",
    "sv8-replaygain-truncated": "a ReplayGain packet cut short, whose values the tools read differently",
    "sv8-packet-size-invalid": "a packet size the tools cannot follow",
    "sv8-seek-table-truncated": "a seek table that counts more entries than it holds",
    "sv8-seek-table-short": "a seek table with fewer entries than the packets need",
    "sv8-invalid-key-in-run": "libmpcdec stops decoding at a key outside A to Z",
}
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
        # mpcdec prints libmpcdec r495's own spelling of the name for 0.
        expected["encoder"] = "Buschmann 1.7.0...9, Klemm 0.90...1.05" if h["encoder"] == 0 else m.libmpcdec_name(h["encoder"]).replace("Alpha", "--Alpha--")
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


def decoder_lines(tool, path):
    return [line.split() for line in run([str(tool), str(path)]).stdout.decode().split("\n") if line]


def check_old(name, manifest, path, frames126, portable, problems):
    """libmpcdec 1.2.6's reading of an SV4 to SV6 stream, the same in the Portable Musepack decoder it
    was ported from, and mppdec's frame count."""
    lines = decoder_lines(frames126, path)
    portable_frames, portable_decoder = portable or (None, None)
    if portable is None:
        skip(name, "the Portable Musepack decoder", f"{downloads.TOKEN} is not set, and its archive is only in the private repository")
    elif (other := decoder_lines(portable_frames, path)) != lines:
        first = next((i for i, (a, b) in enumerate(zip(lines, other)) if a != b), min(len(lines), len(other)))
        problems.append(f"the Portable Musepack decoder reads {other[first:first + 1]} at line {first}, libmpcdec 1.2.6 {lines[first:first + 1]}")
    header = manifest["header"]
    values = dict(zip(lines[0][2::2], lines[0][3::2]))
    version = int(header["Stream Version"])
    bitrate, intensity, block = int(header["Bitrate"].split()[0]), header["Intensity Stereo"] == "true", int(header["Block Size"].split()[0])
    expected_status = 2 if bitrate else 3 if intensity else 4 if block != 1 else 0
    frames = int(header["Frame Count"].split()[0])
    # "Bugfix: last frame was invalid for up to SV5": versions 4 and 5 drop the last counted frame.
    decoded_frames = frames if version == 6 else frames - 1
    expected = {"version": header["Stream Version"], "frames": str(decoded_frames if expected_status == 0 else frames), "bitrate": str(bitrate),
                "is": str(int(intensity)), "ms": str(int(header["Mid/Side Stereo"] == "true")), "max_band": header["Max Band"], "block": str(block)}
    if int(lines[0][1]) != expected_status:
        problems.append(f"libmpcdec 1.2.6 header status {lines[0][1]}, expected {expected_status}")
    for key, value in expected.items():
        if values.get(key) != value:
            problems.append(f"libmpcdec 1.2.6 {key}: {values.get(key)}, manifest {value}")
    if expected_status == 0:
        decoded = [line for line in lines[1:] if line[0] != "samples"]
        if len(decoded) != len(manifest["frames"]):
            problems.append(f"libmpcdec 1.2.6 read {len(decoded)} frames, manifest {len(manifest['frames'])}")
        for i, (want, got) in enumerate(zip(manifest["frames"], decoded)):
            # The decoders count the 20-bit length and, after the last frame of version 6, the 11 bits that follow it.
            bits = want[0] + 20 + (11 if version == 6 and i == frames - 1 else 0)
            if [str(bits), str(want[1]), want[2], str(want[3])] != got:
                problems.append(f"frame {i}: libmpcdec 1.2.6 {got}, manifest {want}")
                break
        samples = manifest["stream_infos"][0]["samples"]
        if lines[-1] != ["samples", str(samples)]:
            problems.append(f"libmpcdec 1.2.6 {' '.join(lines[-1])}, manifest {samples} samples")
        output = portable and run([str(portable_decoder), str(path)]).stdout.decode("latin-1")
        if portable and f"Total samples decoded: {samples}." not in output:
            problems.append(f"the Portable Musepack decoder's own driver: {' '.join(output.split())[-80:]!r}, manifest {samples} samples")
    else:
        why = {2: "a constant bitrate (ERROR_CODE_CBR)", 3: "intensity stereo (ERROR_CODE_IS)", 4: "a block size other than 1 (ERROR_CODE_BLOCKSIZE)"}
        skip(name, "the decoders' frames", f"libmpcdec 1.2.6 and its C++ ancestor refuse the stream for {why[expected_status]}")
    # SV4 and SV5 manifests hold the frames mppdec decoded when the encoder wrote the fixture, the
    # derived SV6 ones the line it printed for the relabeled file.
    line = (manifest.get("derived") or {}).get("mppdec")
    recorded = (manifest.get("encoder") or {}).get("mppdec_frames")
    if line is not None:
        recorded = int(re.search(r"after (\d+) Frames", line).group(1))
    if recorded is not None and recorded != decoded_frames:
        problems.append(f"mppdec decoded {recorded} frames when the fixture was written, expected {decoded_frames} of the {frames} the header counts")
    if shutil.which("wine") is None:
        skip(name, "mppdec", "Wine is not installed" + ("; recorded, not run: the manifest holds what mppdec printed when the fixture was written" if recorded is not None else ""))
    elif line is not None:
        if (now := musepack_tool.mppdec_line(path)) != line:
            problems.append(f"mppdec prints {now!r}, {line!r} when the fixture was written")
    elif (decoded_now := musepack_tool.mppdec_frames(path)) != decoded_frames:
        problems.append(f"mppdec decodes {decoded_now} frames, expected {decoded_frames} of the {frames} the header counts")


def check_damaged_old(name, manifest, path, frames126, portable, problems):
    """A frame damaged on purpose: libmpcdec 1.2.6 and the Portable Musepack decoder read the frames
    before it as the manifest has them, then report it invalid."""
    lines = decoder_lines(frames126, path)
    if portable is not None and decoder_lines(portable[0], path) != lines:
        problems.append("the Portable Musepack decoder and libmpcdec 1.2.6 read the damaged stream differently")
    overrun = next((i for i, f in enumerate(m.old(path.read_bytes())["frames"]) if f["overrun"]), None)
    if overrun is None:
        problems.append("no frame's subband resolutions run past its length")
        return
    decoded = [line for line in lines[1:] if line[0] != "samples"]
    if len(decoded) != overrun + 1 or decoded[-1] != ["invalid"]:
        problems.append(f"libmpcdec 1.2.6 read {len(decoded)} lines ending {decoded[-1:]}, expected frame {overrun} to be invalid")
    for i, (want, got) in enumerate(zip(manifest["frames"][:overrun], decoded)):
        if [str(want[0] + 20), str(want[1]), want[2], str(want[3])] != got:
            problems.append(f"frame {i}: libmpcdec 1.2.6 {got}, manifest {want}")
            break


# What MediaInfo (mediainfo_tool.py) does not read in Musepack files, with the evidence.
MEDIAINFO_OLD = "MediaInfo reads SV7 (\"MP+\") and SV8 (\"MPCK\") only (Audio/File_Mpc.cpp, Audio/File_MpcSv8.cpp); SV4 to SV6 streams have no signature"
MEDIAINFO_LEADING_TAG = "MediaInfo looks for \"MP+\" and \"MPCK\" at the very start of the file (File_Mpc.cpp and File_MpcSv8.cpp, FileHeader_Begin), so it finds no stream after a leading ID3v2 tag"
MEDIAINFO_SV7_ENCODER = "MediaInfo reads the SV7 encoder version from byte 24 (File_Mpc.cpp, FileHeader_Parse), where the header keeps it in byte 27, the high byte of its seventh little-endian word (libmpcdec, streaminfo.c)"
MEDIAINFO_SIGNATURE_TAG = ("MediaInfo takes \"TAG\" 128 bytes before the end for an ID3v1 tag on the signature alone (Tag/File__Tags.cpp, "
                           "DetectBeginOfEndTags_Test), here inside the Musepack data that by its own size runs to the end of the file, "
                           "which the analyzer reports as a near miss")
# An SV4 stream has no signature, and in this one MediaInfo finds a chain of MPEG audio frame headers
# from 0x1B2F (its trace: "frame - Frame 0" there), which it reports as MPEG audio.
MEDIAINFO_MISREAD_AS_MPEG = {
    "external-mppsv4-superframes": {"Format": "MPEG Audio", "Format_Version": "2.5", "Format_Profile": "Layer 2", "SamplingRate": "12000", "FrameCount": "7"},
}
MEDIAINFO_READS_OTHERWISE = {
    "sv7-tag-inside": dict.fromkeys(["tag title", "tag track", "tag performer", "tag album"], MEDIAINFO_SIGNATURE_TAG),
    "sv8-tag-inside": dict.fromkeys(["tag title", "tag track", "tag performer", "tag album"], MEDIAINFO_SIGNATURE_TAG),
    "sv7-version-unknown": {"format": "MediaInfo takes \"MP+\" only with stream version 7 (File_Mpc.cpp, FileHeader_Begin); the analyzer reads the SV7 header and reports the version"},
    "sv8-audio-before-header": dict.fromkeys(["sample rate", "samples", "duration and bitrate"], "MediaInfo stops at the first audio packet (File_MpcSv8.cpp, AP: Finish), so it does not read the stream header after it"),
    "sv8-rate-reserved": {"format": "MediaInfo 24.01 indexes its four-entry Mpc_SampleFreq table with the 3-bit sample rate code (File_MpcSv8.cpp, SH) and divides by what it finds past the end, so it exits with SIGFPE on a reserved code"},
}


def sv7_encoder_text(version):
    """MediaInfo's name for an SV7 encoder version byte (File_Mpc.cpp, FileHeader_Parse)."""
    suffix = "" if version % 10 == 0 else " Beta" if version % 2 == 0 else " Alpha"
    return f"{version / 100:.2f}{suffix}"


def check_mediainfo(name, path, tally):
    """MediaInfo must report the format, sample rate and, for SV7, the channels and the header's frame
    count (as its samples, 1,152 per frame), for SV8 the stream header's sample count, the duration
    and bitrate it derives from them and the file size, and the APE tag's title, artist and album.
    Where it reads otherwise by its source, the value it gives is checked."""
    problems = tally.start(name)
    report = mi.golden(path)
    info = mi.read(path)
    audio = info["Audio"][0] if info and info["Audio"] else {}
    if name in MEDIAINFO_MISREAD_AS_MPEG:
        tally.compare("misreading as MPEG audio", {k: audio.get(k) for k in MEDIAINFO_MISREAD_AS_MPEG[name]}, MEDIAINFO_MISREAD_AS_MPEG[name])
        return problems
    if not report["streams"]:
        tally.compare("format where the analyzer finds no stream", audio.get("Format"), None)
        return problems
    stream = report["streams"][0]
    if stream["codec"] not in ("Musepack SV7", "Musepack SV8"):
        tally.compare("format of SV4 to SV6 (" + MEDIAINFO_OLD + ")", audio.get("Format"), None)
        return problems
    if report["root"]["children"][0]["name"] == "ID3v2":
        tally.compare("format after a leading ID3v2 tag (" + MEDIAINFO_LEADING_TAG + ")", audio.get("Format"), None)
        return problems
    tally.compare("format", audio.get("Format"), stream["codec"])
    if not audio:
        return problems
    node = mi.stream_node(report, stream)
    data = path.read_bytes()
    start = node["offset"]
    if stream["codec"] == "Musepack SV8" and mi.child(node, "Stream Header") is None:
        # The stream header holds the sample rate and count.
        tally.compare("sample rate without a stream header", audio.get("SamplingRate"), None)
        return problems
    tally.compare("sample rate", audio.get("SamplingRate"), str(stream["sampleRate"]))
    rate = stream["sampleRate"]
    if stream["codec"] == "Musepack SV7":
        frames = mi.leading_number(mi.value(node, "Header", "Frame Count"))
        tally.compare("channels", audio.get("Channels"), str(stream["channels"]))
        tally.compare("header frames (samples / 1152)", audio.get("SamplingCount"), str(frames * 1152))
        tally.compare("encoder as MediaInfo misreads it (" + MEDIAINFO_SV7_ENCODER + ")", audio.get("Encoded_Library"), sv7_encoder_text(data[start + 24]))
        # File_Mpc.cpp, FileHeader_Parse: frames x 1152 over the rate, and the file less 25 bytes over them.
        tally.compare("duration and bitrate", (audio.get("Duration"), audio.get("BitRate")),
                      (f"{frames * 1152 * 1000 // rate / 1000:.3f}", str((len(data) - 25) * 8 * rate // frames // 1152) if frames else None))
    else:
        # File_MpcSv8.cpp, SH: the channel count and the encoder info packet are read but not filled in.
        tally.compare("SV8 channels and encoder", (audio.get("Channels"), audio.get("Encoded_Library")), (None, None))
        count = mi.leading_number(mi.value(node, "Stream Header", "Sample Count"))
        if count == 0:
            # A sample count of 0 means unknown (SV8 specification, Stream Header Packet), which
            # MediaInfo leaves out with the duration and bitrate (File_MpcSv8.cpp, SH: if (SampleCount)).
            tally.compare("samples, duration and bitrate where the count is unknown", (audio.get("SamplingCount"), audio.get("Duration"), audio.get("BitRate")), (None, None, None))
        else:
            tally.compare("samples", audio.get("SamplingCount"), str(count))
            # File_MpcSv8.cpp, SH: samples over the rate, and the whole file over the samples.
            tally.compare("duration and bitrate", (audio.get("Duration"), audio.get("BitRate")),
                          (f"{count * 1000 // rate / 1000:.3f}", str(len(data) * 8 * rate // count)))
    mi.compare_tags(tally, info["General"], report)
    return problems


def main():
    mi.require()
    tool_versions.require("ffprobe", "musepack")
    tally = mi.Tally(MEDIAINFO_READS_OTHERWISE)
    tools = os.environ.get("MUSEPACK_TOOLS_DIR") and pathlib.Path(os.environ["MUSEPACK_TOOLS_DIR"])
    frames_tool = musepack_tool.build_frames(tools and tools / "frames")
    frames126 = musepack_tool.build_frames_126(tools and tools / "frames126")
    portable = musepack_tool.build_portable(tools and tools / "portable") if downloads.has_token(musepack_tool.PORTABLE[2][0]) else None
    failures = 0
    for manifest_path in sorted(DIRECTORY.glob("*.expected.json")):
        name = manifest_path.name[:-len(".expected.json")]
        path = DIRECTORY / (name + ".mpc")
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        problems = []
        valid = manifest.get("crosscheck", {}).get("valid") or name in READABLE
        if manifest["version"] in (4, 5, 6) and valid:
            check_old(name, manifest, path, frames126, portable, problems)
            skip(name, "mpcdec, ffprobe, mpcdec_frames and mpc2sv8", "they read only SV7 and SV8")
        elif manifest["version"] not in (7, 8):
            if name in DAMAGED:
                skip(name, "every tool", DAMAGED[name])
            elif manifest["header"]["Bitrate"] != "0 kbps":
                skip(name, "every tool", "hand-built SV6 with a constant bitrate, which libmpcdec 1.2.6 refuses; its frames are zero bits")
            else:
                check_damaged_old(name, manifest, path, frames126, portable, problems)
        elif name in REFUSED:
            code, info = mpcdec_info(path)
            if code == 0 and "samplerate" in info:
                problems.append(f"mpcdec read the stream, though {REFUSED[name]}")
            skip(name, "ffprobe, mpcdec_frames and mpc2sv8", REFUSED[name])
        elif name in SHORT:
            check_decode(name, manifest, path, problems, exact=False)
            skip(name, "mpcdec -i, ffprobe and mpc2sv8", "the stream is cut short or damaged, so the tools stop at different points; mpcdec must not decode more than the header announces")
        elif name in DECODE_ONLY:
            check_decode(name, manifest, path, problems)
            check_frames(name, manifest, path, frames_tool, problems)
            skip(name, "mpcdec -i, ffprobe and mpc2sv8", DECODE_ONLY[name])
        elif name in DAMAGED:
            skip(name, "every tool", DAMAGED[name])
        elif valid:
            check_info(name, manifest, path, problems)
            check_decode(name, manifest, path, problems)
            check_ffprobe(name, manifest, path, problems)
            if manifest["version"] == 7:
                check_frames(name, manifest, path, frames_tool, problems)
                check_mpc2sv8(name, manifest, path, problems)
        else:
            skip(name, "every tool", "the damage (a tag signature inside the audio, a key libmpcdec stops at, a sample count past the audio or a missing stream end) makes the tools' readings differ by design")
        problems += check_mediainfo(name, path, tally)
        if problems:
            failures += 1
            print(f"FAIL {name}: " + "; ".join(problems))
        else:
            print(f"ok   {name}")
    for line in SKIPS:
        print(line)
    for problem in tally.unused():
        print(f"FAIL {problem}")
        failures += 1
    tally.print(sys.stdout)
    if failures:
        raise SystemExit(f"{failures} fixtures disagree with the tools")


if __name__ == "__main__":
    main()
