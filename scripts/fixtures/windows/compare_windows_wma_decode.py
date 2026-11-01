#!/usr/bin/env python3
"""Compares what Microsoft's decoders made of the fixtures (the artifact of
.github/workflows/windows-wma-decode.yml) with the analyzer's reports and ffmpeg's sample counts.

    python3 compare_windows_wma_decode.py <artifact folder> [--fixtures tests/fixtures/asf]
        [--encspect path/to/encspect] [--no-ffmpeg] [--only text] [--objects N] [--json out.json]

The artifact folder holds records/*.json (one per file: the Source Reader's reads and the decoder
DMO's output per media object) and environment.json. For each file the script prints the total
samples (per channel) of the analyzer, of ffmpeg, of the Source Reader and of the decoder fed
directly, then, where the analyzer's report has the samples of each frame, the media objects whose
decoded samples differ from the analyzer's. It runs on Linux and needs neither Windows nor Windows
tools. The analyzer's figures come from the committed `<name>.report.json` next to the fixtures, or
from the encspect CLI when --encspect is given; ffmpeg is run on the fixtures if it is on the path.

What it can see: counts, timestamps and results. It cannot see the decoders' internal state; frame
boundaries show only through the Source Reader's timestamps and counts and the DMO's output per input.
"""

import argparse
import bisect
import json
import pathlib
import re
import shutil
import subprocess
import sys

TAGS = {0x160: "WMA 1", 0x161: "WMA 2", 0x162: "WMA Pro", 0x163: "WMA Lossless", 0x0A: "WMA Voice"}
# Frames of these formats list their samples as "N samples" blocks in the report.
PER_OBJECT_TAGS = {0x160, 0x161}


def load_report(path, fixtures, encspect):
    if encspect:
        source = fixtures / path
        if not source.exists():
            return None
        out = subprocess.run([encspect, "--ndjson", str(source)], capture_output=True, text=True)
        line = out.stdout.splitlines()[0] if out.stdout.strip() else None
        return json.loads(line) if line else None
    stem = pathlib.PurePosixPath(path).with_suffix("")
    report = fixtures / (stem.as_posix() + ".report.json")
    if not report.exists():
        return None
    data = json.loads(report.read_text(encoding="utf-8"))
    return data[0] if isinstance(data, list) else data


def audio_stream(report):
    for stream in report.get("streams", []):
        if "Windows Media Audio" in str(stream.get("codec", "")):
            return stream
    return report["streams"][0] if report.get("streams") else None


def nodes_of(node):
    yield node
    for child in node.get("children", []):
        yield from nodes_of(child)


def analyzer_frames(report):
    """(offset, samples) of each frame of the first Windows Media Audio stream whose frames list blocks, or None."""
    for node in nodes_of(report["root"]):
        if node.get("kind") == "Stream" and str(node.get("name", "")).startswith("Windows Media Audio"):
            frames = next((c for c in node.get("children", []) if c["name"] == "Frames"), None)
            if frames is None:
                return None
            result = []
            for frame in frames.get("children", []):
                samples = 0
                for child in frame.get("children", []):
                    m = re.match(r"^(\d+) samples\b", str(child.get("value", "")))
                    if m:
                        samples += int(m.group(1))
                result.append((frame["offset"], samples))
            return result
    return None


def ffmpeg_samples(path):
    out = subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-map", "0:a:0", "-f", "framecrc", "-"], capture_output=True, text=True)
    if out.returncode != 0:
        return None, f"ffmpeg exited with {out.returncode}: {out.stderr.strip().splitlines()[-1][:80] if out.stderr.strip() else ''}"
    durations = [int(line.split(",")[3]) for line in out.stdout.splitlines() if line and not line.startswith("#")]
    return sum(durations), None


def hr_failed(text):
    return text is not None and int(text, 16) >= 0x80000000


def reader_events(reader):
    """Counts of the Source Reader's errors, gaps (stream ticks and timestamp jumps) and discontinuities."""
    events = {"errors": 0, "gaps": 0, "discontinuities": 0, "other": 0}
    previous_end = None
    for read in reader.get("reads", []):
        if hr_failed(read.get("hresult")) or "error" in read.get("flagNames", []):
            events["errors"] += 1
        if "streamTick" in read.get("flagNames", []):
            events["gaps"] += 1
        if any(w in read.get("flagNames", []) for w in ("newStream", "nativeMediaTypeChanged", "currentMediaTypeChanged")):
            events["other"] += 1
        if any(a.startswith("9cdf01d9-a0f0-43ba-b077-eaa06cbd728a=") and not a.endswith("=0") for a in read.get("attributes", [])):
            events["discontinuities"] += 1
        if "duration" in read:
            if previous_end is not None and abs(read["timestamp"] - previous_end) > 10000:  # more than 1 ms; the durations are rounded to 100 ns
                events["gaps"] += 1
            previous_end = read["timestamp"] + read["duration"]
    return events


def object_table(direct, frames):
    """Per media object: the decoder's samples and the analyzer's, from the frames that start in the object's bytes."""
    spans = []
    for obj in direct.get("objects", []):
        for offset, length in obj["ranges"]:
            spans.append((offset, offset + length, obj["index"]))
    spans.sort()
    starts = [s[0] for s in spans]
    expected = {}
    for offset, samples in frames:
        i = bisect.bisect_right(starts, offset) - 1
        if i >= 0 and offset < spans[i][1]:
            expected[spans[i][2]] = expected.get(spans[i][2], 0) + samples
    rows = []
    for entry in direct.get("inputs", []):
        rows.append((entry["index"], entry, expected.get(entry["index"], 0)))
    return rows


def fmt(value):
    return "-" if value is None else str(value)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("artifact", type=pathlib.Path)
    parser.add_argument("--fixtures", type=pathlib.Path, default=pathlib.Path("tests/fixtures/asf"))
    parser.add_argument("--encspect", help="run this encspect CLI on the fixtures instead of reading the committed reports")
    parser.add_argument("--no-ffmpeg", action="store_true")
    parser.add_argument("--only", help="only files whose name contains this text")
    parser.add_argument("--objects", type=int, default=6, help="media objects listed per file where they differ (default 6)")
    parser.add_argument("--json", type=pathlib.Path, help="also write the rows as JSON")
    args = parser.parse_args()

    records = sorted((args.artifact / "records").glob("*.json"))
    if not records:
        print(f"no records in {args.artifact / 'records'}", file=sys.stderr)
        return 1
    env_path = args.artifact / "environment.json"
    if env_path.exists():
        env = json.loads(env_path.read_text(encoding="utf-8"))
        print(f"Windows: {env.get('productName')} {env.get('displayVersion')} build {env.get('currentBuild')}.{env.get('updateBuildRevision')} ({env.get('architecture')}), image {env.get('runnerImageOS')} {env.get('runnerImageVersion')}")
        print("Decoders: " + ", ".join(f"{k} {v}" for k, v in env.get("decodersUsed", {}).items()))
        print()
    ffmpeg = None if args.no_ffmpeg else shutil.which("ffmpeg")
    if ffmpeg:
        version = subprocess.run([ffmpeg, "-version"], capture_output=True, text=True).stdout.splitlines()
        print(f"ffmpeg column: {version[0] if version else ffmpeg} (the local ffmpeg; the artifact's environment.json names the runner's: {env.get('ffmpeg', 'not recorded') if env_path.exists() else 'no environment.json'})")
        print()

    rows = []
    details = []
    for path in records:
        record = json.loads(path.read_text(encoding="utf-8"))
        name = record["file"]
        if args.only and args.only not in name:
            continue
        reader = record.get("sourceReader") or {}
        direct = record.get("direct") or {}
        asf = direct.get("asf") or {}
        tag = asf.get("formatTag")
        report = load_report(record.get("path", name), args.fixtures, args.encspect)
        stream = audio_stream(report) if report else None
        analyzer = stream.get("samples") if stream else None
        ffm = None
        ffmpeg_error = None
        source = args.fixtures / record.get("path", name)
        if ffmpeg and source.exists():
            ffm, ffmpeg_error = ffmpeg_samples(source)

        if reader.get("crashed"):
            reader_text = f"crashed {reader.get('exitCode')}"
        elif reader.get("timedOut"):
            reader_text = "timed out"
        elif reader.get("stalled"):
            reader_text = "stalled"
        elif not reader.get("opened"):
            reader_text = f"not opened {reader.get('openHresult', '')}".strip()
        elif not reader.get("outputSet"):
            reader_text = "no PCM output"
        else:
            reader_text = str(reader.get("totalSamples"))
        # The reader's total is the reference only where it reached the end of the stream with no failed
        # read, no stall and no stop by the probe; else it is the samples before the stop, and the DMO's total is the reference.
        reader_complete = bool(reader.get("outputSet") and reader.get("endOfStream") and not reader.get("stoppedBy")
                               and not reader.get("stalled") and not reader.get("errorCount"))
        reader_total = reader.get("totalSamples") if reader_complete else None
        if reader.get("outputSet") and not reader_complete and not reader.get("crashed") and not reader.get("timedOut"):
            reader_text = f"stopped at {reader.get('totalSamples')}" if not reader.get("stalled") else "stalled"

        if direct.get("crashed"):
            direct_text = f"crashed {direct.get('exitCode')}"
        elif direct.get("timedOut"):
            direct_text = "timed out"
        elif direct.get("stalled") or direct.get("exception"):
            direct_text = f"{'stalled' if direct.get('stalled') else 'failed'} at {direct.get('partialSamples', direct.get('totalSamples', 0))}"
        elif "totalSamples" in direct:
            direct_text = str(direct["totalSamples"])
        else:
            direct_text = direct.get("error") or asf.get("error") or "no decoder"
        # A partial total of a run that stalled or failed is never a figure to compare.
        direct_total = None if direct.get("stalled") or direct.get("exception") else direct.get("totalSamples")
        encrypted = bool(stream and (stream.get("protection") or {}).get("isEncrypted")) or bool(report and any(f.get("code") == "asf.content-encrypted" for f in report.get("findings", [])))
        reference, reference_from = (None, None) if encrypted else (reader_total, "reader") if reader_total is not None else (direct_total, "decoder") if direct_total is not None else (None, None)
        if encrypted:
            reader_total = direct_total = None
            notes_encrypted = True
        else:
            notes_encrypted = False

        notes = []
        if notes_encrypted:
            notes.append("payloads are encrypted: no reference, the decoders' figures are not compared")
        if ffmpeg_error:
            notes.append(ffmpeg_error)
        figures = {"analyzer": analyzer, "ffmpeg": ffm, "reader": reader_total, "decoder": direct_total}
        known = {k: v for k, v in figures.items() if v is not None}
        if len(set(known.values())) > 1:
            groups = {}
            for k, v in known.items():
                groups.setdefault(v, []).append(k)
            notes.append("; ".join("=".join(g) for g in groups.values()) + " differ")
        elif known:
            notes.append("agree")
        if reader.get("stalled"):
            notes.append(f"reader stalled after {reader.get('calls')} ReadSample calls, {reader.get('sampleCount')} samples, last timestamp {reader.get('lastTimestamp')}, "
                         f"{'inside ReadSample since ' + str(reader.get('readStartedAtMs')) + ' ms' if reader.get('inReadSample') else 'between calls'}")
        if reader.get("stoppedBy"):
            notes.append(f"reader stopped ({reader['stoppedBy']}) after {reader.get('totalSamples')} samples" + ("; reference is the decoder's" if direct_total is not None else "; no reference"))
        nots = record.get("directWithoutTimestamps") or {}
        if not notes_encrypted and "totalSamples" in nots and nots["totalSamples"] != direct_total:
            notes.append(f"decoder without timestamps: {nots['totalSamples']}")
        events = reader_events(reader) if reader.get("opened") else {}
        if events and any(events.values()):
            notes.append("reader " + ", ".join(f"{v} {k}" for k, v in events.items() if v))
        # A first ProcessInput refused with DMO_E_NOTACCEPTING that the retry then took is not a failure.
        failed = [e for e in direct.get("inputs", [])
                  if (hr_failed(e.get("processInputHresult")) and not (e.get("retryHresult") and not hr_failed(e["retryHresult"])))
                  or hr_failed(e.get("retryHresult")) or any(hr_failed(o.get("hresult")) for o in e.get("outputs", []))]
        silent = [e for e in direct.get("inputs", []) if e.get("frames") == 0 and e not in failed]
        if failed:
            notes.append(f"{len(failed)} objects with a failing HRESULT")
        if silent:
            notes.append(f"{len(silent)} objects without output")
        rows.append({"file": name, "tag": tag, "analyzer": analyzer, "ffmpeg": ffm, "reader": reader_total, "reader_text": reader_text,
                     "decoder": direct_total, "decoder_text": direct_text, "reference": reference, "reference_from": reference_from, "notes": notes})

        if tag in PER_OBJECT_TAGS and report and "inputs" in direct:
            frames = analyzer_frames(report)
            if frames:
                table = object_table(direct, frames)
                diff = [(i, e, x) for i, e, x in table if e.get("frames") != x]
                if diff:
                    details.append((name, len(table), diff))

    # Files whose audio stream is not one of the WMA formats (PCM, MPEG audio, none) are counted apart.
    others = [r for r in rows if r["tag"] not in TAGS]
    rows = [r for r in rows if r["tag"] in TAGS]
    width = max((len(r["file"]) for r in rows), default=10)
    def reference_text(r):
        return "-" if r["reference"] is None else f"{r['reference']} ({r['reference_from']})"

    rw = max([len("reference")] + [len(reference_text(r)) for r in rows])
    cw = max([len("reader"), len("decoder")] + [len(r[k]) for r in rows for k in ("reader_text", "decoder_text")])
    header = f"{'file'.ljust(width)}  {'format':<12} {'reference':>{rw}} {'analyzer':>9} {'ffmpeg':>9} {'reader':>{cw}} {'decoder':>{cw}}  notes"
    print(header)
    print("-" * len(header))
    for r in rows:
        print(f"{r['file'].ljust(width)}  {TAGS[r['tag']]:<12} {reference_text(r):>{rw}} {fmt(r['analyzer']):>9} {fmt(r['ffmpeg']):>9} {r['reader_text']:>{cw}} {r['decoder_text']:>{cw}}  {'; '.join(r['notes'])}")
    if others:
        by_tag = {}
        for r in others:
            by_tag.setdefault(hex(r["tag"]) if r["tag"] is not None else "no audio stream", []).append(r["file"])
        print(f"\nNot WMA, left out of the table and the counts ({len(others)} files): " + "; ".join(f"{len(v)} with format tag {k}" for k, v in sorted(by_tag.items())))

    if details:
        print("\nMedia objects whose decoded samples differ from the analyzer's frames (versions 1 and 2):")
        for name, total, diff in details:
            print(f"  {name}: {len(diff)} of {total} objects")
            for index, entry, expected in diff[:args.objects]:
                print(f"    object {index} (number {entry['number']}, {entry['bytes']} bytes, {'complete' if entry['isComplete'] else 'incomplete'}): decoder {entry.get('frames')}, analyzer {expected}, ProcessInput {entry.get('processInputHresult')}")

    print("\nSummary:")
    n = len(rows)

    def count(a, b):
        pairs = [(r[a], r[b]) for r in rows if r[a] is not None and r[b] is not None]
        return sum(1 for x, y in pairs if x == y), len(pairs)

    for label, a, b in (("reference = analyzer", "reference", "analyzer"), ("reference = ffmpeg", "reference", "ffmpeg"), ("ffmpeg = analyzer", "ffmpeg", "analyzer"),
                        ("reader = decoder (reader complete)", "reader", "decoder")):
        same, of = count(a, b)
        print(f"  {label}: {same} of {of} files where both are known")
    print("\nPer format (files where both figures are known / agreeing):")
    print(f"  {'format':<14} {'files':>5} {'reference=analyzer':>19} {'reference=ffmpeg':>17} {'ffmpeg=analyzer':>16} {'reader=decoder':>15}")
    for tag in sorted({r["tag"] for r in rows}):
        group = [r for r in rows if r["tag"] == tag]
        cells = []
        for a, b in (("reference", "analyzer"), ("reference", "ffmpeg"), ("ffmpeg", "analyzer"), ("reader", "decoder")):
            pairs = [(r[a], r[b]) for r in group if r[a] is not None and r[b] is not None]
            cells.append(f"{sum(1 for x, y in pairs if x == y)}/{len(pairs)}")
        print(f"  {TAGS[tag]:<14} {len(group):>5} {cells[0]:>19} {cells[1]:>17} {cells[2]:>16} {cells[3]:>15}")
    print(f"  WMA files: {n}; no reference for {sum(1 for r in rows if r['reference'] is None)}; the reader stopped, stalled or crashed on {sum(1 for r in rows if r['reader'] is None and r['reader_text'].split()[0] in ('stopped', 'stalled', 'crashed', 'timed'))}")

    if args.json:
        args.json.write_text(json.dumps(rows, indent=1) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
