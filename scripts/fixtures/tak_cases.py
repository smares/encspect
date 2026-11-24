#!/usr/bin/env python3
"""The encodes of the TAK fixtures: the test signals as WAV files and the plan of Takc.exe runs over them.

    python3 scripts/fixtures/tak_cases.py OUT_DIR

Writes OUT_DIR/pcm/<input>.wav and OUT_DIR/plan.json, which scripts/fixtures/windows/make_windows_tak_fixtures.ps1 runs on a Windows runner
(Takc.exe runs there only) and import_windows_tak_fixtures.py reads back through the records the script writes. The design is
docs/design/tak-optimfrog-headers.md, "Fixtures", "TAK, on Windows".

- The signals are those of wma_signals.py (a tone with noise bursts, deterministic), 0.25 s long to keep the files small, or 1 s where lossyWAV
  needs blocks to work on.
- A case is a name, an input, Takc's arguments, how the input reaches Takc (`file`, or `pipe`: Takc reads standard input, the script
  writing the WAV's bytes to it), whether the case may fail without failing the run (`optional`, with the reason), and for the lossyWAV
  case the preprocessing (`lossywav`) whose output is the input.
- The `pair-*` cases look for files whose channel table pairs channels (codec 4's types 0 to 3, which no other case has produced); the importer keeps only those.
- The file names say what the settings are; the importer names the committed files after what the headers say (the stream info and
  the encoder info), as the WMA importer does, and keeps the request in the provenance.
"""

import itertools
import json
import pathlib
import struct
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import wma_signals  # noqa: E402

PCM_SUBFORMAT = bytes.fromhex("0100000000001000800000aa00389b71")
SECONDS = 0.25

# name -> (rate, channels, bits, frames, kind, options)
INPUTS = {
    "cd-16-stereo": (44100, 2, 16, 11024, "tones", {}),
    "mono-8": (44100, 1, 8, 11024, "tones", {}),
    "stereo-24": (44100, 2, 24, 11024, "tones", {}),
    "stereo-96k": (96000, 2, 16, 24000, "tones", {}),
    "stereo-192k": (192000, 2, 16, 48000, "tones", {}),
    "surround-5.1-mask": (48000, 6, 16, 12000, "tones", {"mask": 0x3F}),
    "surround-6ch-nomask": (48000, 6, 16, 12000, "tones", {}),
    "cd-16-stereo-chunks": (44100, 2, 16, 11024, "tones", {"chunks": True}),
    "cd-16-stereo-unknown-size": (44100, 2, 16, 11024, "tones", {"unknown_size": True}),
    "lossywav-source": (44100, 2, 16, 44100, "tones", {}),
    # Six channels in three pairs (0 and 1, 2 and 3, 4 and 5) that Takc's encoder may enter into the channel table as paired (docs/formats/tak.md, "Decoding";
    # tak-format.md, 6.2): the second channel of a pair equals the first, differs by noise of 2 LSB, or is the first at another gain. About 2 s.
    "pairs-identical": (48000, 6, 16, 96000, "pairs-identical", {"mask": 0x3F}),
    "pairs-noisy": (48000, 6, 16, 96000, "pairs-noisy", {"mask": 0x3F}),
    "pairs-gains": (48000, 6, 16, 96000, "pairs-gains", {"mask": 0x3F}),
}

PAIR_PRESETS = ("-p2m", "-p3e", "-p4m")
PAIR_GAINS = ((3, 5), (7, 4), (-1, 2))


def pair_samples(rate, bits, frames, kind):
    """The interleaved samples of a pairs-* signal: three base channels (the tones signal of wma_signals.py), each followed by its partner."""
    base = list(itertools.islice(wma_signals.samples(rate, 3, bits, frames / rate + 1, "tones"), frames * 3))
    full = (1 << (bits - 1)) - 1
    seed = 4242
    out = []
    for i in range(frames):
        for k in range(3):
            x = base[i * 3 + k]
            if kind == "pairs-identical":
                y = x
            elif kind == "pairs-noisy":
                seed = (seed * 1103515245 + 12345) & 0x7FFFFFFF
                y = x + ((seed >> 16) % 5) - 2
            else:
                numerator, denominator = PAIR_GAINS[k]
                y = x * numerator // denominator
            out.append(x)
            out.append(max(-full - 1, min(full, y)))
    return out


def chunk(tag, payload):
    pad = b"\0" if len(payload) % 2 else b""
    return tag + struct.pack("<I", len(payload)) + payload + pad


def wav_bytes(rate, channels, bits, frames, kind, options):
    if kind.startswith("pairs-"):
        values = pair_samples(rate, bits, frames, kind)
    else:
        values = itertools.islice(wma_signals.samples(rate, channels, bits, frames / rate + 1, kind), frames * channels)
    if bits == 8:
        data = bytes(v + 128 for v in values)
    elif bits == 16:
        data = b"".join(struct.pack("<h", v) for v in values)
    else:
        data = b"".join(struct.pack("<i", v)[:3] for v in values)
    block = channels * bits // 8
    mask = options.get("mask")
    if mask is None:
        fmt = struct.pack("<HHIIHH", 1, channels, rate, rate * block, block, bits)
    else:
        fmt = struct.pack("<HHIIHHHHI", 0xFFFE, channels, rate, rate * block, block, bits, 22, bits, mask) + PCM_SUBFORMAT
    before = after = b""
    if options.get("chunks"):
        before = chunk(b"LIST", b"INFO" + chunk(b"ISFT", b"Encspect test signal\0"))
        after = chunk(b"id3 ", b"Encspect trailing chunk, 40 bytes of text.\0\0\0")
    body = b"WAVE" + chunk(b"fmt ", fmt) + before
    if options.get("unknown_size"):
        # What a program that cannot know the length writes to a pipe; Takc's -ihs ignores both sizes.
        return b"RIFF" + struct.pack("<I", 0xFFFFFFFF) + body + b"data" + struct.pack("<I", 0xFFFFFFFF) + data
    audio = chunk(b"data", data)
    return b"RIFF" + struct.pack("<I", len(body) + len(audio) + len(after)) + body + audio + after


def cases():
    """The plan: a list of dicts, in the order the script runs them."""
    plan = []

    def add(name, input_name, arguments, mode="file", optional=None, **extra):
        case = {"name": name, "input": input_name, "arguments": arguments, "mode": mode, "optional": optional}
        case.update(extra)
        plan.append(case)

    for level in range(5):
        for letter, word in (("", "standard"), ("e", "extra"), ("m", "max")):
            add(f"preset-p{level}{letter}", "cd-16-stereo", [f"-p{level}{letter}"], evaluation=word)
    add("format-8-mono", "mono-8", ["-p2"])
    add("format-24-stereo", "stereo-24", ["-p2"])
    add("format-96k", "stereo-96k", ["-p2"])
    add("format-192k", "stereo-192k", ["-p2"])
    add("format-5.1-mask", "surround-5.1-mask", ["-p2"])
    add("format-6ch-nomask", "surround-6ch-nomask", ["-p2"],
        optional="Takc may refuse six channels without a channel mask; the record then says what it printed")
    for limit in (512, 1024, 2048, 4096, 8192):
        add(f"fsl-{limit}", "cd-16-stereo", ["-p2", f"-fsl{limit}"])
    # Takc 2.3.3 refuses -fsl16384 at -p2 ("FrameSizeLimit too high for preset", run 42719141357).
    add("fsl-16384", "cd-16-stereo", ["-p4", "-fsl16384"], optional="the highest preset may be the only one that takes a limit of 16384")
    add("md5", "cd-16-stereo", ["-p2", "-md5"])
    add("wm0", "cd-16-stereo", ["-p2", "-wm0"])
    add("wave-chunks", "cd-16-stereo-chunks", ["-p2"])
    add("tag-ape", "cd-16-stereo", ["-p2", "-tt", "TITLE=Encspect test", "-tt", "ARTIST=Encspect"])
    add("pipe", "cd-16-stereo", ["-p2"], mode="pipe")
    add("pipe-ihs", "cd-16-stereo-unknown-size", ["-p2", "-ihs"], mode="pipe",
        optional="a WAV with unknown sizes is the case -ihs exists for, but Takc's reaction to the sizes is not documented")
    add("pipe-wm1024", "cd-16-stereo", ["-p2", "-wm1024"], mode="pipe",
        optional="reserves 1024 bytes for the wave data, which should leave unused space in the metadata")
    # Encodes that may pair channels in the channel table: import_windows_tak_fixtures.py reads the table of every frame and commits only the files in which
    # one is paired; the others are listed in tests/fixtures/tak/pairs-tried.json.
    for signal in ("identical", "noisy", "gains"):
        for preset in PAIR_PRESETS:
            add(f"pair-{signal}{preset}", f"pairs-{signal}", [preset], optional="may not pair channels; the importer commits only a file whose channel tables pair")
    add("lossywav-fsl512", "lossywav-source", ["-p2", "-fsl512"],
        optional="needs lossyWAV 1.4.2 from the private repository (ENCSPECT_PRIVATE_TOKEN)",
        lossywav={"arguments": ["-q", "S"], "from": "lossywav-source"})
    return plan


def main():
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    out = pathlib.Path(sys.argv[1])
    (out / "pcm").mkdir(parents=True, exist_ok=True)
    signals = {}
    for name, (rate, channels, bits, frames, kind, options) in INPUTS.items():
        data = wav_bytes(rate, channels, bits, frames, kind, options)
        (out / "pcm" / f"{name}.wav").write_bytes(data)
        signals[name] = {"rate": rate, "channels": channels, "bits": bits, "frames": frames, "kind": kind,
                         "mask": options.get("mask"), "chunks": bool(options.get("chunks")),
                         "unknownSize": bool(options.get("unknown_size")), "size": len(data)}
    plan = {"signals": signals, "cases": cases()}
    (out / "plan.json").write_text(json.dumps(plan, indent=1) + "\n", encoding="utf-8")
    print(f"{len(signals)} signals and {len(plan['cases'])} cases in {out}")


if __name__ == "__main__":
    main()
