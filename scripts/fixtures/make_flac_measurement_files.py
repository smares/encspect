#!/usr/bin/env python3
"""Writes the files of the second-entropy-pass measurement (docs/design/decoders.md) into a folder; FlacDecodingMeasurement reads them.

    python3 scripts/fixtures/make_flac_measurement_files.py OUT_DIR [music.mp3 ...]

- Synthetic: 10 minutes of 16-bit 44.1 kHz stereo, sines of 440, 1,000 and 3,000 Hz plus white noise at 0.002, 0.05 and 0.3 of full scale (FFmpeg
  makes the signal), written by flac at levels 0, 5 and 8 for the 0.05 noise and at level 5 for the others.
- Music: each file named on the command line, decoded to 16-bit 44.1 kHz stereo and written by flac at level 5 (and 8 for the first).
  The run recorded in the design used two commercial tracks from the private repository's mp3pro folder, which are not committed.

Needs flac and ffmpeg; the files are about 400 MB in all, so delete them after the measurement.
"""

import pathlib
import subprocess
import sys


def run(*arguments):
    subprocess.run(arguments, check=True)


def synthetic(out, name, noise):
    wav = out / f"{name}.wav"
    run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=f=440:r=44100:d=600", "-f", "lavfi", "-i", "sine=f=1000:r=44100:d=600",
        "-f", "lavfi", "-i", "sine=f=3000:r=44100:d=600", "-f", "lavfi", "-i", f"anoisesrc=a={noise}:r=44100:c=white:d=600:seed=1",
        "-filter_complex", "[0][1][2]amix=inputs=3:normalize=0,volume=0.3[t];[t][3]amix=inputs=2:normalize=0,aformat=sample_fmts=s16:channel_layouts=stereo[a]",
        "-map", "[a]", "-t", "600", str(wav))
    return wav


def encode(wav, out, name, level):
    run("flac", "-s", "-f", f"-{level}", str(wav), "-o", str(out / f"{name}-l{level}.flac"))


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    out = pathlib.Path(sys.argv[1])
    out.mkdir(parents=True, exist_ok=True)
    for name, noise, levels in (("s05", 0.05, (0, 5, 8)), ("s03", 0.3, (5,)), ("s0002", 0.002, (5,))):
        wav = synthetic(out, name, noise)
        for level in levels:
            encode(wav, out, name, level)
        wav.unlink()
    for number, music in enumerate(sys.argv[2:], 1):
        wav = out / f"m{number}.wav"
        run("ffmpeg", "-v", "error", "-y", "-i", music, "-ac", "2", "-ar", "44100", "-sample_fmt", "s16", str(wav))
        for level in ((5, 8) if number == 1 else (5,)):
            encode(wav, out, f"m{number}", level)
        wav.unlink()


if __name__ == "__main__":
    main()
