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
import struct
import subprocess
import sys

TOOLS = {"wmf-sdk": "the Windows Media Format SDK writer (wmvcore.dll)", "media-foundation": "Media Foundation's transcoder (Windows.Media.Transcoding, MediaEncodingProfile.CreateWma)"}
ROOT = pathlib.Path(__file__).resolve().parent
OUT = ROOT.parents[1] / "tests" / "fixtures" / "asf"
LARGE = 600_000
AUDIO_GUID = bytes.fromhex("409e69f84d5bcf11a8fd00805f5c442b")
CODECS = {0x0161: "wma92", 0x0162: "wma10pro", 0x0163: "wma92lossless", 0x000A: "wmavoice"}


def stream_format(data):
    """The WAVEFORMATEX of the first audio stream of an ASF file: tag, channels, rate, bytes per second, bits."""
    at = data.find(AUDIO_GUID)
    if at < 0 or len(data) < at + 70:
        raise SystemExit("no audio stream in the file")
    tag, channels, rate, average, _, bits = struct.unpack_from("<HHIIHH", data, at + 54)
    return tag, channels, rate, average, bits


EXTENDED_CONTENT = bytes.fromhex("40a4d0d207e3d21197f000a0c95ea850")


def header_is_vbr(data):
    """The IsVBR attribute of the Extended Content Description Object, as the file itself states it; None where the
    file has none. The writer stamps it, so it is evidence about the file, unlike the description the run writes."""
    at = data.find(EXTENDED_CONTENT)
    if at < 0 or len(data) < at + 26:
        return None
    size = struct.unpack_from("<Q", data, at + 16)[0]
    count = struct.unpack_from("<H", data, at + 24)[0]
    position = at + 26
    for _ in range(count):
        if position + 2 > min(len(data), at + size):
            return None
        name_length = struct.unpack_from("<H", data, position)[0]
        name = data[position + 2:position + 2 + name_length].decode("utf-16-le", "replace").rstrip("\0")
        kind, length = struct.unpack_from("<HH", data, position + 2 + name_length)
        value = data[position + 6 + name_length:position + 6 + name_length + length]
        if name == "IsVBR" and kind in (2, 3, 5):
            return int.from_bytes(value, "little") != 0
        position += 6 + name_length + length
    return None


def plural(count, noun):
    return f"{count} {noun}" if count == 1 else f"{count} {noun}s"


def describe(entry, actual):
    tool = TOOLS.get(entry.get("encoderPath", "wmf-sdk"), "Windows")
    text = f"Encoded by {tool} as {entry['codecName']}, {entry['mode'].upper()}, \"{entry['formatDescription']}\"."
    if entry.get("encoderPath") == "media-foundation":
        layout = {1: "mono", 2: "stereo", 6: "5.1"}.get(actual["channels"], f"{actual['channels']} channels")
        text += f" The file is {layout} at {actual['bytes_per_second'] * 8 // 1000} kbit/s, as its header states; the request was {plural(entry['requestedChannels'], 'channel')} at {entry['requestedBitrate'] // 1000} kbit/s."
    return text


def actual_name(entry, data):
    """What the file is, from its own header: a request can come out as another format (Media
    Foundation gave stereo for a mono request and capped Professional at 256 kbit/s), and the
    name must not claim what the file is not."""
    tag, channels, rate, average, bits = stream_format(data)
    layout = {1: "mono", 2: "stereo", 6: "5.1"}.get(channels, f"{channels}ch")
    parts = [CODECS[tag], entry["mode"], layout, f"{round(rate / 1000)}k"]
    if tag == 0x0163:
        parts = [CODECS[tag], layout, f"{round(rate / 1000)}k", str(bits)]
    elif entry["mode"] == "vbr":
        parts.append(f"q{entry['requestedQuality']}")
    elif entry.get("encoderPath") == "media-foundation":
        parts.append(str(round(average * 8 / 1000)))
    else:
        parts.append(str(round(entry["formatBitrate"] / 1000)))
    name = "-".join(parts)
    if entry["mode"] == "cbr" and entry.get("encoderPath", "wmf-sdk") == "wmf-sdk" and tag != 0x000A:
        name += "-sdk"
    if entry.get("signal") not in (None, "tones", "quiet"):
        name += "-" + entry["signal"]
    if entry.get("title"):
        name += "-tagged"
    return name, {"format_tag": tag, "channels": channels, "rate": rate, "bytes_per_second": average, "bits": bits}


def tags_match(path, entry):
    """The file's own title and author, read by ffprobe, against what the run wrote; the run's
    read-back (tagsVerified) must also say so."""
    if not entry.get("tagsVerified"):
        return False
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format_tags", "-of", "json", str(path)],
        capture_output=True, text=True, check=False)
    try:
        tags = {k.lower(): v for k, v in json.loads(probe.stdout).get("format", {}).get("tags", {}).items()}
    except ValueError:
        return False
    if tags.get("title") != entry["title"]:
        return False
    return not entry.get("author") or tags.get("artist", tags.get("author")) == entry["author"]


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

    # Refuse an artifact whose encodes are not what was asked for: a constant bitrate request that
    # got a variable bitrate format, or two encodes that wrote the same file.
    seen = {}
    for entry in encodes:
        if entry.get("status") != "ok":
            continue
        variable = "VBR" in entry["formatDescription"]
        if variable != (entry["mode"] == "vbr"):
            raise SystemExit(f"{entry['name']}: a {entry['mode']} request was encoded as \"{entry['formatDescription']}\".")
        if entry["sha256"] in seen:
            raise SystemExit(f"{entry['name']} and {seen[entry['sha256']]} are the same file.")
        seen[entry["sha256"]] = entry["name"]
        stated = header_is_vbr((artifact / entry["file"]).read_bytes())
        if stated is not None and stated != (entry["mode"] == "vbr"):
            raise SystemExit(f"{entry['name']}: a {entry['mode']} request, but the file's IsVBR attribute says {int(stated)}.")

    imported = 0
    for entry in encodes:
        requested = entry["name"]
        name = requested
        if entry.get("status") != "ok":
            print(f"{name}: skipped, {entry.get('status')}")
            continue
        if "MISMATCH" in (entry.get("log") or ""):
            print(f"{name}: skipped, a setting did not read back as it was set")
            continue
        source = artifact / entry["file"]
        data = source.read_bytes()
        if entry.get("title") and not tags_match(source, entry):
            print(f"{name}: skipped, the tags in the file are not the ones written ({entry.get('title')!r}, {entry.get('author')!r}) or the run did not verify them")
            continue
        name, actual = actual_name(entry, data)
        if name != requested:
            print(f"{requested}: named {name}, which is what the file is")
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
            "description": describe(entry, actual),
            "encoder": {
                "tool": TOOLS.get(entry.get("encoderPath", "wmf-sdk"), "Windows"),
                "path": entry.get("encoderPath", "wmf-sdk"),
                "codec": entry["codecName"],
                "format_index": entry.get("formatIndex"),
                "achieved_bitrate": entry.get("achievedBitrate"),
                "format_description": entry["formatDescription"],
                "format_tag": entry["formatTag"],
                "mode": entry["mode"],
                "requested": {k: entry[k] for k in ("requestedChannels", "requestedRates", "requestedFormatBits", "requestedQuality", "requestedBitrate") if entry.get(k) not in (None, 0, [])},
                "format_channels": entry["formatChannels"],
                "format_rate": entry["formatRate"],
                "format_bits": entry["formatBits"],
                # Media Foundation takes a bitrate, not a format: what it wrote is the header's, and the request is under "requested".
                "format_bitrate": actual["bytes_per_second"] * 8 if entry.get("encoderPath") == "media-foundation" else entry["formatBitrate"],
                "config_bitrate": entry.get("configBitrate"),
                "buffer_window_ms": entry.get("bufferWindow"),
                "title": entry.get("title"),
                "author": entry.get("author"),
            },
            "input": {
                "signal": entry["signal"],
                # The PCM the encoder was fed: for Media Foundation the request's, which the file need not keep.
                "channels": entry["requestedChannels"] if entry.get("encoderPath") == "media-foundation" else entry["formatChannels"],
                "rate": entry["requestedRates"][0] if entry.get("encoderPath") == "media-foundation" else entry["formatRate"],
                "bits": entry["inputBits"],
                "seconds": entry["seconds"],
                "frames": entry["frames"],
                "type": entry["inputType"],
                "generator": "wma_signals.py",
                # The PCM file the run wrote with wma_signals.py's arguments: the generator makes the same bytes again.
                **({"pcm_sha256": hashlib.sha256((artifact / entry["pcm"]).read_bytes()).hexdigest()} if entry.get("pcm") and (artifact / entry["pcm"]).exists() else {}),
            },
            "runner": {
                "image_os": environment.get("runnerImageOS"),
                "image_version": environment.get("runnerImageVersion"),
                "windows": environment.get("windowsCaption"),
                "windows_version": environment.get("windowsVersion"),
                "architecture": environment.get("architecture"),
                "wmvcore_version": environment.get("wmvcoreVersion"),
            },
            "requested_name": requested,
            "actual": actual,
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
