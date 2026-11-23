#!/usr/bin/env python3
"""What the TAK decoder must give back for each encoder-written TAK fixture, written into its manifest (decoded_samples, decoded_pcm_md5, decode_findings).

    python3 scripts/fixtures/tak_decoded.py [--check]

The samples are those of the signal Takc was given, never the analyzer's or a TAK decoder's:

- For a file whose input is one of tak_cases.py's signals, the WAVE file is written again from the signal; its SHA-256 must equal the provenance's input.sha256, which
  ties the manifest to what Takc read. Its data chunk is the PCM.
- For the lossyWAV file the input is lossyWAV's output (a private tool, not run here). The PCM is FFmpeg's decoding of the TAK file, accepted only if the stored WAV
  header, the PCM and the stored footer together have the SHA-256 the provenance records for the input.
- For every file with a wave data object, the stored header, the PCM and the stored footer together are also checked against that SHA-256.

decoded_pcm_md5 is the MD5 of the samples in the canonical layout of the decoders (signed, little-endian, as many whole bytes as the depth needs; 8-bit WAVE data is
unsigned, and TAK decodes to values around 0). docs/development.md, "TAK decoding (clean-room)". --check compares the manifests with what the script works out and writes
nothing.
"""

import hashlib
import json
import pathlib
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import tak_cases  # noqa: E402

ROOT = HERE.parents[1]
FIXTURES = ROOT / "tests" / "fixtures" / "tak"
FFMPEG_FORMATS = {8: "u8", 16: "s16le", 24: "s24le"}


def data_chunk(wav):
    """The bytes of the data chunk of a WAVE file; one of unknown size (0xFFFFFFFF) runs to the end."""
    position = 12
    while position + 8 <= len(wav):
        name = wav[position:position + 4]
        size = int.from_bytes(wav[position + 4:position + 8], "little")
        if name == b"data":
            return wav[position + 8:] if size == 0xFFFFFFFF else wav[position + 8:position + 8 + size]
        position += 8 + size + (size & 1)
    raise ValueError("no data chunk")


def stored_wave_parts(tak):
    """The header and the footer the wave data object (ID 3) stores, or (None, None)."""
    position = 4
    while position + 4 <= len(tak):
        header = int.from_bytes(tak[position:position + 4], "little")
        object_id, size = header & 127, header >> 8
        if object_id == 0:
            break
        if object_id == 3:
            body = tak[position + 4:position + 4 + size]
            head, foot = int.from_bytes(body[0:3], "little"), int.from_bytes(body[3:6], "little")
            return body[6:6 + head], body[6 + head:6 + head + foot]
        position += 4 + size
    return None, None


def canonical(pcm, bits):
    return bytes((b - 128) & 255 for b in pcm) if bits == 8 else pcm


def pcm_of(path, provenance):
    """(the PCM as the WAVE file holds it, how it was obtained)."""
    signal = provenance["input"]["signal"]
    tak = path.read_bytes()
    head, foot = stored_wave_parts(tak)
    if signal is not None:
        options = {"mask": signal["mask"], "chunks": signal["chunks"], "unknown_size": signal["unknownSize"]}
        wav = tak_cases.wav_bytes(signal["rate"], signal["channels"], signal["bits"], signal["frames"], signal["kind"], options)
        if hashlib.sha256(wav).hexdigest() != provenance["input"]["sha256"]:
            raise SystemExit(f"{path.name}: the signal written again is not the input Takc was given (SHA-256 differs)")
        pcm = data_chunk(wav)
        source = f"the signal {provenance['input']['name']} of tak_cases.py, written again and checked against the input's SHA-256"
    else:
        bits = json.loads(path.with_name(path.name[:-4] + ".expected.json").read_text(encoding="utf-8"))["stream_info"]["bits_per_sample"]
        result = subprocess.run(["ffmpeg", "-v", "error", "-nostdin", "-i", str(path), "-f", FFMPEG_FORMATS[bits], "-"], capture_output=True)
        if result.returncode != 0 or not result.stdout:
            raise SystemExit(f"{path.name}: ffmpeg did not decode it")
        pcm = result.stdout
        source = "FFmpeg's decoding, accepted because the stored WAV header, the PCM and the stored footer have the input's SHA-256 (lossyWAV's output cannot be written again here)"
        if head is None:
            raise SystemExit(f"{path.name}: no wave data object to check the PCM against")
    if head is not None and hashlib.sha256(head + pcm + (foot or b"")).hexdigest() != provenance["input"]["sha256"]:
        raise SystemExit(f"{path.name}: the stored header, the PCM and the stored footer do not have the input's SHA-256")
    return pcm, source


def fields(path):
    provenance = json.loads(path.with_name(path.name + ".provenance.json").read_text(encoding="utf-8"))
    manifest = json.loads(path.with_name(path.name[:-4] + ".expected.json").read_text(encoding="utf-8"))
    info = manifest["stream_info"]
    pcm, source = pcm_of(path, provenance)
    bits = info["bits_per_sample"]
    frame = info["channels"] * (bits // 8)
    if len(pcm) % frame:
        raise SystemExit(f"{path.name}: the PCM is not a whole number of sample frames")
    return {"decoded_samples": len(pcm) // frame, "decoded_pcm_md5": hashlib.md5(canonical(pcm, bits)).hexdigest(), "decoded_pcm_source": source, "decode_findings": []}


def update(check=False):
    changed = 0
    for path in sorted(FIXTURES.glob("external-*.tak")):
        manifest_path = path.with_name(path.name[:-4] + ".expected.json")
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        wanted = fields(path)
        if any(manifest.get(key) != value for key, value in wanted.items()):
            changed += 1
            if check:
                print(f"{manifest_path.name} differs")
                continue
            manifest.update(wanted)
            manifest_path.write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"{changed} manifests {'differ' if check else 'written'}")
    return 1 if check and changed else 0


if __name__ == "__main__":
    sys.exit(update(check="--check" in sys.argv))
