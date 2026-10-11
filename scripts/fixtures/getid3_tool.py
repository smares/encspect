"""Runs getID3 (third_party/getid3, a pinned commit) through getid3_encoder.php: the LAME version it
reads, recovered from the padding where the tag cuts it short, and the encoder options it guesses,
among them the preset of LAME 3.90 to 3.95 from the tag's settings (module.audio.mp3.php,
GuessEncoderOptions). Needs php on the PATH."""

import json
import pathlib
import subprocess

DRIVER = pathlib.Path(__file__).resolve().parent / "getid3_encoder.php"


def encoder(path):
    """getID3's encoder and encoder options for path."""
    result = subprocess.run(["php", str(DRIVER), str(path)], capture_output=True, text=True, check=True)
    found = json.loads(result.stdout.splitlines()[0])
    return found["encoder"], found["encoder_options"]
