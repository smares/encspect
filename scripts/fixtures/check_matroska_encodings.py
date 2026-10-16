#!/usr/bin/env python3
"""Checks the analyzer's reading of Matroska ContentEncodings against an independent reference model, on random combinations and on every permutation of the
fixtures whose encodings share an order.

    python3 scripts/fixtures/check_matroska_encodings.py [--cli "dotnet path/to/encspect.dll"] [--cases 2000] [--seed 1]

The reference model is matroska_encodings_model.py, written from RFC 9559 and the project's docs without reading the analyzer; its docstring states the rules
and test_model.py (scripts/ci) works 52 cases by hand. This script writes the cases, adapts them to the model's input, and compares per track:

- protected, scheme and is_encrypted with the CLI's stream protection;
- frames_read (the frames are clear and undone, whatever the codec) with what the analyzer shows: "Track N Frames" counted frames, or it undid the frames and
  could not go on (a CodecPrivate or frames that are no zlib data in these files, a missing CodecPrivate, or a codec parser that cannot take frames put together
  from other bytes: the AAC, AC-3, DTS and MPEG audio parsers place their nodes at the offsets of contiguous file bytes);
- codec_private_read with the analyzer's codec-private findings, wherever it gets as far as the CodecPrivate;
- the finding codes of CODES the docs list for encodings, with expected_codes below (the model gives no finding codes).
"""

import argparse
import itertools
import json
import os
import pathlib
import random
import re
import subprocess
import sys
import tempfile

import make_matroska_fixtures as f
import matroska_encodings_model as reference

ROOT = pathlib.Path(__file__).resolve().parents[2]
CLI_DLL = ROOT / "src" / "Encspect.Cli" / "bin" / "Release" / "net10.0" / "encspect.dll"
CODES = {f"matroska.{c}" for c in (
    "encoding-order", "encoding-hidden", "encryption-ignored", "encryption-none", "encryption-missing", "encryption-covers-nothing", "aes-settings-not-aes",
    "track-encrypted", "track-encoding-not-read", "codec-private-encrypted", "codec-private-not-read", "codec-private-invalid", "codec-private-missing",
    "frame-encoding-invalid")}


def _model(encodings, has_private):
    """The encodings sorted from the highest order, with the helpers the finding codes use."""
    es = sorted(encodings, key=lambda e: -e["order"])

    def hiders(i):
        return hiders(i - 1) + [es[i - 1]] if i > 0 and es[i - 1]["scope"] & 4 else []

    def encrypts(e):
        return e["type"] == 1 and not (e["has_encryption"] and (e["encryption"] or 0) == 0)

    def verdict(i):
        """Whether the encoding at i is encrypted."""
        h = hiders(i)
        return encrypts(es[i]) if not h else encrypts(h[0]) or any(x["type"] == 1 for x in h[1:]) or es[i]["type"] == 1

    def covering(bit):
        return [i for i in range(len(es)) if es[i]["scope"] & bit and (bit == 1 or has_private)]

    def undoable(i):
        return not hiders(i) and es[i]["type"] == 0 and (es[i]["compression"] if es[i]["compression"] is not None else 0) in (0, 3)

    def is_read(bit):
        return all(not hiders(i) and (es[i]["type"] == 1 and not encrypts(es[i]) or undoable(i)) for i in covering(bit))

    def is_encrypted(bit):
        return any(verdict(i) for i in covering(bit))

    def has_zlib(bit):
        return any(undoable(i) and (es[i]["compression"] if es[i]["compression"] is not None else 0) == 0 for i in covering(bit))

    def steps(bit):
        """The compressions undone before the first encoding that cannot be undone."""
        count = 0
        for i in covering(bit):
            if undoable(i):
                count += 1
            elif hiders(i) or es[i]["type"] != 1 or encrypts(es[i]):
                break
        return count

    return es, hiders, encrypts, covering, is_read, is_encrypted, has_zlib, steps


def to_reference(codec, has_private, encodings):
    """The reference model's input for a track: encodings as dicts of order, scope, type, compression (the ContentCompAlgo, or None without the
    element), settings (whether the compression has ContentCompSettings), has_encryption (the ContentEncryption element), encryption (its ContentEncAlgo),
    cipher and has_aes_settings."""
    def encryption(e):
        if not e["has_encryption"]:
            return None
        return {"algo": e["encryption"], "aes_mode": e["cipher"], "has_aes_settings": e.get("has_aes_settings", e["cipher"] is not None), "has_key_id": False}

    def compression(e):
        return None if e["compression"] is None else {"algo": e["compression"], "has_settings": bool(e.get("settings"))}

    return {"codec": codec, "has_codec_private": has_private,
            "encodings": [{"order": e["order"], "scope": e["scope"], "type": e["type"], "compression": compression(e), "encryption": encryption(e)} for e in encodings]}


def reference_protection(codec, has_private, encodings):
    """(scheme, isEncrypted) or None of the reference model, for the fixture cross-check."""
    result = reference.expected(to_reference(codec, has_private, encodings))
    return (result["scheme"], result["is_encrypted"]) if result["protected"] else None


def expected_codes(encodings, has_private, codec):
    """The finding codes of CODES a track with these encodings has."""
    if len({e["order"] for e in encodings}) < len(encodings):
        return {"matroska.encoding-order", "matroska.track-encoding-not-read"}
    es, hiders, encrypts, covering, is_read, is_encrypted, has_zlib, steps = _model(encodings, has_private)
    opaque = codec in ("A_OPUS", "A_VORBIS")
    codes = set()
    for i, e in enumerate(es):
        if hiders(i):
            codes.add("matroska.encoding-hidden")
            continue
        if e["type"] == 0 and e["has_encryption"]:
            codes.add("matroska.encryption-ignored")
        elif e["type"] == 1 and not e["has_encryption"]:
            codes.add("matroska.encryption-missing")
        elif e["type"] == 1 and (e["encryption"] or 0) == 0:
            codes.add("matroska.encryption-none")
        if encrypts(e) and not e["scope"] & 1 and not (e["scope"] & 2 and has_private) and not (e["scope"] & 4 and i + 1 < len(es)):
            codes.add("matroska.encryption-covers-nothing")
        if e["type"] == 1 and e["has_aes_settings"] and e["has_encryption"] and e["encryption"] != 5:
            codes.add("matroska.aes-settings-not-aes")
    if not is_read(1):
        codes.add("matroska.track-encrypted" if is_encrypted(1) else "matroska.track-encoding-not-read")
        if not is_encrypted(1):
            return codes
    elif not opaque and (steps(1) or steps(2)):
        codes.add("matroska.track-encoding-not-read")
        return codes
    if has_private and not is_read(2):
        codes.add("matroska.codec-private-encrypted" if is_encrypted(2) else "matroska.codec-private-not-read")
        if opaque:
            return codes
    elif has_private and has_zlib(2):
        codes.add("matroska.codec-private-invalid")
        return codes
    elif not has_private and opaque:
        codes.add("matroska.codec-private-missing")
        return codes
    if opaque and is_read(1) and has_zlib(1):
        codes.add("matroska.frame-encoding-invalid")
    return codes


def random_encoding(rng, order):
    """(content_encoding arguments, model dict) of one random encoding."""
    scope = rng.randrange(16)
    kind = rng.choice(["compression", "compression", "encryption", "encryption", "encryption", "other"])
    model = dict(order=order, scope=scope, type=0, compression=None, settings=False, has_encryption=False, encryption=None, cipher=None, has_aes_settings=False)
    if kind in ("compression", "other"):
        algorithm = rng.choice([0, 1, 2, 3])
        arguments = dict(order=order, scope=scope, compression=algorithm, settings=b"\xF8" if algorithm == 3 else None)
        model["compression"], model["settings"] = algorithm, algorithm == 3
        if kind == "other":
            model["type"] = arguments["kind"] = rng.choice([2, 3, 100])
        elif rng.random() < 0.2:
            arguments["stray_encryption"] = rng.choice([0, 5])
            model["has_encryption"], model["encryption"] = True, arguments["stray_encryption"]
    else:
        model["type"] = 1
        variant = rng.choice(["algorithm", "algorithm", "algorithm", "no-algorithm", "missing"])
        if variant == "missing":
            arguments = dict(order=order, scope=scope, missing_encryption=True)
        elif variant == "no-algorithm":
            arguments = dict(order=order, scope=scope, no_algo=True, key_id=b"\x01")
            model["has_encryption"] = True
        else:
            model.update(has_encryption=True, encryption=rng.choice([0, 1, 2, 3, 4, 5, 5, 6]), cipher=rng.choice([None, 0, 1, 2, 3, 4]))
            model["has_aes_settings"] = model["cipher"] is not None
            arguments = dict(order=order, scope=scope, encryption=model["encryption"], cipher=model["cipher"], key_id=b"\x01")
    omitted = [o for o in ("order", "scope", "type") if rng.random() < 0.05]
    if omitted:
        arguments["omit"] = omitted
        defaults = dict(order=0, scope=1, type=0)
        for o in omitted:
            model[o] = defaults[o]
    return arguments, model


def write(path, tracks):
    """tracks: [(codec, content_encoding arguments, has a CodecPrivate)] of the file's tracks, numbered from 1."""
    entries, items = [], []
    for number, (codec, encodings, has_private) in enumerate(tracks, 1):
        enc = f.content_encodings(*[f.content_encoding(**kw) for kw in encodings]) if encodings else None
        if codec == "A_OPUS":
            entries.append(f.opus_track(number=number, encodings=enc, **({} if has_private else {"private": None})))
            items += [f.simple_block(number, i * 20, f.opus_packet(i), key=True) for i in range(4)]
        else:
            entries.append(f.audio_track(number, codec, 2, 44100.0, private=b"\x12\x10" if has_private else None, encodings=enc))
            items += [f.simple_block(number, i * 23, bytes((i * 7 + k) & 0xFF for k in range(40)), key=True) for i in range(4)]
    path.write_bytes(f.build(f.standard(f.cluster(0, *items), track=entries)))


def analyze(cli, directory):
    """{file name: [what the analyzer shows per track]}: the protection, the tracks whose frames were counted, the finding codes of CODES, and whether it
    undid the frames and could not map them to a codec parser."""
    result = subprocess.run([*cli, "--ndjson", str(directory)], capture_output=True, text=True, timeout=1200)
    reports = {}
    for line in result.stdout.splitlines():
        report = json.loads(line)
        counted = set()
        stack = [report["root"]]
        while stack:
            node = stack.pop()
            stack.extend(node.get("children", []))
            if (m := re.fullmatch(r"Track (\d+) Frames", node["name"])) and isinstance(node["value"], int):
                counted.add(int(m.group(1)))
        codes, unmapped = {}, set()
        for finding in report["findings"]:
            if (m := re.search(r"Track (\d+)", finding["message"])) and finding["code"] in CODES:
                codes.setdefault(int(m.group(1)), set()).add(finding["code"])
                if "cannot map back" in finding["message"]:
                    unmapped.add(int(m.group(1)))
        tracks = []
        for number, stream in enumerate(report["streams"], 1):
            protection = stream.get("protection")
            tracks.append(dict(protection=(protection["scheme"], protection["isEncrypted"]) if protection else None, counted=number in counted,
                               codes=codes.get(number, set()), unmapped=number in unmapped))
        reports[pathlib.Path(report["source"]).name] = tracks
    return reports


def compare(got, codec, has_private, models):
    """The differences between what the analyzer shows of a track and the reference model, as a list of texts."""
    want = reference.expected(to_reference(codec, has_private, models))
    codes = got["codes"]
    differences = []
    protection = (want["scheme"], want["is_encrypted"]) if want["protected"] else None
    if got["protection"] != protection:
        differences.append(f"protection {got['protection']}, model {protection}")

    # The frames are clear and undone when the analyzer counted them, or undid them and could not go on for a reason that is not about the frames.
    blocked = "matroska.track-encrypted" in codes or ("matroska.track-encoding-not-read" in codes and not got["unmapped"])
    frames = got["counted"] or got["unmapped"] or (not blocked and bool(codes & {"matroska.codec-private-invalid", "matroska.codec-private-missing"}))
    if frames != want["frames_read"]:
        differences.append(f"frames read {frames}, model {want['frames_read']}")

    # The CodecPrivate is seen where the analyzer gets as far as it: not after frames it cannot read, a codec parser it cannot map them to, or tied encodings.
    if "matroska.track-encoding-not-read" not in codes:
        private = has_private and not codes & {"matroska.codec-private-encrypted", "matroska.codec-private-not-read"}
        if private != want["codec_private_read"]:
            differences.append(f"CodecPrivate read {private}, model {want['codec_private_read']}")
    if codes != expected_codes(models, has_private, codec):
        differences.append(f"codes {sorted(codes)}, expected {sorted(expected_codes(models, has_private, codec))}")
    return differences


def tie_model(encodings):
    """The model dicts of a tied fixture's (content_encoding arguments, type) pairs."""
    return [dict(order=kw["order"], scope=kw["scope"], type=kind, compression=kw.get("compression"), settings="settings" in kw,
                 has_encryption="encryption" in kw or "no_algo" in kw or "stray_encryption" in kw, encryption=kw.get("encryption", kw.get("stray_encryption")),
                 cipher=kw.get("cipher"), has_aes_settings="cipher" in kw) for kw, kind in encodings]


# Cases where the reference model and the analyzer differ and the analyzer is right against the RFC: none.
ALLOWED = {}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cli", default=f"dotnet {CLI_DLL}")
    parser.add_argument("--cases", type=int, default=2000)
    parser.add_argument("--seed", type=int, default=1)
    options = parser.parse_args()
    cli = options.cli.split()
    rng = random.Random(options.seed)
    failures = 0
    tracks_checked = 0
    with tempfile.TemporaryDirectory() as temp:
        directory = pathlib.Path(temp)
        cases = {}
        for n in range(options.cases):
            tracks = []
            for _ in range(rng.choice([1, 1, 2, 3])):
                count = rng.randint(0, 4)
                # Orders with gaps; one track in five lets two share an order.
                orders = rng.sample(range(7), count) if rng.random() < 0.8 else [rng.randrange(3) for _ in range(count)]
                tracks.append((rng.choice(["A_OPUS", "A_OPUS", "A_AAC"]), [random_encoding(rng, order) for order in orders], rng.random() < 0.85))
            name = f"random-{n:05d}.mka"
            write(directory / name, [(codec, [a for a, _ in pairs], has_private) for codec, pairs, has_private in tracks])
            cases[name] = tracks
        permutations = {}
        for case, encodings in f.TIE_CASES.items():
            for i, order in enumerate(itertools.permutations(encodings)):
                name = f"tie-{case}-{i}.mka"
                (directory / name).write_bytes(f.tie_file(list(order)))
                permutations.setdefault(case, []).append(name)
        reports = analyze(cli, directory)
        differences = []
        for name, tracks in cases.items():
            got = reports.get(name, [])
            for number, (codec, pairs, has_private) in enumerate(tracks, 1):
                tracks_checked += 1
                found = ["the analyzer shows no such track"] if len(got) != len(tracks) else compare(got[number - 1], codec, has_private, [m for _, m in pairs])
                if found:
                    differences.append(f"{name} track {number} ({codec}, {'with' if has_private else 'without'} a CodecPrivate) {[a for a, _ in pairs]}: {'; '.join(found)}")
        for case, names in permutations.items():
            models = tie_model(f.TIE_CASES[case])
            first = reports.get(names[0], [None])[0]
            found = compare(first, "A_OPUS", True, models) if first else ["no report"]
            if len({repr(reports.get(n)) for n in names}) != 1:
                found.append("the permutations give different results")
            if found:
                differences.append(f"{case}: {'; '.join(found)}")
        allowed = [d for d in differences if d.split(" track ")[0].split(":")[0] in ALLOWED]
        failures = len(differences) - len(allowed)
        for d in [d for d in differences if d not in allowed][:25]:
            print(d)
    print(f"{options.cases} random files with {tracks_checked} tracks and {sum(len(n) for n in permutations.values())} permutations of {len(permutations)} tied fixtures "
          f"checked, {failures} disagreements ({len(allowed)} allowed)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
