#!/usr/bin/env python3
"""Checks the OptimFROG fixtures' manifests against OptimFROG's own SDK library and command-line tools, run as black boxes (CI only).

For every manifest in tests/fixtures/optimfrog (make_external_optimfrog_fixtures.py, import_optimfrog_fixtures.py), with the pinned OptimFROG 5.100 package
(optimfrog_tool.py):

- The SDK library `libOptimFROG.so` is called through ctypes (`OptimFROG_infoFile`): the channels, the sample rate and the number of sample points must
  equal the manifest's, and the file must open. The bits per sample, the version, the method and speedup strings, the sample type and the channel
  configuration strings are printed beside the manifest's stored values, so a change in how the library names them shows in the log; the stored speedup and
  mode are held to the strings the library gives for the values of the first files seen (no mapping is assumed in advance: the script prints the table of stored
  mode to method string that the fixtures show, and fails when one stored value gives two strings).
- `ofr --info` (`ofs --info` for DualStream files) is run on each file and its output kept in the log; it must exit with status 0.
- ffprobe and MediaInfo do not read OptimFROG (FFmpeg has no demuxer; MediaInfoLib 26.01 has no parser): both must find no audio stream.

Every skipped check prints its reason. Disagreements fail the run (exit status 1).

    python3 scripts/fixtures/crosscheck_optimfrog_fixtures.py [name ...]
"""

import ctypes
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile

import mediainfo_tool as mi
import optimfrog_model as model
import optimfrog_tool
import tool_versions

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "optimfrog"


class Info(ctypes.Structure):
    # OptimFROG_Info of SDK/OptimFROG/OptimFROG.h, packed: SystemID.h defines CFG_ATTRIBUTE_PACKED for GNUC_LINUX_X8664, and the header then declares the
    # struct __attribute__((packed)). Offsets: channels 0, samplerate 4, bitspersample 8, bitrate 12, version 16, method 20, speedup 28, noPoints 36,
    # originalSize 44, compressedSize 52, length_ms 60, sampleType 68, channelConfig 76; 84 bytes.
    _pack_ = 1
    _layout_ = "ms"  # Python 3.13 and later warn about _pack_ without a layout; 1-byte packing is the same on every platform
    _fields_ = [("channels", ctypes.c_uint32), ("samplerate", ctypes.c_uint32), ("bitspersample", ctypes.c_uint32), ("bitrate", ctypes.c_uint32),
                ("version", ctypes.c_uint32), ("method", ctypes.c_char_p), ("speedup", ctypes.c_char_p),
                ("noPoints", ctypes.c_int64), ("originalSize", ctypes.c_int64), ("compressedSize", ctypes.c_int64), ("length_ms", ctypes.c_int64),
                ("sampleType", ctypes.c_char_p), ("channelConfig", ctypes.c_char_p)]


assert ctypes.sizeof(Info) == 84 and Info.method.offset == 20 and Info.noPoints.offset == 36 and Info.sampleType.offset == 68, "OptimFROG_Info does not have the packed layout of OptimFROG.h"


# Files the 5.100 library does not open in CI (job 129444958413 of PR 249: "the SDK library does not open the file"): the float files `off` writes, whose main block
# is "OFRX". The reason is an observation, not a cause found in the SDK (its header and documentation say nothing of float files). They are checked with `off --info`
# instead, and the run fails if the library starts to open one. Their fields are compared with the recorded input that `off` decoded each file back to.
LIBRARY_REFUSES = {
    "external-float-32-md5.ofr": "OFRX main block of `off`",
    "external-float-32.ofr": "OFRX main block of `off`",
    "external-float-mantissa-8.ofr": "OFRX main block of `off`",
}

# Files MediaInfo reports an audio track for, with the reason; the run fails if it stops reporting one.
MEDIAINFO_READS = {
    "built-tag-id3v1-short-title.ofr": "MediaInfo 24.01 and 26.01 read the file as MPEG Audio (Layer 1, 160 kb/s); the file is the encoder's blocks plus a 128-byte ID3v1 tag with a one-character title, "
                                      "which no other built file shows, and MediaInfo's reason is not known",
}


def find(manifest):
    stem = manifest.name[:-len(".expected.json")]
    return next(p for p in DIRECTORY.glob(stem + ".*") if p.suffix in (".ofr", ".ofs", ".ofc"))


def library_info(library, path):
    info = Info()
    result = library.OptimFROG_infoFile(str(path).encode(), ctypes.byref(info), None)
    if result != 0:
        return None
    return {"channels": info.channels, "samplerate": info.samplerate, "bitspersample": info.bitspersample, "version": info.version,
            "method": (info.method or b"").decode("latin-1"), "speedup": (info.speedup or b"").decode("latin-1"), "noPoints": info.noPoints,
            "sampleType": (info.sampleType or b"").decode("latin-1"), "channelConfig": (info.channelConfig or b"").decode("latin-1")}


def audio_streams(output):
    try:
        return [s for s in json.loads(output or b"{}").get("streams", []) if s.get("codec_type") == "audio"]
    except json.JSONDecodeError:
        return []


def main():
    tool_versions.require("ffprobe")
    mi.require()
    chosen = [a for a in sys.argv[1:] if not a.startswith("--")]
    package = optimfrog_tool.extract(tempfile.mkdtemp(prefix="optimfrog-"))
    library = ctypes.CDLL(str(optimfrog_tool.library(package)))
    library.OptimFROG_infoFile.restype = ctypes.c_int32
    manifests = sorted(DIRECTORY.glob("*.expected.json"))
    failures = []
    skipped = {}
    methods = {}
    checked = 0
    for manifest_path in manifests:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        path = find(manifest_path)
        if chosen and path.name not in chosen:
            continue
        main = manifest.get("main")
        info = library_info(library, path)
        if main is None:
            skipped.setdefault("no main block in the manifest", []).append(path.name)
            continue
        if path.name in LIBRARY_REFUSES:
            if info is not None:
                failures.append(f"{path.name}: the SDK library opens the file now; remove it from LIBRARY_REFUSES and compare its fields")
            result = subprocess.run([str(package / "off"), "--info", str(path)], capture_output=True, text=True)
            print(f"{path.name}: library refuses ({LIBRARY_REFUSES[path.name]}); off --info exits with {result.returncode}")
            if result.returncode != 0:
                failures.append(f"{path.name}: off --info exits with {result.returncode}")
            # The black box for OFRX's fields: `off` decoded the file to the input it was given (provenance "samples_equal_input"; with --mantissabits 8 only the shape is the same), whose rate, channels, sample count and
            # bits are recorded, and the manifest's fields must be those.
            provenance = json.loads(path.with_name(path.name + ".provenance.json").read_text(encoding="utf-8"))
            given = provenance["input"]
            stream = manifest["stream_info"]
            if not provenance["decoded"]["decode"]["samples_equal_input"] and "mantissa" not in path.name:
                failures.append(f"{path.name}: off did not decode the file to its input")
            for key, expected, actual in (("channels", given["channels"], 1 if main["channel_config"] == 0 else 2), ("sample rate", given["rate"], main["sample_rate"]),
                                          ("samples", given["samples"], stream["samples"]), ("bits per sample", given["bits"], stream["bits_per_sample"])):
                if expected != actual:
                    failures.append(f"{path.name}: {key} is {actual} in the manifest, {expected} in the input `off` decoded the file to")
            checked += 1
            continue
        if info is None:
            failures.append(f"{path.name}: the SDK library does not open the file")
            continue
        channels = 1 if main["channel_config"] == 0 else 2
        stream = manifest["stream_info"]
        for key, expected, actual in (("channels", channels, info["channels"]), ("samplerate", main["sample_rate"], info["samplerate"]),
                                      ("noPoints", stream["samples"], info["noPoints"])):
            if expected != actual:
                failures.append(f"{path.name}: {key} is {actual} in the SDK library, {expected} in the manifest")
        # The documented tables of format.txt (appendices A, B, D) against the library's own names, and the bits per sample.
        for key, expected, actual in (("speedup", model.SPEEDUPS[main["speedup"]], info["speedup"]), ("sampleType", model.SAMPLE_TYPES[main["sample_type"]], info["sampleType"]),
                                      ("channelConfig", model.CHANNEL_CONFIGS[main["channel_config"]], info["channelConfig"]), ("bitspersample", stream["bits_per_sample"], info["bitspersample"])):
            if str(expected).casefold() != str(actual).casefold():
                failures.append(f"{path.name}: {key} is {actual!r} in the SDK library, {expected!r} by the table of format.txt")
        methods.setdefault(main["mode"], set()).add(info["method"])
        print(f"{path.name}: library bits={info['bitspersample']} version={info['version']} method={info['method']} speedup={info['speedup']} "
              f"type={info['sampleType']} config={info['channelConfig']}; manifest bits={stream['bits_per_sample']} mode={main['mode']} speedup={main['speedup']} "
              f"type={main['sample_type']} config={main['channel_config']}")
        tool = package / ("ofs" if path.suffix == ".ofs" else "ofr")
        result = subprocess.run([str(tool), "--info", str(path)], capture_output=True, text=True)
        if result.returncode != 0:
            failures.append(f"{path.name}: {tool.name} --info exits with {result.returncode}")
        if shutil.which("ffprobe"):
            probe = subprocess.run(["ffprobe", "-v", "quiet", "-print_format", "json", "-show_streams", str(path)], capture_output=True)
            if audio_streams(probe.stdout):
                failures.append(f"{path.name}: ffprobe finds an audio stream")
        else:
            skipped.setdefault("ffprobe is not installed", []).append(path.name)
        if shutil.which("mediainfo"):
            probe = subprocess.run(["mediainfo", "--Output=JSON", str(path)], capture_output=True)
            try:
                tracks = json.loads(probe.stdout or b"{}").get("media", {}).get("track", [])
            except json.JSONDecodeError:
                tracks = []
            reads = any(t.get("@type") == "Audio" for t in tracks)
            if reads and path.name not in MEDIAINFO_READS:
                failures.append(f"{path.name}: MediaInfo finds an audio track")
            elif not reads and path.name in MEDIAINFO_READS:
                failures.append(f"{path.name}: MediaInfo no longer finds the audio track it found ({MEDIAINFO_READS[path.name]}); remove it from MEDIAINFO_READS")
        else:
            skipped.setdefault("mediainfo is not installed", []).append(path.name)
        checked += 1
    for mode, strings in sorted(methods.items()):
        print(f"stored mode {mode} -> library method {sorted(strings)}")
        if len(strings) > 1:
            failures.append(f"stored mode {mode} gives several method strings: {sorted(strings)}")
    for reason, names in skipped.items():
        print(f"skipped {len(names)} ({reason}): {', '.join(names[:5])}")
    print(f"Checked {checked} fixtures.")
    if failures:
        print("\n".join(failures))
        raise SystemExit(1)


if __name__ == "__main__":
    main()
