#!/usr/bin/env python3
"""Writes the WavPack fixtures the original encoders wrote: the files of versions 1 to 3 in tests/fixtures/wavpack/legacy (external-wv<release>-*.wv, and *.wvc
for the hybrid ones that have a correction file) and the files of the DOS build of 4.31, which are version 4 (stream version 0x404), in tests/fixtures/wavpack
(external-wv431-*.wv), as docs/design/wavpack-legacy.md, "Fixtures", lists them.

The encoders are the programs ReallyRareWares keeps (wavpack_legacy_tool.py pins them): 1.0, 2.0, 3.0, 3.2 and 3.4 are real-mode DOS programs, 4.31 is a DOS/32A
program, and DOSBox runs them. The release 3.97 is a Win32 program, run directly on Windows (through Wine elsewhere). The inputs are deterministic signals written by wavpack_signal.py (WAV files whose fmt chunk is the plain PCM one the old encoders
take), the lossyWAV-processed WAV file of tests/fixtures/wav and raw files cut from the same signal.

Each file is decoded by the release's own WVUNPACK in the same run, and the manifest records what it wrote (the MD5 of the PCM, against the encoder's input):
a lossless file must give the input back, a hybrid file with a correction file is decoded both with it and without it. The files are committed, so --manifests-only
recomputes the manifests from them without DOSBox: the model (wavpack_legacy_model.py for versions 1 to 3, wavpack_model.py for version 4) reads the file, and the
encoder, its settings and the original decoder's answer come from the manifest already there. crosscheck_wavpack_legacy_fixtures.py confirms them with libwavpack's
legacy decoder.

    make_external_wavpack_legacy_fixtures.py [--manifests-only] [--release=RELEASE] [name ...]

--release=3.97 writes the files of that release only (the others' manifests are recomputed from the committed files).
"""

import hashlib
import json
import pathlib
import struct
import sys

import make_external_wavpack_fixtures as ext
import wavpack_legacy_model as legacy_model
import wavpack_legacy_tool as tool
import wavpack_signal as sig

ROOT = pathlib.Path(__file__).resolve().parents[2]
LEGACY = ROOT / "tests" / "fixtures" / "wavpack" / "legacy"
VERSION4 = ROOT / "tests" / "fixtures" / "wavpack"
LOSSYWAV = ROOT / "tests" / "fixtures" / "wav" / "lossywav-standard.wav"
SHORT = 24000
# 3.4 reads no WAV file whose 3-byte samples are more than 16,383 bytes of data (it stops with "couldn't read all samples, file may be corrupt!!"; 2,730
# stereo frames pass, 2,731 do not), so the files of more than 16 bits are of that length.
SHORT_3 = 2700
LIST_CHUNK = sig.chunk(b"LIST", b"INFOISFT" + (12).to_bytes(4, "little") + b"Test tool\0\0\0")
TAIL_CHUNK = sig.chunk(b"id3 ", b"TAG" + bytes(range(100, 125)))

# Release, in the order the design lists them: the name of the program as the files record it.
NAMES = {"1.0": "wv100", "2.0": "wv200", "3.0": "wv300", "3.2": "wv320", "3.4": "wv340", "3.97": "wv397", "4.31": "wv431"}


def fixture(release, topic, spec, arguments="", files=("wv",)):
    # "-" as the arguments stands for the encoder reading its input from the standard input, "<y" for the answer yes to its question (1.0 and 2.0 have no -y):
    # the encoder reads it from a file on its standard input.
    stdin, answers = arguments == "-", arguments == "<y"
    return (f"{NAMES[release]}-{topic}", {"release": release, "input": spec, "arguments": "" if stdin or answers else arguments, "files": files, "stdin": stdin, "answers": answers})


FIXTURES = dict([
    fixture("1.0", "stereo-16", ("wav", 44100, 2, 16, SHORT)),
    fixture("1.0", "mono-16", ("wav", 44100, 1, 16, SHORT)),
    fixture("1.0", "chunks", ("wav", 44100, 2, 16, SHORT, LIST_CHUNK, TAIL_CHUNK), "<y"),
    fixture("2.0", "stereo-16", ("wav", 44100, 2, 16, SHORT)),
    fixture("2.0", "mono-16", ("wav", 44100, 1, 16, SHORT)),
    fixture("2.0", "chunks", ("wav", 44100, 2, 16, SHORT, LIST_CHUNK, TAIL_CHUNK), "<y"),
    fixture("2.0", "lossy-stereo-b4", ("wav", 44100, 2, 16, SHORT), "-b4"),
    fixture("2.0", "lossy-stereo-b8", ("wav", 44100, 2, 16, SHORT), "-b8"),
    fixture("2.0", "lossy-stereo-b15", ("wav", 44100, 2, 16, SHORT), "-b15"),
    fixture("2.0", "lossy-mono-b6", ("wav", 44100, 1, 16, SHORT), "-b6"),
    *[fixture(r, t, s, a) for r in ("3.0", "3.2") for t, s, a in [
        ("stereo-16", ("wav", 44100, 2, 16, SHORT), ""),
        ("fast", ("wav", 44100, 2, 16, SHORT), "-f"),
        ("mono-16", ("wav", 44100, 1, 16, SHORT), ""),
        ("lossy-b6", ("wav", 44100, 2, 16, SHORT), "-b6"),
        ("lossy-b6-fast", ("wav", 44100, 2, 16, SHORT), "-b6 -f"),
        ("lossy-mono-b6", ("wav", 44100, 1, 16, SHORT), "-b6"),
        ("raw-stereo", ("raw", 2, SHORT, 0), "-r2"),
        ("raw-mono", ("raw", 1, SHORT, 0), "-r1"),
        ("chunks", ("wav", 44100, 2, 16, SHORT, LIST_CHUNK, TAIL_CHUNK), ""),
    ]],
    *[fixture(r, t, s, a, f) for r in ("3.4", "3.97") for t, s, a, f in [
        ("lossless-16-stereo", ("wav", 44100, 2, 16, SHORT), "", ("wv",)),
        ("fast", ("wav", 44100, 2, 16, SHORT), "-f", ("wv",)),
        ("very-fast", ("wav", 44100, 2, 16, SHORT), "-ff", ("wv",)),
        ("high", ("wav", 44100, 2, 16, SHORT), "-h", ("wv",)),
        ("joint-stereo-off", ("wav", 44100, 2, 16, SHORT), "-j0", ("wv",)),
        ("joint-stereo-on", ("wav", 44100, 2, 16, SHORT), "-j1", ("wv",)),
        ("lossless-16-mono", ("wav", 44100, 1, 16, SHORT), "", ("wv",)),
        ("lossless-18-stereo", ("wav", 44100, 2, 18, SHORT_3), "", ("wv",)),
        ("lossless-20-stereo", ("wav", 44100, 2, 20, SHORT_3), "", ("wv",)),
        ("lossless-24-stereo", ("wav", 44100, 2, 24, SHORT_3), "", ("wv",)),
        ("hybrid-320", ("wav", 44100, 2, 16, SHORT), "-b320", ("wv",)),
        ("hybrid-320-correction", ("wav", 44100, 2, 16, SHORT), "-b320 -c", ("wv", "wvc")),
        ("hybrid-320-high", ("wav", 44100, 2, 16, SHORT), "-b320 -h", ("wv",)),
        ("hybrid-320-fast", ("wav", 44100, 2, 16, SHORT), "-b320 -f", ("wv",)),
        ("hybrid-320-very-fast", ("wav", 44100, 2, 16, SHORT), "-b320 -ff", ("wv",)),
        ("hybrid-320-joint-stereo", ("wav", 44100, 2, 16, SHORT), "-b320 -j1", ("wv",)),
        ("hybrid-320-left-right", ("wav", 44100, 2, 16, SHORT), "-b320 -j0", ("wv",)),
        ("hybrid-320-shaping", ("wav", 44100, 2, 16, SHORT), "-b320 -s1", ("wv",)),
        ("hybrid-320-no-shaping-96k", ("wav", 96000, 2, 16, SHORT), "-b320 -s0", ("wv",)),
        ("hybrid-320-96k", ("wav", 96000, 2, 16, SHORT), "-b320", ("wv",)),
        ("hybrid-6", ("wav", 44100, 2, 16, SHORT), "-b6", ("wv",)),
        ("hybrid-6-correction", ("wav", 44100, 2, 16, SHORT), "-b6 -c", ("wv", "wvc")),
        ("hybrid-6-mono", ("wav", 44100, 1, 16, SHORT), "-b6", ("wv",)),
        ("hybrid-320-mono", ("wav", 44100, 1, 16, SHORT), "-b320", ("wv",)),
        ("hybrid-320-24", ("wav", 44100, 2, 24, SHORT_3), "-b320", ("wv",)),
        ("hybrid-320-24-correction", ("wav", 44100, 2, 24, SHORT_3), "-b320 -c", ("wv", "wvc")),
        ("raw-stereo", ("raw", 2, SHORT, 0), "-r2", ("wv",)),
        ("raw-stereo-extra-1", ("raw", 2, SHORT, 1), "-r2", ("wv",)),
        ("raw-stereo-extra-2", ("raw", 2, SHORT, 2), "-r2", ("wv",)),
        ("raw-stereo-extra-3", ("raw", 2, SHORT, 3), "-r2", ("wv",)),
        ("raw-mono", ("raw", 1, SHORT, 0), "-r1", ("wv",)),
        ("chunks", ("wav", 44100, 2, 16, SHORT, LIST_CHUNK, TAIL_CHUNK), "", ("wv",)),
        ("trailer-only", ("wav", 44100, 2, 16, SHORT, b"", TAIL_CHUNK), "", ("wv",)),
        ("lossywav", ("file", LOSSYWAV), "", ("wv",)),
        ("standard-input", ("wav", 44100, 2, 16, SHORT), "-", ("wv",)),
    ]],
    # Pairs of the paths the first pairs do not take (the correction added in mono, after the joint-stereo step and with the noise shaping of the lossy coding): 3.4 only,
    # since 3.97's files are 3.4's byte for byte.
    *[fixture("3.4", t, s, a, ("wv", "wvc")) for t, s, a in [
        ("hybrid-320-mono-correction", ("wav", 44100, 1, 16, SHORT), "-b320 -c"),
        ("hybrid-320-joint-stereo-correction", ("wav", 44100, 2, 16, SHORT), "-b320 -j1 -c"),
        ("hybrid-320-shaping-correction", ("wav", 44100, 2, 16, SHORT), "-b320 -s1 -c"),
        ("hybrid-320-96k-correction", ("wav", 96000, 2, 16, SHORT), "-b320 -c"),
    ]],
    # What 3.97's documentation adds to 3.4's options: the raw and hybrid modes together ("With version 3.96 the raw and hybrid modes are usable together
    # (with a warning)"), which -y answers. 3.4's limit on 3-byte samples is a question of the DOS build, so one 24-bit file has the full length.
    *[fixture("3.97", t, s, a, f) for t, s, a, f in [
        ("raw-stereo-hybrid-320", ("raw", 2, SHORT, 0), "-r2 -b320", ("wv",)),
        ("raw-stereo-hybrid-320-correction", ("raw", 2, SHORT, 0), "-r2 -b320 -c", ("wv", "wvc")),
        ("raw-mono-hybrid-320", ("raw", 1, SHORT, 0), "-r1 -b320", ("wv",)),
        ("lossless-24-stereo-long", ("wav", 44100, 2, 24, SHORT), "", ("wv",)),
    ]],
    *[fixture("4.31", t, s, a, f) for t, s, a, f in [
        ("lossless-16-stereo", ("wav", 44100, 2, 16, SHORT), "", ("wv",)),
        ("mode-fast", ("wav", 44100, 2, 16, SHORT), "-f", ("wv",)),
        ("mode-high", ("wav", 44100, 2, 16, SHORT), "-h", ("wv",)),
        ("mode-extra-1", ("wav", 44100, 2, 16, SHORT), "-x", ("wv",)),
        ("hybrid-320-correction", ("wav", 44100, 2, 16, SHORT), "-b320 -c", ("wv", "wvc")),
        ("lossless-16-stereo-md5", ("wav", 44100, 2, 16, SHORT), "-m", ("wv",)),
        ("lossless-24-stereo", ("wav", 44100, 2, 24, SHORT), "", ("wv",)),
    ]],
])


def plain_wav(rate, channels, bits, count, before=b"", after=b""):
    """A WAV file with the plain 16-byte PCM fmt chunk the encoders before 4.0 take, samples of more than 16 bits left-aligned in 3 bytes."""
    width = (bits + 7) // 8
    body = bytearray()
    for frame in sig.samples(rate, channels, count, 1 << (bits - 1)):
        for value in frame:
            body += (value << (width * 8 - bits)).to_bytes(width, "little", signed=True)
    align = channels * width
    fmt = struct.pack("<HHIIHH", 1, channels, rate, rate * align, align, width * 8 if bits <= 16 else bits)
    chunks = b"fmt " + struct.pack("<I", len(fmt)) + fmt + before + b"data" + struct.pack("<I", len(body)) + bytes(body) + after
    return b"RIFF" + struct.pack("<I", 4 + len(chunks)) + b"WAVE" + chunks


def make_input(spec):
    """(file name inside DOSBox, bytes, PCM bytes the encoder takes in, description)."""
    kind, *p = spec
    if kind == "wav":
        data = plain_wav(*p)
        description = {"kind": "wav", "rate": p[0], "channels": p[1], "bits": p[2], "samples": p[3], "chunks_before_data": len(p) > 4 and len(p[4]) > 0, "chunks_after_data": len(p) > 5 and len(p[5]) > 0}
    elif kind == "file":
        data = p[0].read_bytes()
        description = {"kind": "wav", "file": str(p[0].relative_to(ROOT)).replace("\\", "/")}
    else:
        channels, count, extra = p
        pcm = sig.pcm(44100, channels, count, 16) + bytes([1, 2, 3][:extra])
        return "IN.RAW", pcm, pcm, {"kind": "raw", "channels": channels, "bits": 16, "samples": count, "extra_bytes": extra}
    return "IN.WAV", data, wav_pcm(data), description


def wav_pcm(data):
    """The payload of the data chunk."""
    p = 12
    while p + 8 <= len(data):
        size = struct.unpack_from("<I", data, p + 4)[0]
        if data[p:p + 4] == b"data":
            return data[p + 8:p + 8 + size]
        p += 8 + size + (size & 1)
    raise ValueError("no data chunk")


def md5(data):
    return hashlib.md5(data).hexdigest()


def dos_commands(release, name, spec, arguments, files, stdin, answers, source_name):
    """The commands of one DOSBox run: the encoder, then the release's decoder over its output."""
    old = release in ("1.0", "2.0")
    yes = "" if old else "-y "
    if release == "4.31":
        encode = f"WAVPACK -y -q {arguments} {source_name} -o OUT.WV".replace("  ", " ")
        decode = "WVUNPACK -y -q OUT.WV -o BACK.WAV"
    else:
        source = "-" if stdin else source_name
        encode = f"WAVPACK {yes}{arguments} {source} OUT.WV".replace("  ", " ")
        if stdin:
            encode += f" < {source_name}"
        if answers:
            encode += " < Y.TXT"
        decode = f"WVUNPACK {yes}OUT.WV BACK.{'BIN' if spec[0] == 'raw' else 'WAV'}".replace("  ", " ")
    return encode, decode


def banner(release):
    try:
        return tool.banner(release)
    except Exception:
        return ""


def encode(name, entry, work):
    release, spec = entry["release"], entry["input"]
    source_name, data, pcm, description = make_input(spec)
    encode_command, decode_command = dos_commands(release, name, spec, entry["arguments"], entry["files"], entry["stdin"], entry["answers"], source_name)
    redirect = release != "3.0"
    texts, found = tool.run(release, {source_name: data, **({"Y.TXT": b"y\r\ny\r\n"} if entry["answers"] else {})}, [encode_command, decode_command], redirect=redirect)
    if "OUT.WV" not in found:
        raise SystemExit(f"{release}: {encode_command} wrote no file for {name}")
    produced = {"wv": found["OUT.WV"]}
    if "wvc" in entry["files"]:
        if "OUT.WVC" not in found:
            raise SystemExit(f"{release}: {encode_command} wrote no correction file for {name}")
        produced["wvc"] = found["OUT.WVC"]
    back = found.get("BACK.BIN" if spec[0] == "raw" else "BACK.WAV")
    if back is None:
        raise SystemExit(f"{release}: {decode_command} wrote no file for {name}")
    decoded = back if spec[0] == "raw" else wav_pcm(back)
    original = {"tool": decoder_name(release), "pcm_md5": md5(decoded), "pcm_bytes": len(decoded), "matches_input": decoded == pcm}
    if spec[0] != "raw":
        # Whether the whole WAV file comes back: the chunks before the data always, those after it only where the encoder kept them.
        original["file_matches_input"] = back == data
    if "wvc" in produced:
        # The same decoder with the main file alone is lossy where the hybrid file discarded bits.
        _, alone = tool.run(release, {"OUT.WV": produced["wv"]}, [decode_command], redirect=redirect)
        lossy = alone["BACK.BIN"] if spec[0] == "raw" else wav_pcm(alone["BACK.WAV"])
        original = {"tool": original["tool"], "with_correction": {k: v for k, v in original.items() if k != "tool"},
                    "without_correction": {"pcm_md5": md5(lossy), "pcm_bytes": len(lossy), "matches_input": lossy == pcm}}
    encoder = {"tool": encoder_name(release), "arguments": entry["arguments"].split(), "input": dict(description, pcm_md5=md5(pcm), file_md5=md5(data))}
    return produced, encoder, original


def encoder_name(release):
    if release in tool.WIN32:
        return f"WavPack {release} for Win32 (ReallyRareWares: {tool.RELEASES[release][0]}), run on Windows 10"
    return f"WavPack {release} for DOS (ReallyRareWares: {tool.RELEASES[release][0]}), run under DOSBox 0.74-3"


def decoder_name(release):
    if release in tool.WIN32:
        return f"wvunpack {release} for Win32, run on Windows 10"
    return f"WVUNPACK {release} for DOS, run under DOSBox 0.74-3"


def path_for(name, kind):
    folder = VERSION4 if name.startswith("wv431-") else LEGACY
    return folder / (f"external-{name}.wv" if kind == "wv" else f"external-{name}-wvc.wvc")


def manifest_path(path):
    return path.with_name(path.name.rsplit(".", 1)[0] + ".expected.json")


def build_manifest(name, kind, path, encoder, original):
    data = path.read_bytes()
    note = dict(encoder, file="main file" if kind == "wv" else "correction file written next to " + path_for(name, "wv").name)
    if name.startswith("wv431-"):
        findings = [("wavpack.correction-file", "correction data only")] if kind == "wvc" else []
        # A version 4 main file with its correction file among the fixtures is analyzed with it (docs/design/correction-files.md).
        wvc = path_for(name, "wvc")
        has_pair = kind == "wv" and wvc.exists()
        result = ext.build_manifest(path, note, findings, wvc.read_bytes() if has_pair else None)
        if has_pair:
            result["companion"] = wvc.name
        result["crosscheck"]["original_decoder"] = original
        return result
    if kind == "wvc":
        # A correction file has no header and no signature: it is a bitstream alone and nothing reads it by itself.
        return {"length": len(data), "kind": "correction", "encoder": note, "crosscheck": {"opens": False}}
    result = legacy_model.manifest(data, {"encoder": note})
    # A main file with its correction file among the fixtures is analyzed with it (docs/design/correction-files.md); a version 3 pair is compared only by decoding.
    if path_for(name, "wvc").exists():
        result["companion"] = path_for(name, "wvc").name
    result["crosscheck"] = {"libwavpack_legacy": result.pop("libwavpack_legacy"), "original_decoder": original}
    return result


def main():
    only_manifests = "--manifests-only" in sys.argv
    chosen = [a for a in sys.argv[1:] if not a.startswith("--")]
    releases = [a.split("=", 1)[1] for a in sys.argv[1:] if a.startswith("--release=")]
    chosen += [name for name, entry in FIXTURES.items() if entry["release"] in releases]
    if not only_manifests:
        for release in sorted({entry["release"] for name, entry in FIXTURES.items() if not chosen or name in chosen}):
            tool.check_runner(release)
    LEGACY.mkdir(parents=True, exist_ok=True)
    count = 0
    for name, entry in FIXTURES.items():
        wv = path_for(name, "wv")
        if only_manifests or (chosen and name not in chosen):
            if not wv.exists() or not manifest_path(wv).exists():
                if chosen and not only_manifests:
                    continue
                raise SystemExit(f"No manifest for {name} to take the encoder from; run the script without --manifests-only.")
            old = json.loads(manifest_path(wv).read_text(encoding="utf-8"))
            encoder, original = {k: v for k, v in old["encoder"].items() if k != "file"}, old["crosscheck"]["original_decoder"]
            produced = {kind: path_for(name, kind).read_bytes() for kind in entry["files"]}
        else:
            for old in (wv, path_for(name, "wvc")):
                old.unlink(missing_ok=True)
                for companion in old.parent.glob(old.name.rsplit(".", 1)[0] + ".*.json"):
                    companion.unlink()
            produced, encoder, original = encode(name, entry, None)
            for kind, content in produced.items():
                path_for(name, kind).write_bytes(content)
        for kind in entry["files"]:
            path = path_for(name, kind)
            manifest_path(path).write_text(json.dumps(build_manifest(name, kind, path, encoder, original), indent=1, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
            count += 1
    print(f"Wrote {count} {'manifests' if only_manifests else 'fixtures'} to {LEGACY} and {VERSION4}")


if __name__ == "__main__":
    main()
