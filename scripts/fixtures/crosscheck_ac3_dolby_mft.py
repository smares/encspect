#!/usr/bin/env python3
"""Rollout step 7 (docs/design/official-decoder-crosschecks.md): the AC-3 decoder's PCM against Dolby's decoder on a Windows machine that has it, by hand.

    py -3 scripts/fixtures/crosscheck_ac3_dolby_mft.py --probe WmaDecodeProbe.exe --pcmdump "dotnet ...Encspect.PcmDump.dll" --encspect "...encspect.exe"

The probe's `ac3-pcm` mode (Ac3Mft.cs) feeds each syncframe to "Microsoft Dolby Digital Plus Decoder MFT" and writes floats; the comparison is crosscheck_ac3_coreaudio.py's (strict on a
copy with the dither flags cleared, bands on the file repeated otherwise, every channel order and first position listed), so a first run records what the MFT does. Not run
when written. The record to keep is the output with the machine's Windows build and DolbyDecMFT.dll version.
"""

import json
import pathlib
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import crosscheck_ac3_coreaudio as c  # noqa: E402
import unit_list  # noqa: E402


def decode(probe, path, out):
    tsv = out.with_suffix(".units.tsv")
    tsv.write_text(unit_list.tsv(unit_list.extract_bytes(pathlib.Path(path).read_bytes())), encoding="utf-8")
    run = subprocess.run([probe, "ac3-pcm", str(path), str(tsv), str(out)], capture_output=True, text=True)
    line = next((l for l in run.stdout.splitlines() if l.startswith("{")), "{}")
    info = json.loads(line)
    if run.returncode != 0 or "error" in info or not out.exists():
        return None, info
    return out.read_bytes(), {"channels": info["channels"], "frames": info["frames"], "layoutTag": json.dumps(info.get("output"))}


c.decode = decode

if __name__ == "__main__":
    sys.exit(c.main())
