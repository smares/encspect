#!/usr/bin/env python3
"""Checks the analyzer's reading of Matroska ContentEncodings against a model written from RFC 9559, on random combinations and on every permutation of
the fixtures whose encodings share an order.

    python3 scripts/fixtures/check_matroska_encodings.py [--cli "dotnet path/to/encspect.dll"] [--cases 2000] [--seed 1]

The model (expected) is a small recursive definition of these rules, not a port of the analyzer's code:

- ContentEncodingOrder must be unique (section 5.1.4.1.31.2). A track with two encodings of one order cannot be ordered, so none of its encodings is
  trusted: neither the frames nor the CodecPrivate are read, and the track is encrypted by an unknown algorithm if any encoding is of type 1 or holds
  a ContentEncryption, else it is not protected. Whatever the order of the elements in the file.
- Otherwise the reader undoes the encodings from the highest order down. ContentEncryption counts for type 1 only (section 5.1.4.1.31.8); ContentEncAlgo 0,
  also its default when the element is there without it, states that the data are not encrypted (Table 24); only AES (5) has a cipher mode (Table 25).
- Bit 4 of the scope (Table 21) makes the next encoding's settings unreadable, so the encodings that hide one are those of the run of bit 4 encodings just
  before it, and a hidden encoding hides the next as well. A hidden encoding states nothing that can be trusted: it is encrypted when the first hider
  encrypts, or another hider is an encryption, or it is one itself; the scheme names the first hider when that encrypts, else no algorithm.
- Scope bit 1 covers the frames and bit 2 the CodecPrivate. A part is read when every encoding that covers it is unhidden and is header stripping (3) or
  not an encryption; the track's frames are analyzed when both parts are read.
- The stream is protected by the first encryption, in that order, that covers the frames (its samples are encrypted), else the first that covers the
  CodecPrivate (they are not). An encryption that covers neither covers nothing.

Each case writes an Opus track with the encodings and compares the CLI's protection and whether "Track 1 Frames" counted frames (not "frames not read").
"""

import argparse
import itertools
import json
import pathlib
import random
import subprocess
import sys
import tempfile

import make_matroska_fixtures as f

ROOT = pathlib.Path(__file__).resolve().parents[2]
SCHEME = "Matroska ContentEncryption"
ALGORITHMS = {1: "DES", 2: "3DES", 3: "Twofish", 4: "Blowfish", 5: "AES"}
MODES = {1: "AES-CTR", 2: "AES-CBC"}


def expected(encodings):
    """(scheme, isEncrypted) or None, and whether the frames are analyzed, for encodings given as dicts of order, scope, type, compression, has_encryption,
    encryption (the ContentEncAlgo or None) and cipher."""
    if len({e["order"] for e in encodings}) < len(encodings):
        return ((SCHEME, True) if any(e["type"] == 1 or e["has_encryption"] for e in encodings) else None), False
    es = sorted(encodings, key=lambda e: -e["order"])

    def hiders(i):
        return hiders(i - 1) + [es[i - 1]] if i > 0 and es[i - 1]["scope"] & 4 else []

    def encrypts(e):
        return e["type"] == 1 and not (e["has_encryption"] and (e["encryption"] or 0) == 0)

    def name(e):
        if not e["has_encryption"]:
            return None
        return MODES.get(e["cipher"], "AES") if e["encryption"] == 5 and e["cipher"] is not None else ALGORITHMS.get(e["encryption"] or 0)

    def verdict(i):
        """(encrypted, name of the scheme) of the encoding at i."""
        h = hiders(i)
        if not h:
            return encrypts(es[i]), name(es[i])
        return encrypts(h[0]) or any(x["type"] == 1 for x in h[1:]) or es[i]["type"] == 1, name(h[0]) if encrypts(h[0]) else None

    def read(bit):
        return all(not hiders(i) and (es[i]["type"] == 1 and not encrypts(es[i]) or es[i]["type"] == 0 and (es[i]["compression"] or 0) == 3)
                   for i in range(len(es)) if es[i]["scope"] & bit)

    found = [(verdict(i)[1], bool(es[i]["scope"] & 1)) for i in range(len(es)) if es[i]["scope"] & 3 and verdict(i)[0]]
    chosen = next((x for x in found if x[1]), found[0] if found else None)
    protection = None if chosen is None else (SCHEME + (f", {chosen[0]}" if chosen[0] else ""), chosen[1])
    return protection, read(1) and read(2)


def random_case(rng, order):
    """(content_encoding arguments, model dict) of one encoding."""
    scope, kind = rng.randrange(8), rng.choice([0, 1, 1])
    model = dict(order=order, scope=scope, type=kind, compression=None, has_encryption=False, encryption=None, cipher=None)
    if kind == 0:
        model["compression"] = rng.choice([3, 1])
        arguments = dict(order=order, scope=scope, compression=model["compression"], settings=b"\xF8" if model["compression"] == 3 else None)
        if rng.random() < 0.2:
            arguments["stray_encryption"] = rng.choice([0, 5])
            model["has_encryption"], model["encryption"] = True, arguments["stray_encryption"]
        return arguments, model
    variant = rng.choice(["algorithm", "algorithm", "algorithm", "no-algorithm", "missing"])
    if variant == "missing":
        return dict(order=order, scope=scope, missing_encryption=True), model
    model["has_encryption"] = True
    if variant == "no-algorithm":
        return dict(order=order, scope=scope, no_algo=True, key_id=b"\x01"), model
    model["encryption"] = rng.choice([0, 1, 5, 5])
    model["cipher"] = rng.choice([None, 1, 2])
    return dict(order=order, scope=scope, encryption=model["encryption"], cipher=model["cipher"], key_id=b"\x01"), model


def write(path, encodings):
    sealed = [f.simple_block(1, i * 20, f.opus_packet(i), key=True) for i in range(4)]
    enc = f.content_encodings(*[f.content_encoding(**kw) for kw in encodings])
    path.write_bytes(f.build(f.standard(f.cluster(0, *sealed), track=[f.opus_track(encodings=enc)])))


def analyze(cli, directory):
    """{file name: (protection, frames analyzed, sorted finding codes)} of the CLI's reports on a directory."""
    result = subprocess.run([*cli, "--ndjson", str(directory)], capture_output=True, text=True, timeout=1200)
    reports = {}
    for line in result.stdout.splitlines():
        report = json.loads(line)
        stack, counted = [report["root"]], False
        while stack:
            node = stack.pop()
            stack.extend(node.get("children", []))
            counted = counted or (node["name"] == "Track 1 Frames" and isinstance(node["value"], int))
        stream = report["streams"][0] if report["streams"] else {}
        protection = stream.get("protection")
        reports[pathlib.Path(report["source"]).name] = ((protection["scheme"], protection["isEncrypted"]) if protection else None, counted,
                                                         sorted(x["code"] for x in report["findings"]))
    return reports


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--cli", default=f"dotnet {ROOT / 'src/Encspect.Cli/bin/Release/net10.0/encspect.dll'}")
    parser.add_argument("--cases", type=int, default=2000)
    parser.add_argument("--seed", type=int, default=1)
    options = parser.parse_args()
    cli = options.cli.split()
    rng = random.Random(options.seed)
    failures = 0
    with tempfile.TemporaryDirectory() as temp:
        directory = pathlib.Path(temp)
        cases = {}
        for n in range(options.cases):
            count = rng.randint(1, 4)
            # Orders with gaps; one case in five lets two share an order.
            orders = rng.sample(range(7), count) if rng.random() < 0.8 else [rng.randrange(3) for _ in range(count)]
            pairs = [random_case(rng, order) for order in orders]
            name = f"random-{n:05d}.mka"
            write(directory / name, [a for a, _ in pairs])
            cases[name] = (pairs, expected([m for _, m in pairs]))
        permutations = {}
        for case, encodings in f.TIE_CASES.items():
            for i, order in enumerate(itertools.permutations(encodings)):
                name = f"tie-{case}-{i}.mka"
                (directory / name).write_bytes(f.tie_file(list(order)))
                permutations.setdefault(case, []).append(name)
        reports = analyze(cli, directory)
        for name, (pairs, (protection, analyzed)) in cases.items():
            got = reports.get(name)
            if got is None or got[0] != protection or got[1] != analyzed:
                failures += 1
                if failures <= 20:
                    print(f"{name}: the analyzer gives {got and got[:2]}, the model {(protection, analyzed)} for {[a for a, _ in pairs]}")
        for case, names in permutations.items():
            results = {tuple(map(str, reports.get(n, ()))) for n in names}
            model = expected([dict(order=kw["order"], scope=kw["scope"], type=1 if "encryption" in kw else 0, compression=kw.get("compression"),
                                   has_encryption="encryption" in kw, encryption=kw.get("encryption"), cipher=kw.get("cipher")) for kw in f.TIE_CASES[case]])
            first = reports.get(names[0])
            if len(results) != 1 or first is None or first[:2] != model:
                failures += 1
                print(f"{case}: the {len(names)} permutations give {len(results)} results; the first {first and first[:2]}, the model {model}")
    print(f"{options.cases} random encodings and {sum(len(n) for n in permutations.values())} permutations of {len(permutations)} tied fixtures checked, {failures} disagreements")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
