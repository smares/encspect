#!/usr/bin/env python3
"""Checks the analyzer's reading of Matroska ContentEncodings against a model written from RFC 9559, on random combinations and on every permutation of
the fixtures whose encodings share an order.

    python3 scripts/fixtures/check_matroska_encodings.py [--cli "dotnet path/to/encspect.dll"] [--cases 2000] [--seed 1]

The model (expected, expected_codes) is a small recursive definition of these rules, not a port of the analyzer's code:

- ContentEncodingOrder must be unique (section 5.1.4.1.31.2). A track with two encodings of one order cannot be ordered, so none of its encodings is
  trusted: neither the frames nor the CodecPrivate are read, and the track is encrypted by an unknown algorithm if any encoding is of type 1, else it is
  not protected. Whatever the order of the elements in the file. (A ContentEncryption under another type must be ignored, section 5.1.4.1.31.8.)
- Otherwise the reader undoes the encodings from the highest order down. ContentEncryption counts for type 1 only (section 5.1.4.1.31.8); ContentEncAlgo 0,
  also its default when the element is there without it, states that the data are not encrypted (Table 24); only AES (5) has a cipher mode (Table 25).
- Bit 4 of the scope (Table 21) makes the next encoding's settings unreadable, so the encodings that hide one are those of the run of bit 4 encodings just
  before it, and a hidden encoding hides the next as well. A hidden encoding states nothing that can be trusted: it is encrypted when the first hider
  encrypts, or another hider is an encryption, or it is one itself; the scheme names the first hider when that encrypts, else no algorithm.
- Scope bit 1 covers the frames and bit 2 the CodecPrivate. A part is read when every encoding that covers it is unhidden and is zlib (0), header stripping
  (3) or not an encryption (the test data are not zlib data, so a zlib step fails: frame-encoding-invalid, and for the CodecPrivate codec-private-invalid,
  after which the frames are not analyzed). Opus needs its CodecPrivate; a track without one is not analyzed.
- The stream is protected by the first encryption, in that order, that covers the frames (its samples are encrypted), else the first that covers the
  CodecPrivate (they are not). An encryption that covers neither covers nothing.
- The findings the docs list for encodings (CODES) are expected per track from these rules.

Each case writes a file of one to three Opus tracks with their encodings and compares, per track, the CLI's protection, whether "Track N Frames" counted
frames (not "frames not read"), and the set of those finding codes.
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

ROOT = pathlib.Path(__file__).resolve().parents[2]
CLI_DLL = ROOT / "src" / "Encspect.Cli" / "bin" / "Release" / "net10.0" / "encspect.dll"
SCHEME = "Matroska ContentEncryption"
ALGORITHMS = {1: "DES", 2: "3DES", 3: "Twofish", 4: "Blowfish", 5: "AES"}
MODES = {1: "AES-CTR", 2: "AES-CBC"}
CODES = {f"matroska.{c}" for c in (
    "encoding-order", "encoding-hidden", "encryption-ignored", "encryption-none", "encryption-missing", "encryption-covers-nothing", "aes-settings-not-aes",
    "track-encrypted", "track-encoding-not-read", "codec-private-encrypted", "codec-private-not-read", "codec-private-invalid", "codec-private-missing",
    "frame-encoding-invalid")}


def _model(encodings):
    """The encodings sorted from the highest order, with the helpers every rule uses."""
    es = sorted(encodings, key=lambda e: -e["order"])

    def hiders(i):
        return hiders(i - 1) + [es[i - 1]] if i > 0 and es[i - 1]["scope"] & 4 else []

    def encrypts(e):
        return e["type"] == 1 and not (e["has_encryption"] and (e["encryption"] or 0) == 0)

    def name(e):
        if not e["has_encryption"]:
            return None
        if e["encryption"] == 5 and e["cipher"] is not None:
            return MODES.get(e["cipher"], f"AES, mode {e['cipher']}")
        return ALGORITHMS.get(e["encryption"] or 0, f"algorithm {e['encryption']}")

    def verdict(i):
        """(encrypted, name of the scheme) of the encoding at i."""
        h = hiders(i)
        if not h:
            return encrypts(es[i]), name(es[i])
        return encrypts(h[0]) or any(x["type"] == 1 for x in h[1:]) or es[i]["type"] == 1, name(h[0]) if encrypts(h[0]) else None

    def covering(bit):
        return [i for i in range(len(es)) if es[i]["scope"] & bit]

    def is_read(bit):
        return all(not hiders(i) and (es[i]["type"] == 1 and not encrypts(es[i]) or es[i]["type"] == 0 and (es[i]["compression"] or 0) in (0, 3))
                   for i in covering(bit))

    def is_encrypted(bit):
        return any(verdict(i)[0] for i in covering(bit))

    def has_zlib(bit):
        return any(es[i]["type"] == 0 and (es[i]["compression"] or 0) == 0 for i in covering(bit))

    return es, hiders, encrypts, verdict, covering, is_read, is_encrypted, has_zlib


def expected(encodings, has_private=True):
    """(scheme, isEncrypted) or None, and whether the frames are analyzed, for encodings given as dicts of order, scope, type, compression, has_encryption,
    encryption (the ContentEncAlgo or None), cipher and has_aes_settings."""
    if len({e["order"] for e in encodings}) < len(encodings):
        return ((SCHEME, True) if any(e["type"] == 1 for e in encodings) else None), False
    es, hiders, encrypts, verdict, covering, is_read, is_encrypted, has_zlib = _model(encodings)
    found = [(verdict(i)[1], bool(es[i]["scope"] & 1)) for i in range(len(es)) if es[i]["scope"] & 3 and verdict(i)[0]]
    chosen = next((x for x in found if x[1]), found[0] if found else None)
    protection = None if chosen is None else (SCHEME + (f", {chosen[0]}" if chosen[0] else ""), chosen[1])
    return protection, bool(is_read(1) and is_read(2) and has_private and not has_zlib(2))


def expected_codes(encodings, has_private=True):
    """The finding codes of CODES a track with these encodings has."""
    if len({e["order"] for e in encodings}) < len(encodings):
        return {"matroska.encoding-order", "matroska.track-encoding-not-read"}
    es, hiders, encrypts, verdict, covering, is_read, is_encrypted, has_zlib = _model(encodings)
    codes = set()
    for i, e in enumerate(es):
        if hiders(i):
            codes.add("matroska.encoding-hidden")
        elif e["type"] == 0 and e["has_encryption"]:
            codes.add("matroska.encryption-ignored")
        elif e["type"] == 1 and not e["has_encryption"]:
            codes.add("matroska.encryption-missing")
        elif e["type"] == 1 and (e["encryption"] or 0) == 0:
            codes.add("matroska.encryption-none")
        if not hiders(i) and encrypts(e) and not e["scope"] & 3 and not (e["scope"] & 4 and i + 1 < len(es)):
            codes.add("matroska.encryption-covers-nothing")
        if not hiders(i) and e["type"] == 1 and e["has_aes_settings"] and e["has_encryption"] and e["encryption"] != 5:
            codes.add("matroska.aes-settings-not-aes")
    codes = {c.replace("matroska.", "matroska.", 1) for c in codes}
    if not is_read(1):
        codes.add("matroska.track-encrypted" if is_encrypted(1) else "matroska.track-encoding-not-read")
        if not is_encrypted(1):
            return codes
    if not has_private:
        codes.add("matroska.codec-private-missing")
    elif not is_read(2):
        codes.add("matroska.codec-private-encrypted" if is_encrypted(2) else "matroska.codec-private-not-read")
    elif has_zlib(2):
        codes.add("matroska.codec-private-invalid")
    elif is_read(1) and has_zlib(1):
        codes.add("matroska.frame-encoding-invalid")
    return codes


def random_encoding(rng, order):
    """(content_encoding arguments, model dict) of one random encoding."""
    scope = rng.randrange(8)
    kind = rng.choice(["compression", "compression", "encryption", "encryption", "encryption", "other"])
    model = dict(order=order, scope=scope, type=0, compression=None, has_encryption=False, encryption=None, cipher=None, has_aes_settings=False)
    if kind in ("compression", "other"):
        algorithm = rng.choice([0, 1, 2, 3])
        arguments = dict(order=order, scope=scope, compression=algorithm, settings=b"\xF8" if algorithm == 3 else None)
        model["compression"] = algorithm
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
    """tracks: [(content_encoding arguments, has a CodecPrivate)] of the file's Opus tracks, numbered from 1."""
    entries, items = [], []
    for number, (encodings, has_private) in enumerate(tracks, 1):
        kw = {} if has_private else {"private": None}
        entries.append(f.opus_track(number=number, encodings=f.content_encodings(*[f.content_encoding(**kw2) for kw2 in encodings]) if encodings else None, **kw))
        items += [f.simple_block(number, i * 20, f.opus_packet(i), key=True) for i in range(4)]
    path.write_bytes(f.build(f.standard(f.cluster(0, *items), track=entries)))


def analyze(cli, directory):
    """{file name: [(protection, frames analyzed, finding codes of CODES) per track]} of the CLI's reports on a directory."""
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
        codes = {}
        for finding in report["findings"]:
            if finding["code"] in CODES and (m := re.search(r"Track (\d+)", finding["message"])):
                codes.setdefault(int(m.group(1)), set()).add(finding["code"])
        tracks = []
        for number, stream in enumerate(report["streams"], 1):
            protection = stream.get("protection")
            tracks.append(((protection["scheme"], protection["isEncrypted"]) if protection else None, number in counted, codes.get(number, set())))
        reports[pathlib.Path(report["source"]).name] = tracks
    return reports


def tie_model(encodings):
    """The model dicts of a tied fixture's (content_encoding arguments, type) pairs."""
    return [dict(order=kw["order"], scope=kw["scope"], type=kind, compression=kw.get("compression"),
                 has_encryption="encryption" in kw or "no_algo" in kw or "stray_encryption" in kw, encryption=kw.get("encryption", kw.get("stray_encryption")),
                 cipher=kw.get("cipher"), has_aes_settings="cipher" in kw) for kw, kind in encodings]


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
                tracks.append(([random_encoding(rng, order) for order in orders], rng.random() < 0.85))
            name = f"random-{n:05d}.mka"
            write(directory / name, [([a for a, _ in pairs], has_private) for pairs, has_private in tracks])
            cases[name] = [(pairs, has_private) for pairs, has_private in tracks]
        permutations = {}
        for case, encodings in f.TIE_CASES.items():
            for i, order in enumerate(itertools.permutations(encodings)):
                name = f"tie-{case}-{i}.mka"
                (directory / name).write_bytes(f.tie_file(list(order)))
                permutations.setdefault(case, []).append(name)
        reports = analyze(cli, directory)
        for name, tracks in cases.items():
            got = reports.get(name, [])
            for number, (pairs, has_private) in enumerate(tracks, 1):
                models = [m for _, m in pairs]
                want = (*expected(models, has_private), expected_codes(models, has_private)) if models else (None, has_private, {"matroska.codec-private-missing"} if not has_private else set())
                tracks_checked += 1
                if len(got) != len(tracks) or got[number - 1] != want:
                    failures += 1
                    if failures <= 20:
                        print(f"{name} track {number}: the analyzer gives {got[number - 1] if len(got) >= number else None}, the model {want} for {[a for a, _ in pairs]}, "
                              f"{'with' if has_private else 'without'} a CodecPrivate")
        for case, names in permutations.items():
            results = {repr(reports.get(n)) for n in names}
            models = tie_model(f.TIE_CASES[case])
            want = [(*expected(models), expected_codes(models))]
            if len(results) != 1 or reports.get(names[0]) != want:
                failures += 1
                print(f"{case}: the {len(names)} permutations give {len(results)} results; the first {reports.get(names[0])}, the model {want}")
    print(f"{options.cases} random files with {tracks_checked} tracks and {sum(len(n) for n in permutations.values())} permutations of {len(permutations)} tied fixtures "
          f"checked, {failures} disagreements")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
