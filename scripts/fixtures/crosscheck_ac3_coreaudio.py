#!/usr/bin/env python3
"""Compares the PCM that Encspect's AC-3 decoder delivers with Core Audio's, on macOS (docs/development.md, "AC-3 decoding"; rollout step 5 of
docs/design/official-decoder-crosschecks.md), and decodes the damage variants of derive_variants.py ac3 with it.

    crosscheck_ac3_coreaudio.py --probe BUILD/audiotoolbox_probe [--pcmdump CMD] [--encspect CMD] [--keep DIR] [FILE ...]

The probe's `pcm` mode reads a file with ExtAudioFile as interleaved floats. As crosscheck_ac3_pcm.py does, each encoder-written file is compared twice with
compare_pcm.py: sample by sample on a copy with its dither flags cleared (the design's bounds), and, if that fails, by the energy of bands on the file repeated
(whatever Core Audio's dither is, bands of noise are compared by energy). What the documents do not fix and the first runs record: Core Audio's channel order
for 5.1 and whether it drops the first 288 samples (the converter outputs 288 fewer than the coded stage, "Reference for sample counts" in docs/formats/ac3.md),
so each attempt is made for the candidate orders and first positions in CANDIDATES, and the first that passes counts; the output lists every attempt, so a
failing file shows what was tried and by how much each missed. The variants (cut, flipped, dropped, repeated frames) are only decoded and their frame counts
listed beside the analyzer's: Core Audio conceals damage, the analyzer does not, so no bound applies (the lead's rule of 2026-11-01).

Exit status 0 where every file passes, 1 where one fails, 2 for a usage error.
"""

import argparse
import array
import json
import pathlib
import shlex
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import crosscheck_ac3_pcm as x  # noqa: E402
import derive_variants  # noqa: E402

# (name, channel order of the reference's output in the coded order: reference channel i is coded channel ORDER[i] ... as indexes into the reference's channels
# for each coded channel, first position of the reference's samples at the coded stage). Core Audio's 5.1 is L R C LFE Ls Rs like FFmpeg's, or the coded order.
ORDERS = {
    1: {"coded": [0]},
    2: {"coded": [0, 1]},
    3: {"coded": [0, 1, 2]},
    4: {"coded": [0, 1, 2, 3]},
    5: {"coded": [0, 1, 2, 3, 4], "smpte": [0, 2, 1, 3, 4]},
    6: {"coded": [0, 1, 2, 3, 4, 5], "smpte": [0, 2, 1, 4, 5, 3]},
}
FIRST_POSITIONS = (288, 0)


def candidates(channels):
    """[(label, order, first position)] to try: every order known for the channel count at every first position."""
    return [(f"{name} order, first position {first}", order, first) for name, order in ORDERS.get(channels, {"coded": list(range(channels))}).items() for first in FIRST_POSITIONS]


def reorder(raw, channels, order):
    """The interleaved floats with the channels moved so that output channel c is the reference's channel order[c]."""
    values = array.array("f")
    values.frombytes(raw)
    out = array.array("f")
    for n in range(len(values) // channels):
        for c in order:
            out.append(values[n * channels + c])
    return out.tobytes()


def first_passing(attempts):
    """attempts: [(label, problems)] in order. Returns (label of the first with no problems or None, the report lines of all)."""
    lines = []
    for label, problems in attempts:
        lines.append(f"    {label}: {'passes' if not problems else '; '.join(problems)}")
        if not problems:
            return label, lines
    return None, lines


def decode(probe, path, out):
    run = subprocess.run([probe, "pcm", str(path), str(out)], capture_output=True, text=True)
    line = next((l for l in run.stdout.splitlines() if l.startswith("{")), "{}")
    info = json.loads(line)
    if run.returncode != 0 or info.get("status") != 0:
        return None, info
    return out.read_bytes(), info


def attempt(dump, raw, channels, work, tag, extra):
    results = []
    for label, order, first in candidates(channels):
        reference = reorder(raw, channels, order)
        problems, _ = compare(dump, reference, channels, f"{tag}-{len(results)}", first, extra)
        results.append((label, problems))
        if not problems:
            break
    return results


def compare(dump, reference, channels, label, first, extra):
    raw = dump.parent / f"{label}.f32"
    raw.write_bytes(reference)
    out = subprocess.run([sys.executable, str(HERE / "compare_pcm.py"), str(dump), "--reference", str(raw), "--reference-format", "float32", "--reference-channels", str(channels),
                          "--reference-first-position", str(first), "--json", *extra], capture_output=True, text=True)
    if out.returncode == 2:
        return [out.stderr.strip()], None
    result = json.loads(out.stdout)
    if out.returncode == 0:
        return [], result
    if "--no-sample-bounds" in extra:
        return ["a band outside the tolerance, or none comparable"], result
    worst = max(result["channels"], key=lambda c: c["max"])
    return [f"largest {worst['max']:.3g}, RMS {worst['rms']:.3g}"], result


def check_file(args, path, work):
    manifest = json.loads((x.FIXTURES / (path.stem + ".expected.json")).read_text(encoding="utf-8"))
    frames = x.manifest_frames(manifest)
    stripped = work / (path.stem + ".nodither.ac3")
    x.clear_dither(args.encspect, path, frames, stripped)
    lines = []
    long_data, long_frames = x.repeated(path.read_bytes(), frames, max(2, x.REPEATED_SAMPLES // (6 * 256 * len(frames))))
    long_path = work / (path.stem + ".repeated.ac3")
    long_path.write_bytes(long_data)
    for variant, source, extra in (("strict", stripped, []), ("dithered", long_path, ["--bands", x.BANDS, "--no-sample-bounds"])):
        folder = work / variant
        folder.mkdir()
        dump = x.pcmdump(args.pcmdump, source, folder)
        raw, info = decode(args.probe, source, folder / "coreaudio.f32")
        if raw is None:
            lines.append(f"  {variant}: Core Audio does not decode the file: {info}")
            continue
        channels = info["channels"]
        label, detail = first_passing(attempt(dump, raw, channels, work, f"ca-{variant}", extra))
        lines.append(f"  {variant}: {'ok, ' + label if label else 'OUTSIDE THE BOUND, attempts:'} ({info['frames']} frames, layout tag {info.get('layoutTag')})")
        if label is None:
            lines += detail
        else:
            return True, lines
    return False, lines


def variants_report(args, root):
    out = root / "variants"
    entries = derive_variants.write_ac3(out)
    lines = []
    for entry in entries:
        path = out / entry["name"]
        raw, info = decode(args.probe, path, root / "variant.f32")
        lines.append(f"  {entry['name']}: " + (f"{info['frames']} frames" if raw is not None else f"not decoded ({info})"))
    return lines


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("files", nargs="*", type=pathlib.Path)
    parser.add_argument("--probe", required=True)
    parser.add_argument("--pcmdump", default=x.DEFAULT_PCMDUMP)
    parser.add_argument("--encspect", default=x.DEFAULT_ENCSPECT)
    parser.add_argument("--keep", type=pathlib.Path)
    args = parser.parse_args(argv)
    files = args.files or [p for p in x.default_files() if p.name.startswith("external-")]
    failures = []
    with tempfile.TemporaryDirectory() as temporary:
        root = args.keep or pathlib.Path(temporary)
        root.mkdir(parents=True, exist_ok=True)
        for path in files:
            work = root / path.stem
            work.mkdir(exist_ok=True)
            ok, lines = check_file(args, path, work)
            print(f"{path.name}: {'ok' if ok else 'FAILS'}")
            print("\n".join(lines))
            if not ok:
                failures.append(path.name)
        print("damage variants decoded by Core Audio (frame counts, no bound):")
        print("\n".join(variants_report(args, root)))
    print(f"{len(files)} files, {len(failures)} failing: {', '.join(failures) if failures else 'none'}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
