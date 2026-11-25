"""A real recording for the encoder-written fixture that the README's screenshots show: Mozart's Magic Flute overture,
played by the Czech National Symphony Orchestra and released by Musopen's 2012 Kickstarter project into the public domain.

The Internet Archive item "musopen-dvd-in-lossless-flac-format" holds it as 24-bit, 48 kHz FLAC and marks it with the Creative Commons
Public Domain Mark 1.0; Mozart died in 1791. The file is pinned by SHA-256 (downloads.py), kept in the download cache and never in the
working tree. The WAV the encoder reads is the whole recording as 16-bit PCM, converted by ffmpeg without dither, so the same FLAC always
gives the same bytes for one ffmpeg version.
"""

import pathlib
import subprocess

import downloads

URL = "https://archive.org/download/musopen-dvd-in-lossless-flac-format/Mozart%20-%20Magic%20Flute%20Overture/Magic%20Flute%20Overture.flac"
ARCHIVE = "Magic Flute Overture.flac"
SHA256 = "2703b6cfa9ffd443f4126f4c71319be6e1a392391a09fbd098369603d336d51a"
ADDRESSES = [URL]
RATE = 48000


def write_wav(path):
    """Writes the recording as a 16-bit stereo WAV file at path."""
    flac = pathlib.Path(path).with_suffix(".flac")
    downloads.fetch(ADDRESSES, SHA256, flac)
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(flac), "-c:a", "pcm_s16le", "-af", "aresample=osf=s16:dither_method=none", str(path)], check=True)
    flac.unlink()
