#!/usr/bin/env python3
"""Takes the files of the apple-aac-fixtures workflow's artifact into tests/fixtures.

    python3 scripts/fixtures/import_macos_aac_fixtures.py path/to/extracted/artifact [--all | --only NAME ...] [--force] [--allow-large]
    python3 scripts/fixtures/import_macos_aac_fixtures.py path/to/macos-15/artifact --compare path/to/macos-15-intel/artifact

The artifact is the directory make_macos_aac_fixtures.sh wrote (manifest.json, encoded/, pcm/, info/,
logs/). The importer checks it first: every encode that worked has a file whose SHA-256 and size
are the manifest's, a source whose SHA-256 is the one the manifest records, and an afinfo reading;
a file that is the same bytes as an earlier one is not imported twice. It then copies, byte for byte
and unrewritten:

- AAC in M4A, MP4, 3GP and ADTS to tests/fixtures/aac/apple-<name>, where the other encoders'
  files are, and Apple Lossless to tests/fixtures/alac/apple-<name> (ALAC is phase 3: the analyzer
  shows the container and an unknown codec today, and the files wait for its parser);
- AAC in CAF to tests/fixtures/caf/apple-<name>.caf, for a container the analyzer does not read yet.

Next to each file go two records:

- apple-<name>.provenance.json: the command line, file type, format ID, strategy and options, afconvert's
  exit status and the afinfo reading, the source PCM (the arguments of wma_signals.py) and its SHA-256, the
  macOS version, hardware and framework versions of the runner, and the file's SHA-256. The output is not
  reproducible: the muxers stamp the time of the encode, so the files are committed once and checked by
  their content.
- apple-<name>.expected.json in the style of make_external_aac_fixtures.py for the AAC files: the
  command, the length, the container and the stream as afconvert and afinfo say it (codec, rate, channels,
  bitrate mode), and, where ffprobe and MP4Box are installed, the packets and boxes they list. It holds no
  statement about the encoder: what the analyzer must say of the encoder rests on the files' content and is
  written once that has been looked at. "fields" and "findings", which come from the analyzer's own run, are
  left out until then.

Without --all a chosen set is imported (CHOSEN below); --only takes exact names. A file that is already
there is left alone unless it is the same bytes or --force is given. Files over 600 KB are refused unless
--allow-large is given: a fixture is a few seconds of audio.

--compare takes a second artifact (the other runner's) and prints, for every encode in both, whether the
bytes are the same, and for those that differ how many bytes and where, so the architecture's effect on
the encoder can be read; it imports nothing.
"""

import hashlib
import json
import pathlib
import re
import shutil
import sys

ROOT = pathlib.Path(__file__).resolve().parent
FIXTURES = ROOT.parents[1] / "tests" / "fixtures"
LARGE = 600_000

CODECS = {"aac": "AAC LC", "aach": "HE-AAC", "aacp": "HE-AAC v2", "alac": "Apple Lossless", "aacl": "AAC LD", "aace": "AAC ELD", "aacf": "AAC ELD v2",
          "aacg": "AAC ELD SBR", "usac": "xHE-AAC (USAC)"}
MODES = {"cbr": "constant bitrate (-s 0)", "abr": "average bitrate (-s 1)", "constrained-vbr": "constrained variable bitrate (-s 2)",
         "vbr": "variable bitrate (-s 3)", "lossless": "lossless", "default": "afconvert's default strategy"}
# Containers by file type; the analyzer's format name for the manifest.
CONTAINERS = {"m4af": "MP4", "mp4f": "MP4", "3gpp": "MP4", "3gp2": "MP4", "adts": "AAC", "caff": "CAF"}

# The set imported by default: each format ID in M4A with a constant, average, constrained and
# variable bitrate, mono, 48 kHz and 5.1, the other containers once, and ALAC at 16 and 24 bits in
# both containers. Golden reports are 2.5 MB each, so the set stays small.
CHOSEN = [
    "lc-m4a-cbr-st44", "lc-m4a-cbr-mono44", "lc-m4a-cbr-st48", "lc-m4a-cbr-six48", "lc-m4a-default-st44", "lc-m4a-abr-st44", "lc-m4a-cvbr-st44",
    "lc-m4a-vbr-q64-st44", "lc-m4a-tagged-st44", "lc-adts-cbr-st44", "lc-adts-vbr-q64-st44", "lc-caf-cbr-st44", "lc-3gp-cbr-st44", "lc-mp4-cbr-st44",
    "he-m4a-cbr-st44", "he-m4a-cbr-mono44", "he-m4a-cbr-st22", "he-m4a-vbr-q64-st44", "he-adts-cbr-st44", "he-caf-cbr-st44",
    "hev2-m4a-cbr-st44", "hev2-m4a-cbr-st44-24", "hev2-m4a-vbr-q64-st44", "hev2-adts-cbr-st44",
    "alac-m4a-st44q16", "alac-caf-st44q16", "alac-m4a-st44q24", "alac-caf-st44q24", "alac-m4a-six48q16",
]


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def load(artifact):
    path = artifact / "manifest.json"
    if not path.is_file():
        raise SystemExit(f"{path} is missing: this is not the artifact of the apple-aac-fixtures workflow.")
    manifest = json.loads(path.read_text(encoding="utf-8"))
    if manifest.get("schema") != 1:
        raise SystemExit(f"manifest.json has schema {manifest.get('schema')}, expected 1.")
    return manifest


def verify(artifact, manifest):
    """Checks the files against the manifest; returns the encodes that worked, without the ones that repeat an earlier file."""
    sources = {s["name"]: s for s in manifest["sources"]}
    for source in sources.values():
        data = (artifact / source["file"]).read_bytes()
        if sha256(data) != source["sha256"]:
            raise SystemExit(f"source {source['name']}: the SHA-256 of {source['file']} is not the manifest's.")
    worked = []
    seen = {}
    for encode in manifest["encodes"]:
        name = encode["name"]
        if encode["exit_status"] != 0 or not encode.get("bytes"):
            print(f"{name}: skipped, afconvert exited {encode['exit_status']}; see {encode['log']}")
            continue
        data = (artifact / encode["output"]).read_bytes()
        if len(data) != encode["bytes"] or sha256(data) != encode["sha256"]:
            raise SystemExit(f"{name}: {encode['output']} is not the file the manifest describes.")
        if encode["source"] not in sources or encode["source_sha256"] != sources[encode["source"]]["sha256"]:
            raise SystemExit(f"{name}: its source {encode['source']} is not the one recorded.")
        if not encode.get("afinfo") or not (artifact / encode["afinfo"]).is_file():
            raise SystemExit(f"{name}: there is no afinfo reading.")
        if encode["sha256"] in seen:
            print(f"{name}: skipped, the same file as {seen[encode['sha256']]}")
            continue
        seen[encode["sha256"]] = name
        worked.append(encode)
    return worked, sources


AFINFO_FORMAT = re.compile(r"Data format:\s+(\d+) ch,\s+(\d+) Hz,\s+'(.{4})'")


def read_afinfo(text):
    """What afinfo says of the file's data format: channels, rate and format ID, and its other lines as they are."""
    found = AFINFO_FORMAT.search(text)
    return {
        "channels": int(found.group(1)) if found else None,
        "sample_rate": int(found.group(2)) if found else None,
        "format_id": found.group(3) if found else None,
        "lines": [line.rstrip() for line in text.splitlines() if line.strip()],
    }


def runner(environment):
    """The runner's macOS version, hardware and the versions of the frameworks, from environment.txt."""
    lines = environment.splitlines()
    def after(heading):
        out = []
        started = False
        for line in lines:
            if line.startswith("--- "):
                if started:
                    break
                started = line.startswith("--- " + heading)
                continue
            if started and line.strip():
                out.append(line.strip())
        return out
    sw_vers = {}
    for line in after("sw_vers"):
        key, _, value = line.partition(":")
        sw_vers[key.strip()] = value.strip()
    image = next((l for l in lines if l.startswith("runner image:")), "")
    return {
        "image": image.removeprefix("runner image:").strip(),
        "sw_vers": sw_vers,
        "uname": " ".join(after("uname")),
        "hardware": after("hardware"),
        "bundles": after("framework and codec bundle versions"),
    }


def stream_expectation(encode, source, afinfo):
    entry = {"codec": CODECS.get(encode["format"], encode["format"])}
    entry["sample_rate"] = afinfo["sample_rate"] or source["rate"]
    entry["channels"] = afinfo["channels"] or source["channels"]
    entry["bitrate_mode"] = MODES.get(encode["strategy"], encode["strategy"])
    if encode["format"] != "alac":
        entry["requested_bitrate"] = next((int(v) for k, v in zip(encode["flags"].split(), encode["flags"].split()[1:]) if k == "-b"), None)
    entry["afinfo_format_id"] = afinfo["format_id"]
    return entry


def tool_lists(path, container):
    """Packets by ffprobe and boxes by MP4Box where they are installed; null otherwise."""
    sys.path.insert(0, str(ROOT))
    try:
        import crosscheck_mp4_fixtures as cc
    except ImportError:
        return None, None
    units = boxes = None
    try:
        units = cc.ffprobe_packets(path)
    except Exception:  # ffprobe missing, or a file it cannot read
        units = None
    if container == "MP4":
        try:
            rows = cc.mp4box_boxes(path)
            boxes = [[p, None, s] for p, s in rows] if rows else None
        except OSError:
            boxes = None
    return units, boxes


def destination(encode):
    extension = pathlib.Path(encode["output"]).suffix
    if encode["format"] == "alac":
        return FIXTURES / "alac", extension
    if encode["file_type"] == "caff":
        return FIXTURES / "caf", extension
    return FIXTURES / "aac", extension


def import_encode(artifact, encode, sources, environment, force, allow_large):
    name = encode["name"]
    data = (artifact / encode["output"]).read_bytes()
    if len(data) > LARGE and not allow_large:
        print(f"{name}: skipped, {len(data)} bytes is over {LARGE}; pass --allow-large")
        return False
    directory, extension = destination(encode)
    directory.mkdir(parents=True, exist_ok=True)
    target = directory / f"apple-{name}{extension}"
    if target.exists() and target.read_bytes() != data and not force:
        print(f"{name}: skipped, {target.name} exists with other bytes; pass --force")
        return False
    shutil.copyfile(artifact / encode["output"], target)
    source = sources[encode["source"]]
    afinfo = read_afinfo((artifact / encode["afinfo"]).read_text(encoding="utf-8", errors="replace"))
    container = CONTAINERS.get(encode["file_type"], encode["file_type"])
    describe = f"afconvert {' '.join(encode['command'].split()[1:])} on {source['seconds']} s of the {source['signal']} signal of wma_signals.py."
    provenance = {
        "description": describe,
        "encoder": {
            "tool": "afconvert (Apple AudioToolbox)",
            "command": encode["command"],
            "file_type": encode["file_type"],
            "format_id": encode["format"],
            "codec": CODECS.get(encode["format"], encode["format"]),
            "strategy": encode["strategy"],
            "options": encode["flags"],
            "exit_status": encode["exit_status"],
            "afinfo": afinfo,
        },
        "input": {k: source[k] for k in ("name", "channels", "rate", "bits", "seconds", "signal", "generator", "channel_mask", "channel_layout_tag", "info_tags", "sha256")},
        "runner": runner(environment),
        "bytes": len(data),
        "sha256": sha256(data),
        "reproducible": False,
        "note": "The muxer stamps the time of the encode into the file; the file is committed as written.",
    }
    (directory / f"apple-{name}.provenance.json").write_text(json.dumps(provenance, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    if directory.name == "aac":
        units, boxes = tool_lists(target, container)
        manifest = {
            "description": describe,
            "encoder": {"command": encode["command"].split()},
            "length": len(data),
            "format": container,
            "streams": [stream_expectation(encode, source, afinfo)],
        }
        if boxes is not None:
            manifest["boxes"] = boxes
        if units is not None:
            manifest["units"] = units
        (directory / f"apple-{name}.expected.json").write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{name}: {len(data)} bytes into {directory.relative_to(FIXTURES.parent.parent)}")
    return True


def compare(first, second):
    one, two = load(first), load(second)
    left = {e["name"]: e for e in one["encodes"] if e["exit_status"] == 0 and e.get("bytes")}
    right = {e["name"]: e for e in two["encodes"] if e["exit_status"] == 0 and e.get("bytes")}
    print(f"A: {first}\nB: {second}")
    same = 0
    for name in sorted(set(left) | set(right)):
        if name not in left or name not in right:
            print(f"{name}: only in {'A' if name in left else 'B'}")
            continue
        a = (first / left[name]["output"]).read_bytes()
        b = (second / right[name]["output"]).read_bytes()
        if a == b:
            same += 1
            continue
        if len(a) != len(b):
            print(f"{name}: differs, {len(a)} against {len(b)} bytes")
            continue
        positions = [i for i in range(len(a)) if a[i] != b[i]]
        print(f"{name}: differs in {len(positions)} of {len(a)} bytes, first at {positions[0]}, last at {positions[-1]}")
    print(f"{same} encodes are byte for byte the same.")


def main():
    arguments = sys.argv[1:]
    if not arguments or arguments[0].startswith("--"):
        raise SystemExit(__doc__)
    artifact = pathlib.Path(arguments[0])
    rest = arguments[1:]
    if "--compare" in rest:
        compare(artifact, pathlib.Path(rest[rest.index("--compare") + 1]))
        return 0
    force = "--force" in rest
    allow_large = "--allow-large" in rest
    only = None
    if "--only" in rest:
        only = [a for a in rest[rest.index("--only") + 1:] if not a.startswith("--")]
    manifest = load(artifact)
    worked, sources = verify(artifact, manifest)
    environment = manifest.get("environment", "")
    wanted = None if "--all" in rest else only if only is not None else CHOSEN
    names = {e["name"] for e in worked}
    for name in wanted or []:
        if name not in names:
            print(f"{name}: not in the artifact or did not work")
    imported = sum(import_encode(artifact, e, sources, environment, force, allow_large) for e in worked if wanted is None or e["name"] in wanted)
    print(f"{imported} fixtures imported.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
