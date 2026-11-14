#!/usr/bin/env python3
"""Compares the PCM of Encspect's MPEG audio decoder with the reference output of the ISO/IEC 13818-4 conformance streams that the decoder covers
(docs/design/lossy-decoders.md, "Conformance material per format"; docs/development.md, "MPEG audio decoding").

    conformance_mpeg_pcm.py [--pcmdump CMD] [--only NAME ...] [CACHE]

ISO publishes 35 audio conformance streams of ISO/IEC 13818-4 with the output of its reference decoder (the 16-bit samples of each channel, `testNN_l.pcm`
and `testNN_r.pcm`). They are downloaded into CACHE (default .cache/conformance, checked by SHA-256 against tests/conformance/mpeg2-audio.json, the manifest
conformance_mpeg_audio.py uses) and never committed (ISO's license lets them be used unmodified for the purpose of the standard).

The decoder outputs the channels of the configuration, left, right, center, the surround channels or the second stereo program, then the LFE, which it holds for 96 samples
(the reference's LFE file has a twelfth of the samples of a frame), so the 14 MPEG-1 Layer II streams with the multichannel extension and no extension bitstream are compared with the
reference's dematrixed channel files. The other eight (test07, 08, 10, 12, 13, 17, 20 and 21) keep part of their multichannel data in a separate extension bitstream the analysis never
sees, so the decoder outputs their base stereo and they are left out of the Encspect column, as are test22 (MPEG-1 Layer I with an extension, whose archive holds only dematrixed
channels) and test23's multichannel part (Layer III). That leaves the 12 streams without the extension (test23's base stereo, test24 to test34) and the 14 multichannel ones. mctoolamed
(derived from the ISO reference software, built from the pinned source and run as a black box, with the extension bitstream where there is one) is a second column: its channel files
against the same reference, for every multichannel stream; it says "n/a" where it cannot be built.

Each stream is dumped by tests/Encspect.PcmDump and compared by compare_pcm.py against the interleaved 16-bit reference: the analyzer's floats are rounded
to nearest (halves up) and clipped to 16 bits, then per channel the largest difference may be 2 LSB and the RMS 1 LSB (docs/development.md, "Comparing decoded
PCM"). A stream outside the bound fails the run and is reported to the lead, not widened for.

Exit status 0 where every stream is within the bound, 1 where one is not.
"""

import argparse
import json
import pathlib
import shlex
import subprocess
import sys
import tarfile
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import downloads  # noqa: E402
import mctoolamed_tool  # noqa: E402

ROOT = HERE.parents[1]
MANIFEST = ROOT / "tests" / "conformance" / "mpeg2-audio.json"
DEFAULT_PCMDUMP = "dotnet " + str(ROOT / "tests" / "Encspect.PcmDump" / "bin" / "Release" / "net10.0" / "Encspect.PcmDump.dll")


SPEAKERS = {"front left": "l", "front right": "r", "front center": "c", "back left": "ls", "back right": "rs", "back center": "s", "LFE": "lfe"}


def has_extension(stream):
    return "multichannel" in stream and stream["multichannel"]["extension_bitstream"]


def channel_files(directory, stream):
    """The reference's file of each speaker of the stream's layout, in the layout's order; the two channels of a second program are testNN_l2.pcm and testNN_r2.pcm."""
    discrete = iter(["l2", "r2"])
    files = []
    for speaker in stream["channel_layout"]:
        suffix = next(discrete) if speaker == "discrete" else SPEAKERS[speaker]
        files.append((speaker, find(directory, f"{stream['name']}_{suffix}.pcm")))
    return files


def hold(samples, width, times):
    """Each sample of a file repeated times: the reference's LFE at the output's rate."""
    out = bytearray()
    for i in range(0, len(samples), width):
        out += samples[i:i + width] * times
    return bytes(out)


def multichannel_reference(directory, stream, samples):
    """The reference of a multichannel stream as one raw file of interleaved little-endian samples in the layout's order, and the sample width."""
    files = channel_files(directory, stream)
    planes = []
    width = None
    for speaker, path in files:
        if path is None:
            return None, None
        data = path.read_bytes()
        if speaker == "LFE":
            frames = len(data) // 2
            data = hold(data, 2, samples // frames)
        planes.append(data)
        width = len(data) // samples
    planes = [swap(p, width) for p in planes]
    out = bytearray(len(planes[0]) * len(planes))
    for c, plane in enumerate(planes):
        for k in range(width):
            out[c * width + k::width * len(planes)] = plane[k::width]
    return bytes(out), width


def mctoolamed_column(binary, directory, stream):
    """Largest difference in LSB between mctoolamed's channels and the reference's, over the samples both have; None where it cannot be run."""
    if binary is None or stream["name"] == "test35":
        # test35's reference has 24 bits ("represented with 24 bit accuracy") and mctoolamed writes 16.
        return None
    path = directory / stream["file"]
    extension = directory / stream["extension_file"] if stream.get("extension_file") else None
    channels, lfe = mctoolamed_tool.decode_pcm(binary, path, extension)
    files = channel_files(directory, stream)
    worst = 0
    wide = [(s, f) for s, f in files if s != "LFE"]
    if len(channels) < len(wide):
        return float("inf")
    pairs = [(c, f) for c, (s, f) in zip(channels, wide)]
    if lfe is not None:
        pairs.extend((lfe, f) for s, f in files if s == "LFE")
    for samples, reference in pairs:
        if reference is None:
            continue
        ref = swap(reference.read_bytes(), 2)
        count = min(len(samples), len(ref)) // 2
        a = memoryview(samples[:2 * count]).cast("h")
        b = memoryview(ref[:2 * count]).cast("h")
        worst = max(worst, max((abs(x - y) for x, y in zip(a, b)), default=0))
    return worst


def fetch(manifest, stream, cache):
    cache.mkdir(parents=True, exist_ok=True)
    archive = cache / f"{stream['name']}.tar.gz"
    url = manifest["source"] + archive.name
    downloads.fetch([url, downloads.archived(url, manifest["archive_year"]), downloads.private(f"mirrors/conformance/mpeg2-audio/{archive.name}")], stream["archive_sha256"], archive)
    target = cache / stream["name"]
    if not (target / "done").exists():
        with tarfile.open(archive) as tar:
            tar.extractall(target, filter="data")
        (target / "done").write_text("")
    return target


def find(directory, name):
    matches = [p for p in directory.rglob(name) if p.is_file()]
    return matches[0] if matches else None


def swap(samples, width):
    """The reference's samples are big-endian (the first samples of test24_l.pcm read -6306, -7145 as 0xE75E, 0xE417 in file order); returns little-endian ones."""
    out = bytearray(samples)
    for k in range(width):
        out[k::width] = samples[width - 1 - k::width]
    return bytes(out)


def interleave(left, right, width):
    """Two files of mono samples as one of interleaved stereo samples."""
    out = bytearray(len(left) * 2)
    for i in range(0, len(left), width):
        out[2 * i:2 * i + width] = left[i:i + width]
        out[2 * i + width:2 * i + 2 * width] = right[i:i + width]
    return bytes(out)


def to_float(samples24):
    """Little-endian 24-bit samples as float32 at full scale 1.0 (the reference of test34 "represented with 24 bit accuracy")."""
    import array
    values = array.array("f", (int.from_bytes(samples24[i:i + 3], "little", signed=True) / 8388608.0 for i in range(0, len(samples24), 3)))
    return values.tobytes()


def compare_multichannel(args, stream, directory, path, temporary, mctoolamed):
    """One multichannel stream: Encspect's channels against the reference's dematrixed files, and mctoolamed's against the same; returns 1 where Encspect is outside the bound."""
    column = mctoolamed_column(mctoolamed, directory, stream)
    second = "n/a" if column is None else f"largest {column} LSB"
    label = f"{stream['name']} ({stream['version']} Layer {stream['layer']}, {stream['multichannel']['configuration']}, {stream['sample_rate']} Hz)"
    if has_extension(stream):
        print(f"skip {label}: an extension bitstream holds part of its channels, which the analysis never sees (the decoder outputs the base stereo); mctoolamed: {second}")
        return 0
    dump = temporary / stream["name"]
    result = subprocess.run(shlex.split(args.pcmdump) + [str(dump), str(path)], capture_output=True)
    manifest_file = dump / f"0000-{path.name}.pcm.json"
    if result.returncode != 0 or not manifest_file.exists():
        print(f"FAIL {label}: PcmDump exits {result.returncode}")
        return 1
    info = json.loads(manifest_file.read_text())["streams"][0]
    reference, width = multichannel_reference(directory, stream, info["samples"])
    if reference is None or info["channels"] != len(stream["channel_layout"]):
        print(f"FAIL {label}: the decoder outputs {info['channels']} channels of the stream's {len(stream['channel_layout'])}, or a reference file is missing")
        return 1
    fmt = "int16"
    if width == 3:
        reference, fmt = to_float(reference), "float32"
    raw = dump / "reference.raw"
    raw.write_bytes(reference)
    outcome = subprocess.run([sys.executable, str(HERE / "compare_pcm.py"), str(manifest_file), "--reference", str(raw), "--reference-format", fmt,
                              "--reference-channels", str(info["channels"]), "--json"], capture_output=True, text=True)
    if outcome.returncode == 2:
        print(f"FAIL {label}: {outcome.stderr.strip()}")
        return 1
    data = json.loads(outcome.stdout)
    worst = max(data["channels"], key=lambda c: c["max"])
    state = "FAIL" if outcome.returncode != 0 else "ok  "
    print(f"{state} {label}: largest {worst['max']:.3g} {data['unit']}, RMS {worst['rms']:.3g} {data['unit']}; mctoolamed: {second}")
    return 1 if outcome.returncode != 0 else 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("cache", nargs="?", type=pathlib.Path, default=ROOT / ".cache" / "conformance")
    parser.add_argument("--pcmdump", default=DEFAULT_PCMDUMP)
    parser.add_argument("--only", nargs="*", default=[])
    args = parser.parse_args(argv)
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    streams = [s for s in manifest["streams"] if not args.only or s["name"] in args.only]
    failed = 0
    try:
        mctoolamed = mctoolamed_tool.build() if any("multichannel" in s for s in streams) else None
    except (SystemExit, OSError, Exception) as error:  # the source is fetched and built: a failure leaves the second column out
        print(f"note: mctoolamed is not available: {error}")
        mctoolamed = None
    with tempfile.TemporaryDirectory() as temporary:
        for stream in streams:
            directory = fetch(manifest, stream, args.cache)
            path = directory / stream["file"]
            if "multichannel" in stream:
                failed += compare_multichannel(args, stream, directory, path, pathlib.Path(temporary), mctoolamed)
                continue
            # Mono streams state their one file as testNN.pcm, dual channel streams name their programs testNN_I.pcm and testNN_II.pcm.
            left = next((f for n in (f"{stream['name']}_l.pcm", f"{stream['name']}.pcm", f"{stream['name']}_I.pcm") if (f := find(directory, n))), None)
            right = next((f for n in (f"{stream['name']}_r.pcm", f"{stream['name']}_II.pcm") if (f := find(directory, n))), None)
            if left is None or not path.exists():
                print(f"FAIL {stream['name']}: no stream or no left reference in the archive")
                failed += 1
                continue
            channels = 1 if stream["channels"] == 1 else 2
            both = find(directory, f"{stream['name']}.pcm")
            dump = pathlib.Path(temporary) / stream["name"]
            result = subprocess.run(shlex.split(args.pcmdump) + [str(dump), str(path)], capture_output=True)
            manifest_file = dump / f"0000-{path.name}.pcm.json"
            if result.returncode != 0 or not manifest_file.exists():
                print(f"FAIL {stream['name']}: PcmDump exits {result.returncode}")
                failed += 1
                continue
            # A stream with a multichannel extension (test22, test23) states the reference of its base stereo as testNN.pcm, interleaved; test22 has none, and its
            # left and right files are dematrixed channels the base does not carry.
            samples = json.loads(manifest_file.read_text())["streams"][0]["samples"]
            if both is not None and channels == 2 and both.stat().st_size == 2 * left.stat().st_size:
                reference, width = both.read_bytes(), both.stat().st_size // (2 * samples)
            elif find(directory, f"{stream['name']}.ext") is not None and both is None:
                print(f"skip {stream['name']}: its extension makes its left and right files dematrixed channels, and no file holds the base stereo")
                continue
            else:
                width = left.stat().st_size // samples
                reference = left.read_bytes() if channels == 1 else interleave(left.read_bytes(), right.read_bytes(), width)
            reference = swap(reference, width)
            fmt = "int16"
            bounds = []
            if width == 3:
                reference, fmt, bounds = to_float(reference), "float32", []
            raw = dump / "reference.raw"
            raw.write_bytes(reference)
            outcome = subprocess.run([sys.executable, str(HERE / "compare_pcm.py"), str(manifest_file), "--reference", str(raw), "--reference-format", fmt,
                                      "--reference-channels", str(channels), "--json"], capture_output=True, text=True)
            if outcome.returncode == 2:
                print(f"FAIL {stream['name']}: {outcome.stderr.strip()}")
                failed += 1
                continue
            data = json.loads(outcome.stdout)
            worst = max(data["channels"], key=lambda c: c["max"])
            only = data["notComparedOnlyInAnalyzer"] + data["notComparedOnlyInReference"]
            note = f"; positions only one side has: analyzer {data['notComparedOnlyInAnalyzer']}, reference {data['notComparedOnlyInReference']}" if only else ""
            if outcome.returncode != 0:
                failed += 1
                print(f"FAIL {stream['name']} (MPEG layer {stream['layer']}): largest {worst['max']:.3g} {data['unit']}, RMS {worst['rms']:.3g} {data['unit']}{note}")
            else:
                print(f"ok   {stream['name']} ({stream['version']} Layer {stream['layer']}, {stream['sample_rate']} Hz): largest {worst['max']:.3g} {data['unit']}, RMS {worst['rms']:.3g} {data['unit']}{note}")
    print(f"{len(streams)} conformance streams compared, {failed} outside the bound")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
