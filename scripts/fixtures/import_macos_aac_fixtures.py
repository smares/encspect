#!/usr/bin/env python3
"""Takes the files of the apple-aac-fixtures workflow's artifact into tests/fixtures.

    python3 scripts/fixtures/import_macos_aac_fixtures.py path/to/extracted/artifact [--all | --only NAME ...] [--runner NAME] [--identical-with DIR] [--force] [--allow-large]
    python3 scripts/fixtures/import_macos_aac_fixtures.py path/to/macos-15/artifact --compare path/to/macos-15-intel/artifact

The artifact is the directory make_macos_aac_fixtures.sh wrote (manifest.json, encoded/, pcm/, info/,
logs/). The importer checks it first: every encode that worked has a file whose SHA-256 and size
are the manifest's, a source whose SHA-256 is the one the manifest records, and an afinfo reading;
a file that is the same bytes as an earlier one is not imported twice. It then copies, byte for byte
and unrewritten:

- AAC in M4A, MP4, 3GP and ADTS to tests/fixtures/aac/apple-<name>, where the other encoders'
  files are (the GOLDEN set, which has manifests and golden reports), or to tests/fixtures/apple (the rest), and Apple Lossless to tests/fixtures/alac/apple-<name> (ALAC is phase 3: the analyzer
  shows the container and an unknown codec today, and the files wait for its parser);
- AAC in CAF to tests/fixtures/caf/apple-<name>.caf, for a container the analyzer does not read yet.

Next to each file goes apple-<name>.provenance.json: the command line, file type, format ID, strategy and options,
afconvert's exit status and the afinfo reading, the source PCM (the arguments of wma_signals.py) and its SHA-256, the macOS
version, hardware and framework versions of the runner, and the file's SHA-256. The output is not reproducible: the muxers
stamp the time of the encode, so the files are committed once and checked by their content. The manifests of the AAC files
(.expected.json) are written by make_external_aac_fixtures.py --manifests-only from their entries there, which hold what
the analyzer must say and what ffprobe, MP4Box and faad confirm.

Without --all a chosen set is imported (CHOSEN below); --only takes exact names. --identical-with DIR records, in the provenance of a file the other runner's artifact in DIR wrote with the same bytes, that runner's
environment, so one record stands for both. --runner NAME imports the artifact of
another runner under apple-NAME-<name> (the Intel leg's, as apple-intel-<name>). A file that is already
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
CONTAINERS = {"m4af": "MP4", "mp4f": "MP4", "3gpp": "MP4", "3gp2": "MP4", "adts": "AAC", "caff": "CAF", "m4bf": "MP4"}

# The set imported by default: AAC-LC in M4A with a constant bitrate (stereo, mono, the 32 kHz rate Apple picks at
# 64 kbit/s) and a variable one, ADTS and 3GP; HE-AAC and HE-AAC v2 in M4A and ADTS (stereo SBR with a FIXVAR frame
# grid, mono); AAC-LD and AAC-ELD; 5.1, Sound Check (iTunNORM, loudness box) and 16-bit sample sizes (stz2); and ALAC at 16 and 24 bits in both containers. The golden reports are 0.6 to 1.7 MB each, so
# only the GOLDEN set below goes to tests/fixtures/aac; --all imports everything.
CHOSEN = [
    "lc-m4a-cbr-st44", "lc-m4a-cbr-mono44", "lc-m4a-cbr-st44-64", "lc-m4a-vbr-q64-st44", "lc-adts-cbr-st44", "lc-3gp-cbr-st44",
    "he-m4a-cbr-st44", "he-m4a-cbr-mono44", "he-adts-cbr-st44", "hev2-m4a-cbr-st44", "hev2-adts-cbr-st44",
    "aacl-m4a-cbr-mono44", "aace-m4a-cbr-mono44",
    "lc-m4a-cbr-six48-lout", "lc-m4a-soundcheck-st44", "lc-m4a-condensed-st44",
    "alac-m4a-st44q16", "alac-caf-st44q16", "alac-m4a-st44q24", "alac-caf-st44q24",
    "he-m4a-cbr-st44-32", "he-m4a-cbr-mono22", "lc-m4a-music-st44", "lc-m4b-cbr-st44",
    "passthru-m4a-from-fdkaac-he-adts", "passthru-adts-from-fdkaac-m4a", "passthru-m4a-from-fdkaac-m4a", "passthru-m4a-from-ffmpeg-adts",
    "passthru-m4a-from-faac-m4a", "passthru-adts-from-ffmpeg-adts",
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


# afinfo prints "2 ch,  22050 Hz, aac  (0x00000000) 0 bits/channel, ..." and in the format list "aach (0x00000000)": the ID, padded to four, then its flags
AFINFO_FORMAT = re.compile(r"(\d+) ch,\s+(\d+) Hz,\s+(\S{1,4})\s*\(")


def read_afinfo(text, format_id=None):
    """What afinfo says of the file's data format: channels, rate and format ID of the entry of its format list that
    has the format ID asked for (HE-AAC's first line is the core's 'aac ' at half the rate), else of the first, and
    its lines as they are."""
    entries = AFINFO_FORMAT.findall(text)
    found = next((e for e in entries if e[2].strip() == (format_id or "").strip()), entries[0] if entries else None)
    return {
        "channels": int(found[0]) if found else None,
        "sample_rate": int(found[1]) if found else None,
        "format_id": found[2] if found else None,
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


# The files with a manifest and a golden report in tests/fixtures/aac, which the MP4 fixture tests and the cross-check walk;
# a golden report is 0.6 to 1.7 MB, so the others (the other runner's, the rest of the containers and options) go to
# tests/fixtures/apple, where only AppleEncoderTests reads them.
GOLDEN = {
    "lc-m4a-cbr-st44", "lc-adts-cbr-st44", "he-m4a-cbr-st44", "he-m4a-cbr-mono44", "hev2-m4a-cbr-st44", "hev2-adts-cbr-st44",
    "aacl-m4a-cbr-mono44", "aace-m4a-cbr-mono44",
}


def destination(encode, runner=False):
    extension = pathlib.Path(encode["output"]).suffix
    if encode["format"] == "alac":
        return FIXTURES / "alac", extension
    if encode["file_type"] == "caff":
        return FIXTURES / "caf", extension
    return (FIXTURES / "aac" if encode["name"] in GOLDEN and not runner else FIXTURES / "apple"), extension


def import_encode(artifact, encode, sources, environment, force, allow_large, prefix="apple-", identical=None):
    name = encode["name"]
    data = (artifact / encode["output"]).read_bytes()
    if len(data) > LARGE and not allow_large:
        print(f"{name}: skipped, {len(data)} bytes is over {LARGE}; pass --allow-large")
        return False
    directory, extension = destination(encode, prefix != "apple-")
    directory.mkdir(parents=True, exist_ok=True)
    target = directory / f"{prefix}{name}{extension}"
    if target.exists() and target.read_bytes() != data and not force:
        print(f"{name}: skipped, {target.name} exists with other bytes; pass --force")
        return False
    shutil.copyfile(artifact / encode["output"], target)
    source = sources[encode["source"]]
    afinfo = read_afinfo((artifact / encode["afinfo"]).read_text(encoding="utf-8", errors="replace"), encode["format"])
    container = CONTAINERS.get(encode["file_type"], encode["file_type"])
    if source.get("foreign"):
        describe = f"afconvert {' '.join(encode['command'].split()[1:])}: the packets of {source['from']} copied (-d 0), not encoded."
    else:
        describe = f"afconvert {' '.join(encode['command'].split()[1:])} on {source['seconds']} s of the {source['signal']} signal of {source.get('generator', 'wma_signals.py')}."
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
        "input": {k: source[k] for k in ("name", "from", "channels", "rate", "bits", "seconds", "signal", "generator", "channel_mask", "channel_layout_tag", "info_tags", "sha256") if k in source},
        "runner": runner(environment),
        "identical_on": identical or [],
        "bytes": len(data),
        "sha256": sha256(data),
        "reproducible": False,
        "note": "The muxer stamps the time of the encode into the file; the file is committed as written.",
    }
    (directory / f"{prefix}{name}.provenance.json").write_text(json.dumps(provenance, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
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
    prefix = f"apple-{rest[rest.index('--runner') + 1]}-" if "--runner" in rest else "apple-"
    other = pathlib.Path(rest[rest.index("--identical-with") + 1]) if "--identical-with" in rest else None
    twins = {}
    if other is not None:
        twin = load(other)
        for e in twin["encodes"]:
            if e["exit_status"] == 0 and e.get("sha256"):
                twins[e["name"]] = (e["sha256"], runner(twin.get("environment", "")))
    imported = sum(import_encode(artifact, e, sources, environment, force, allow_large, prefix,
                                 [twins[e["name"]][1]] if e["name"] in twins and twins[e["name"]][0] == e["sha256"] else None) for e in worked if wanted is None or e["name"] in wanted)
    print(f"{imported} fixtures imported.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
