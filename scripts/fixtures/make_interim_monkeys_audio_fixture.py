#!/usr/bin/env python3
"""Writes tests/fixtures/monkeys-audio/external-interim-24-stereo.ape, a 24-bit file in the "interim" coding of Monkey's Audio from the addition of 32-bit audio to version 8.50.

Those encoders summed the first stage of the predictor in 64 bits for samples over 16 bits; the SDK's decoder undoes that in its "interim mode", which it
enters when a frame of a 24-bit file fails its CRC (APEDecompressCore.cpp, DecodeFrame; Interim.cpp). The SDK 13.44 encoder no longer writes such files, so
the fixture comes from mac built with the one line of its encoder changed back (monkeys_audio_tool.build_interim_mac). The unchanged mac 13.44 verifies and
decodes the file through its interim mode (crosscheck_monkeys_audio_fixtures.py), and the analyzer must do the same.

The signal is a loud sine with noise (monkeys_audio_signal.py, "loud-sine-noise"), whose first stage overflows 32 bits on some samples. --manifests-only recomputes the manifest from the committed file.
"""

import json
import pathlib
import sys
import tempfile

import make_external_monkeys_audio_fixtures as ext
import monkeys_audio_tool

NAME = "interim-24-stereo"
SPEC = ("wav", "loud-sine-noise", 44100, 2, 24, 8000)
ARGUMENTS = ["-c2000"]


def main():
    ext.OUT.mkdir(parents=True, exist_ok=True)
    path = ext.OUT / f"external-{NAME}.ape"
    manifest = ext.manifest_path(path)
    ext.TOOL = f"mac built from {monkeys_audio_tool.NAME} with the line of the encoder that cuts the first stage to 32 bits changed (monkeys_audio_tool.build_interim_mac)"
    if "--manifests-only" in sys.argv:
        encoder = json.loads(manifest.read_text(encoding="utf-8"))["encoder"]
    else:
        ext.MAC = monkeys_audio_tool.build_interim_mac(pathlib.Path(tempfile.gettempdir()) / "monkeys-audio-interim-sdk")
        with tempfile.TemporaryDirectory() as work:
            encoder = ext.encode(NAME, SPEC, ARGUMENTS, pathlib.Path(work))
    manifest.write_text(ext.m.dump(ext.build_manifest(path, encoder, SPEC, [])), encoding="utf-8")
    print(f"Wrote {path}")


if __name__ == "__main__":
    main()
