#!/usr/bin/env python3
"""Takes the files of the wma-fixtures workflow's artifact into tests/fixtures/asf.

    python3 scripts/fixtures/import_windows_wma_fixtures.py path/to/extracted/artifact [--force] [--allow-large]

For each encode that worked (encodes.json) it writes, next to the other ASF fixtures:

- external-windows-<name>.wma, the file as the Windows Media Format SDK's writer wrote it, byte
  for byte. The writer stamps a new file ID and the time of the encode into the File Properties
  object, so another run gives other bytes; the files are committed once and checked by their
  content, never regenerated. Nothing is zeroed or rewritten, so what the encoder wrote is what
  the fixture holds.
- external-windows-<name>.provenance.json, which records where the file came from: the encoder
  and its codec, the format the SDK listed and the settings it was asked for, the PCM it was fed
  (the arguments of wma_signals.py, which writes the same PCM again for the lossless check), the
  runner image, the Windows version, wmvcore.dll's version, and the SHA-256 of the file.

make_external_asf_fixtures.py --manifests-only then writes the expected.json manifests from the
files and these records. A file that is already there is left alone unless it is the same bytes or
--force is given. Files over 600 KB are refused unless --allow-large is given: a fixture is a few
seconds of audio.
"""

import hashlib
import json
import pathlib
import shutil
import sys

ROOT = pathlib.Path(__file__).resolve().parent
OUT = ROOT.parents[1] / "tests" / "fixtures" / "asf"
LARGE = 600_000


def main():
    arguments = [a for a in sys.argv[1:] if not a.startswith("--")]
    if len(arguments) != 1:
        raise SystemExit(__doc__)
    force = "--force" in sys.argv
    allow_large = "--allow-large" in sys.argv
    artifact = pathlib.Path(arguments[0])
    encodes = json.loads((artifact / "encodes.json").read_text(encoding="utf-8-sig"))
    environment = json.loads((artifact / "environment.json").read_text(encoding="utf-8-sig"))
    if isinstance(encodes, dict):
        encodes = [encodes]
    OUT.mkdir(parents=True, exist_ok=True)

    imported = 0
    for entry in encodes:
        name = entry["name"]
        if entry.get("status") != "ok":
            print(f"{name}: skipped, {entry.get('status')}")
            continue
        source = artifact / entry["file"]
        data = source.read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        if digest != entry["sha256"]:
            raise SystemExit(f"{name}: the file's SHA-256 {digest} is not the {entry['sha256']} encodes.json records.")
        if len(data) > LARGE and not allow_large:
            print(f"{name}: skipped, {len(data)} bytes is over {LARGE}; pass --allow-large")
            continue
        target = OUT / f"external-windows-{name}.wma"
        if target.exists() and target.read_bytes() != data and not force:
            print(f"{name}: skipped, {target.name} exists with other bytes; pass --force")
            continue
        shutil.copyfile(source, target)
        provenance = {
            "description": f"Encoded by the Windows Media Format SDK's writer (wmvcore.dll) as {entry['codecName']}, {entry['mode'].upper()}, \"{entry['formatDescription']}\".",
            "encoder": {
                "tool": "Windows Media Format SDK writer (wmvcore.dll)",
                "codec": entry["codecName"],
                "format_index": entry["formatIndex"],
                "format_description": entry["formatDescription"],
                "format_tag": entry["formatTag"],
                "mode": entry["mode"],
                "requested": {k: entry[k] for k in ("requestedChannels", "requestedRates", "requestedFormatBits", "requestedQuality", "requestedBitrate") if entry.get(k) not in (None, 0, [])},
                "format_channels": entry["formatChannels"],
                "format_rate": entry["formatRate"],
                "format_bits": entry["formatBits"],
                "format_bitrate": entry["formatBitrate"],
                "config_bitrate": entry.get("configBitrate"),
                "buffer_window_ms": entry.get("bufferWindow"),
                "title": entry.get("title"),
                "author": entry.get("author"),
            },
            "input": {
                "signal": entry["signal"],
                "channels": entry["formatChannels"],
                "rate": entry["formatRate"],
                "bits": entry["inputBits"],
                "seconds": entry["seconds"],
                "frames": entry["frames"],
                "type": entry["inputType"],
                "generator": "wma_signals.py",
            },
            "runner": {
                "image_os": environment.get("runnerImageOS"),
                "image_version": environment.get("runnerImageVersion"),
                "windows": environment.get("windowsCaption"),
                "windows_version": environment.get("windowsVersion"),
                "architecture": environment.get("architecture"),
                "wmvcore_version": environment.get("wmvcoreVersion"),
            },
            "bytes": len(data),
            "sha256": digest,
            "reproducible": False,
            "note": "The File Properties object's file ID and creation date change with every run; the file is committed as written.",
        }
        (OUT / f"external-windows-{name}.provenance.json").write_text(json.dumps(provenance, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"{name}: {len(data)} bytes, {entry['codecName']}, \"{entry['formatDescription']}\"")
        imported += 1
    print(f"{imported} fixtures imported into {OUT}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
