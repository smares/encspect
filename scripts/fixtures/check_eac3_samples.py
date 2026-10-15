#!/usr/bin/env python3
"""Checks tests/conformance/eac3-samples.json against real E-AC-3 files that this repository must not
redistribute: Dolby's joint object coding (Dolby Atmos) test files from the Dolby Digital Plus Online
Delivery Kit v1.4.1, and three files from the FFmpeg sample archive: two with the adaptive hybrid transform, one with junk in front. The
script downloads them into a cache directory and checks each file's SHA-256 against the manifest before
using it.

The Dolby files are the one source of object audio that no hand-built fixture can stand in for: they
show where Dolby's encoder puts the EMDF container (at the start of a skip field, here in block 2 of each
frame), that emdf_container_length counts the bytes after its own field, that the payload list ends with
a bare 5-bit ID 0 and that codecdatae is 0. The AHT files, whose audio blocks the analyzer does not read
to their end, must give no object audio and no finding about it.

Per file, ffprobe and MediaInfo must agree with the manifest, with their words mapped to the analyzer's as
crosscheck_ac3_fixtures.py does (ffprobe's profile "Dolby Digital Plus + Dolby Atmos" is the frames'
extension flag; MediaInfo's JOC feature, ComplexityIndex and NumberOfDynamicObjects). With --analyzer, the
analyzer's report (encspect --ndjson) must match instead: object audio and its object count, the EMDF
containers and the complexity index in the statistics, and exactly the warnings "findings" and the infos "info_findings" list.

Needs curl, ffprobe and mediainfo. Usage:

    python3 scripts/fixtures/check_eac3_samples.py [cache directory]
    python3 scripts/fixtures/check_eac3_samples.py --analyzer path/to/encspect [cache directory]
"""

import json
import pathlib
import subprocess
import sys

import crosscheck_ac3_fixtures as cc
import downloads
import mediainfo_tool as mi

ROOT = pathlib.Path(__file__).resolve().parents[2]
MANIFEST = ROOT / "tests" / "conformance" / "eac3-samples.json"
ATMOS = "Dolby Digital Plus + Dolby Atmos"


def addresses(sample):
    """The file's address, then the Wayback Machine's copy of it where it has one."""
    return [sample["url"], *([downloads.archived(sample["url"], 2026)] if sample.get("archived", True) else [])]


def fetch(manifest, cache):
    cache.mkdir(parents=True, exist_ok=True)
    for sample in manifest["samples"]:
        downloads.fetch(addresses(sample), sample["sha256"], cache / sample["file"])


def tool_problems(path, sample):
    problems = []
    probe = json.loads(subprocess.run(["ffprobe", "-v", "error", "-select_streams", "a:0", "-count_packets", "-show_entries",
                                       "stream=codec_name,profile,nb_read_packets", "-of", "json", str(path)],
                                      capture_output=True, text=True, check=True).stdout)["streams"][0]
    expected = ATMOS if sample["object_audio"] else None
    if probe.get("profile") not in ([expected] if expected else [None, "unknown"]):
        problems.append(f"ffprobe's profile is {probe.get('profile')!r}, expected {expected!r}")
    if probe["codec_name"] != "eac3":
        problems.append(f"ffprobe finds the codec {probe['codec_name']}")
    if sample.get("frames") and int(probe["nb_read_packets"]) != sample["frames"]:
        problems.append(f"ffprobe finds {probe['nb_read_packets']} packets, the manifest {sample['frames']}")
    info = mi.read(path, full=True)
    audio = info["Audio"][0] if info and info["Audio"] else {}
    features = (audio.get("Format_AdditionalFeatures") or "").split()
    if ("JOC" in features) != sample["object_audio"]:
        problems.append(f"MediaInfo's features are {features}")
    extra = audio.get("extra", {})
    if sample["object_audio"]:
        if extra.get("ComplexityIndex") != str(sample["complexity_index"]):
            problems.append(f"MediaInfo's ComplexityIndex is {extra.get('ComplexityIndex')}")
        if extra.get("NumberOfDynamicObjects") != str(sample["dynamic_objects"]):
            problems.append(f"MediaInfo's NumberOfDynamicObjects is {extra.get('NumberOfDynamicObjects')}")
    elif "ComplexityIndex" in extra or "NumberOfDynamicObjects" in extra:
        problems.append("MediaInfo reports object audio fields")
    return problems


def statistic(report, name):
    return next((n["value"] for n in cc.mi.walk(report["root"]) if n["name"] == name and n.get("kind") != "Frame"), None)


def analyzer_problems(analyzer, path, sample):
    report = json.loads(subprocess.run([analyzer, "--ndjson", str(path)], capture_output=True, text=True, check=True).stdout)
    problems = []
    if report.get("schemaVersion") != 1:
        problems.append(f"the analyzer writes report schema {report.get('schemaVersion')}, this script reads 1")
    stream = report["streams"][0]
    if sample.get("codec") and stream.get("codec") != sample["codec"]:
        problems.append(f"the analyzer's codec is {stream.get('codec')}, the manifest {sample['codec']}")
    if sample.get("format") and report["root"].get("value") != sample["format"]:
        problems.append(f"the analyzer's format is {report['root'].get('value')}, the manifest {sample['format']}")
    if bool(stream.get("hasObjectAudio")) != sample["object_audio"]:
        problems.append(f"the analyzer's hasObjectAudio is {stream.get('hasObjectAudio')}")
    if sample["object_audio"]:
        frames = sample["frames"]
        if stream.get("jocObjectCount") != sample["joc_object_count"]:
            problems.append(f"the analyzer's jocObjectCount is {stream.get('jocObjectCount')}")
        if statistic(report, "Complexity Index Type A") != sample["complexity_index"]:
            problems.append(f"the analyzer's complexity index is {statistic(report, 'Complexity Index Type A')}")
        # Dolby's encoder sends one container in every frame, and the flag in the same frame.
        for name in ("EMDF Containers", "Frames With Object Audio", "Frames With Extension Type A"):
            if str(statistic(report, name)) not in (str(frames), f"{frames} frames", f"{frames}"):
                problems.append(f"the analyzer's {name} is {statistic(report, name)}, expected {frames}")
        objects = cc.mediainfo_dynamic_objects(path.read_bytes(), report)
        if objects != sample["dynamic_objects"]:
            problems.append(f"the OAMD payloads give {objects} dynamic objects, the manifest {sample['dynamic_objects']}")
        if cc.mediainfo_complexity(report) != str(sample["complexity_index"]):
            problems.append(f"the frames and dec3 give the complexity index {cc.mediainfo_complexity(report)}")
    related = [f["code"] for f in report["findings"] if any(word in f["code"] for word in ("emdf", "joc", "object", "extension"))]
    if related:
        problems.append(f"the analyzer reports {related}")
    serious = sorted({f["code"] for f in report["findings"] if f["severity"] != "Info"})
    if serious != sorted(sample["findings"]):
        problems.append(f"the analyzer reports {serious}, the manifest {sample['findings']}")
    infos = sorted({f["code"] for f in report["findings"] if f["severity"] == "Info"})
    if infos != sorted(sample["info_findings"]):
        problems.append(f"the analyzer reports the infos {infos}, the manifest {sample['info_findings']}")
    return problems


def main():
    arguments = sys.argv[1:]
    analyzer = None
    if arguments[:1] == ["--analyzer"]:
        analyzer, arguments = arguments[1], arguments[2:]
    cache = pathlib.Path(arguments[0] if arguments else ROOT / ".cache" / "eac3")
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    mi.require()
    fetch(manifest, cache)
    problems = 0
    for sample in manifest["samples"]:
        path = cache / sample["file"]
        found = analyzer_problems(analyzer, path, sample) if analyzer else tool_problems(path, sample)
        for problem in found:
            problems += 1
            print(f"{sample['file']}: {problem}")
    what = "Analyzed" if analyzer else "Checked"
    print(f"{what} {len(manifest['samples'])} E-AC-3 samples, {problems} problems.", file=sys.stderr)
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
