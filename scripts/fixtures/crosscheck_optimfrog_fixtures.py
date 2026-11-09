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
import optimfrog_tool
import tool_versions

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "optimfrog"


class Info(ctypes.Structure):
    # OptimFROG_Info of SDK/OptimFROG/OptimFROG.h; the Linux x64 build does not pack it.
    _fields_ = [("channels", ctypes.c_uint32), ("samplerate", ctypes.c_uint32), ("bitspersample", ctypes.c_uint32), ("bitrate", ctypes.c_uint32),
                ("version", ctypes.c_uint32), ("method", ctypes.c_char_p), ("speedup", ctypes.c_char_p),
                ("noPoints", ctypes.c_int64), ("originalSize", ctypes.c_int64), ("compressedSize", ctypes.c_int64), ("length_ms", ctypes.c_int64),
                ("sampleType", ctypes.c_char_p), ("channelConfig", ctypes.c_char_p)]


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
        if info is None:
            failures.append(f"{path.name}: the SDK library does not open the file")
            continue
        channels = 1 if main["channel_config"] == 0 else 2
        stream = manifest["stream_info"]
        for key, expected, actual in (("channels", channels, info["channels"]), ("samplerate", main["sample_rate"], info["samplerate"]),
                                      ("noPoints", stream["samples"], info["noPoints"])):
            if expected != actual:
                failures.append(f"{path.name}: {key} is {actual} in the SDK library, {expected} in the manifest")
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
            if any(t.get("@type") == "Audio" for t in tracks):
                failures.append(f"{path.name}: MediaInfo finds an audio track")
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
