#!/usr/bin/env python3
"""Writes the hand-built WavPack fixtures of versions 1 to 3 in tests/fixtures/wavpack/legacy (built-legacy-*.wv) and their manifests.

Each is made from an encoder-written fixture of make_external_wavpack_legacy_fixtures.py by changing bytes: a version the format does not have, a flag
combination libwavpack refuses, a stored WAV header it refuses, a header or a bitstream cut short, a changed check or total, a run of 0xFF that no chunk follows,
a raw file with too many extra bytes, tags in front and behind. The standard library only: CI regenerates them from the committed encoder-written fixtures and
fails if the files differ.

The manifest of each is worked out by wavpack_legacy_model.py from the bytes (the header, the stored WAV header, the figures, the trailer and the findings the analyzer
must report) and says whether libwavpack's legacy decoder opens the file where that is known, which crosscheck_wavpack_legacy_fixtures.py confirms. A chunk before the data
of more than 1 MiB is crafted in the tests instead: the fixture would be a megabyte.

    make_wavpack_legacy_fixtures.py
"""

import json
import pathlib
import struct

import make_wavpack_fixtures as built
import wavpack_legacy_model as model

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "wavpack" / "legacy"


def base(name):
    return (DIRECTORY / f"external-{name}.wv").read_bytes()


def header_at(data):
    """Where the WavPack header begins: after the stored WAV header, or 0 for a raw file."""
    found = model.chunks_before_data(data)
    return 0 if found is None else found[2]


def put(data, at, fmt, *values):
    out = bytearray(data)
    struct.pack_into(fmt, out, at, *values)
    return bytes(out)


def with_header_field(data, offset, fmt, value):
    return put(data, header_at(data) + offset, fmt, value)


def with_fmt_field(data, offset, fmt, value):
    return put(data, 12 + 8 + offset, fmt, value)


def with_flags(data, flags):
    return with_header_field(data, 12, "<H", flags)


def read_flags(data):
    return struct.unpack_from("<H", data, header_at(data) + 12)[0]


def wav_header(chunks):
    """A RIFF header of the chunks (id, body), as 3.4 keeps one, with the data chunk's size of the source."""
    body = b"".join(i + struct.pack("<I", len(b)) + b + (b"\0" if len(b) & 1 else b"") for i, b in chunks)
    return b"RIFF" + struct.pack("<I", 4 + len(body) + 8 + 96000) + b"WAVE" + body + b"data" + struct.pack("<I", 96000)


def with_wav_header(data, chunks):
    return wav_header(chunks) + data[header_at(data):]


FMT = struct.pack("<HHIIHH", 1, 2, 44100, 176400, 4, 16)

# name: (description, from, change, extra findings, libwavpack's answer where it is known)
FIXTURES = {}


def add(name, description, source, change, extra=(), opens=None):
    FIXTURES[name] = (description, source, change, extra, opens)


def build_all():
    stereo, hybrid, raw = "wv340-lossless-16-stereo", "wv340-hybrid-320", "wv340-raw-stereo"
    v1, v2, chunks, lossywav = "wv100-stereo-16", "wv200-stereo-16", "wv340-chunks", "wv340-lossywav"

    add("version-0", "The version field of the WavPack header is 0.", stereo, lambda d: with_header_field(d, 8, "<H", 0), opens=False)
    add("version-4", "The version field of the WavPack header is 4, behind the stored WAV header.", stereo, lambda d: with_header_field(d, 8, "<H", 4), opens=False)
    add("flags-not-stored", "The extreme flag with a flag that is never stored (the noise calculation flag).", stereo, lambda d: with_flags(d, read_flags(d) | 0x0008), opens=False)
    add("flags-hybrid-fast", "A hybrid target with the new high flag, the fast flag and the extreme flag.", hybrid, lambda d: with_flags(d, read_flags(d) | 0x8000 | 0x0002), opens=False)
    add("flags-bits-cross-channel", "A hybrid target with the cross-channel flag and the extreme flag.", hybrid, lambda d: with_flags(d, read_flags(d) | 0x8000 | 0x1000), opens=False)
    add("header-size-0", "The size field of the WavPack header is 0.", stereo, lambda d: with_header_field(d, 4, "<I", 0), opens=True)
    add("wav-8-bit", "The stored WAV header says 8 bits a sample.", stereo, lambda d: with_fmt_field(d, 14, "<H", 8), opens=False)
    add("wav-3-channels", "The stored WAV header says 3 channels, with the block align of 6.", stereo, lambda d: with_fmt_field(with_fmt_field(d, 2, "<H", 3), 12, "<H", 6), opens=False)
    add("wav-format-tag-float", "The stored WAV header has the format tag of IEEE float, 3.", stereo, lambda d: with_fmt_field(d, 0, "<H", 3), opens=False)
    add("wav-block-align", "The stored WAV header has a block align of 5 bytes for 2 channels.", stereo, lambda d: with_fmt_field(d, 12, "<H", 5), opens=False)
    add("wav-sample-rate-0", "The stored WAV header has a sample rate of 0.", stereo, lambda d: with_fmt_field(d, 4, "<I", 0), opens=False)
    add("wav-no-format", "The stored WAV header has no format chunk.", stereo, lambda d: with_wav_header(d, []))
    add("wav-format-short", "The format chunk of the stored WAV header states 14 bytes.", stereo, lambda d: with_wav_header(d, [(b"fmt ", FMT[:14])]), opens=False)
    add("total-samples-more", "The total samples of the header are one more than the data chunk's size gives.", stereo, lambda d: with_header_field(d, 16, "<I", 24001), opens=True)
    add("total-samples-zero", "The total samples of the header are 0.", stereo, lambda d: with_header_field(d, 16, "<I", 0), opens=True)
    add("channels-mismatch", "The mono flag is set in a file whose stored WAV header says 2 channels.", stereo, lambda d: with_flags(d, read_flags(d) | 0x0001))
    add("header-cut-v3", "The data ends 20 bytes into the WavPack header of version 3.", stereo, lambda d: d[:header_at(d) + 20], opens=False)
    add("header-cut-v2", "The data ends 11 bytes into the WavPack header of version 2.", v2, lambda d: d[:header_at(d) + 11], opens=False)
    add("header-cut-v1", "The data ends 8 bytes into the WavPack header of version 1, before the version field is complete.", v1, lambda d: d[:header_at(d) + 8], opens=False)
    add("header-only-v3", "The data ends where the WavPack header of version 3 ends.", stereo, lambda d: d[:header_at(d) + 36], opens=True)
    add("bitstream-cut", "The bitstream is cut in half.", stereo, lambda d: d[:header_at(d) + 36 + (len(d) - header_at(d) - 36) // 2])
    add("bit-flipped", "One bit of the bitstream is flipped, so a decoder's CRC fails.", stereo, lambda d: put(d, header_at(d) + 36 + 4000, "<B", d[header_at(d) + 36 + 4000] ^ 0x10))
    add("crc-changed", "The audio CRC of the header is changed.", stereo, lambda d: with_header_field(d, 20, "<I", struct.unpack_from("<I", d, header_at(d) + 20)[0] ^ 0xFFFF), opens=True)
    add("marker-without-chunks", "16 bytes of 0xFF at the end of the data, with no chunk after them.", stereo, lambda d: d + b"\xff" * 16)
    add("trailer-many-chunks", "A trailer of 70 chunks of no content after the 16 bytes of 0xFF.", stereo, lambda d: d + b"\xff" * 16 + b"".join(b"c%03d" % i + bytes(4) for i in range(70)))
    add("trailer-odd-chunk", "A trailer whose only chunk has an odd size, with its pad byte.", stereo, lambda d: d + b"\xff" * 16 + b"odd " + struct.pack("<I", 3) + b"abc\0")
    add("raw-extra-bytes-4", "The header of a raw file says 4 bytes follow the last whole sample.", raw, lambda d: with_header_field(d, 32, "<B", 4))
    add("raw-version-1", "A raw file whose header says version 1.", raw, lambda d: with_header_field(d, 8, "<H", 1)[:12], opens=False)
    add("id3v2-leading", "An ID3v2 tag in front of the file.", stereo, lambda d: built.id3v2() + d, opens=False)
    add("ape-and-id3v1", "An APEv2 tag and an ID3v1 tag after the bitstream.", stereo, lambda d: d + built.ape_tag(("Title", "Test")) + built.id3v1(), opens=True)
    add("trailer-and-tags", "The trailer of a file that has chunks after its audio, then an APEv2 tag.", chunks, lambda d: d + built.ape_tag(("Title", "Test")), opens=True)
    add("lossywav-fact-unrecognized", "The text of lossyWAV's fact chunk does not have the form \"lossyWAV <version> @ <date>, <settings>\".", lossywav,
        lambda d: d.replace(b" @ ", b" - ", 1), [("info", "wavpack.wrapper-fact-text-unrecognized")], opens=True)
    add("wav-chunks-many", "Eight chunks of no content between the format chunk and the data in the stored WAV header.", stereo,
        lambda d: with_wav_header(d, [(b"fmt ", FMT)] + [(b"JUNK", b"\0\0\0\0")] * 8), opens=True)


def manifest(name, description, source, data, extra, opens):
    if name == "id3v2-leading":
        # The tag is the host's: the file is the WAV header onwards, so the model reads it from there.
        tag = len(built.id3v2())
        result = model.manifest(data[tag:])
        result["header"]["offset"] += tag
        for chunk in result["wav"]["chunks"]:
            chunk["offset"] += tag
        result["wav"]["data_offset"] += tag
        result["bitstream"]["offset"] += tag
        result["length"] = len(data)
        result["leading_tag"] = tag
        result["libwavpack_legacy"] = {}
    elif name in ("ape-and-id3v1", "trailer-and-tags"):
        suffix = len(built.ape_tag(("Title", "Test"))) + (128 if name == "ape-and-id3v1" else 0)
        result = model.manifest(data[:-suffix])
        result["length"] = len(data)
        result["trailing_tags"] = suffix
    else:
        result = model.manifest(data)
    result["findings"] = sorted(result["findings"] + [{"severity": s, "code": c} for s, c in extra], key=lambda f: (f["code"], f["severity"]))
    expectation = result.pop("libwavpack_legacy", {})
    result["built"] = {"description": description, "from": f"external-{source}.wv"}
    if opens is not None:
        # Where the answer is known, the model's must agree with it; damage to the bitstream is not worked out, so only whether the file opens is stated.
        if expectation and expectation.get("opens") is not None and expectation["opens"] != opens:
            raise SystemExit(f"{name}: the model expects libwavpack to {'open' if expectation['opens'] else 'refuse'} the file, the table says {'open' if opens else 'refuse'}")
        result["crosscheck"] = {"libwavpack_legacy": {"opens": opens}}
    return result


def main():
    build_all()
    for old in DIRECTORY.glob("built-legacy-*"):
        old.unlink()
    for name, (description, source, change, extra, opens) in FIXTURES.items():
        data = change(base(source))
        path = DIRECTORY / f"built-legacy-{name}.wv"
        path.write_bytes(data)
        text = json.dumps(manifest(name, description, source, data, extra, opens), indent=1, ensure_ascii=False) + "\n"
        path.with_name(path.name[:-3] + ".expected.json").write_text(text, encoding="utf-8")
    print(f"Wrote {len(FIXTURES)} fixtures to {DIRECTORY}")


if __name__ == "__main__":
    main()
