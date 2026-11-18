#!/usr/bin/env python3
"""Checks the manifests of the WavPack fixtures of versions 1 to 3 (tests/fixtures/wavpack/legacy, make_external_wavpack_legacy_fixtures.py) against independent
tools, as docs/design/wavpack-legacy.md, "Verification and cross-checks", lists them.

- wavpack_legacy (wavpack_tool.py: libwavpack 5.6.0 built from the pinned source with its legacy decoder, ENABLE_LEGACY): whether the file opens, its version,
  channels and the samples a whole pass unpacks, against the manifest's crosscheck.libwavpack_legacy (worked out by wavpack_legacy_model.py); the errors it counts
  (the CRC of version 3) must be 0; and the MD5 of the samples it writes, against what the release's own WVUNPACK wrote (crosscheck.original_decoder, recorded
  when the file was made, with the correction file where there is one, lossless then). Where the library does not read a file (a raw file, which libwavpack does not
  read, and a version 1 mono file, whose samples unpack3.c does not decode) the manifest says so and the run fails if it reads it after all.
- wvunpack of wavpack 5.9.0, which is built without the legacy decoder: it must refuse every file ("this legacy WavPack file is deprecated" or, for a file that
  starts with the WavPack header, "not compatible with this version"), so a change that makes it read them is noticed and the fixtures' notes are reviewed.
- FFmpeg (ffprobe) and MediaInfo: neither reads these files as WavPack. FFmpeg and MediaInfo see a WAV file whose data chunk is the rest of the file; FFmpeg
  refuses a raw file. The run fails if either names a file WavPack, so the entry (the design's "What the files show") is reviewed.

Every skipped check prints its reason. Disagreements fail the run (exit status 1).

    python3 scripts/fixtures/crosscheck_wavpack_legacy_fixtures.py [--any-version] [name ...]

The tools' versions are asserted (tool_versions.py: Ubuntu 26.04's wvunpack and ffprobe, MediaInfo 26.01) unless --any-version is given, which runs the checks with whatever
is installed and skips what is not.
"""

import hashlib
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile

import mediainfo_tool as mi
import tool_versions
import wavpack_tool

ROOT = pathlib.Path(__file__).resolve().parents[2]
DIRECTORY = ROOT / "tests" / "fixtures" / "wavpack" / "legacy"
SKIPS = []


def skip(name, what, why):
    SKIPS.append(f"{name}: {what} skipped, {why}")


def run_library(binary, path, work, with_correction=True):
    """The pairs wavpack_legacy prints, and the MD5 of the samples it wrote."""
    wvc = path.with_name(path.stem + "-wvc.wvc") if with_correction else path.with_name("none")
    out = pathlib.Path(work) / "samples.pcm"
    out.unlink(missing_ok=True)
    result = subprocess.run([str(binary), str(path), str(out), *([str(wvc)] if wvc.exists() else [])], capture_output=True, text=True, timeout=300)
    figures = {}
    for line in result.stdout.splitlines():
        key, _, value = line.partition(" ")
        figures[key] = value
    return figures, hashlib.md5(out.read_bytes()).hexdigest() if out.exists() and out.stat().st_size else None


def check_library(name, path, manifest, binary, work, problems):
    if "libwavpack_legacy" not in manifest.get("crosscheck", {}):
        skip(name, "libwavpack's legacy decoder", "the hand-built file's damage leaves no answer to state")
        return
    expected = manifest["crosscheck"]["libwavpack_legacy"]
    figures, digest = run_library(binary, path, work)
    opens = "open_error" not in figures
    if opens != expected["opens"]:
        problems.append(f"libwavpack's legacy decoder {'opens' if opens else 'refuses (' + figures.get('open_error', '') + ')'} the file, the manifest says it {'opens' if expected['opens'] else 'does not'}")
        return
    if not opens or "samples_unpacked" not in expected:
        # A hand-built file states only whether the library opens it: what it unpacks of damaged audio is not worked out.
        return
    if int(figures["version"]) != expected["version"] or int(figures["channels"]) != expected["channels"]:
        problems.append(f"libwavpack reads version {figures['version']} with {figures['channels']} channels, the manifest says {expected['version']} and {expected['channels']}")
    if int(figures["decode_samples"]) != expected["samples_unpacked"]:
        problems.append(f"libwavpack unpacks {figures['decode_samples']} samples, the manifest says {expected['samples_unpacked']}")
    if int(figures.get("decode_errors", "0")) != 0:
        problems.append(f"libwavpack counts {figures['decode_errors']} errors")
    original = manifest["crosscheck"]["original_decoder"]
    wvc = path.with_name(path.stem + "-wvc.wvc").exists()
    wanted = (original["with_correction"] if "with_correction" in original else original)["pcm_md5"] if wvc or "with_correction" not in original else None
    if expected["samples_unpacked"] and digest != wanted:
        problems.append(f"libwavpack's samples have the MD5 {digest}, the original decoder's {wanted}")
    if wvc:
        # The main file alone is the lossy file; the samples are those the release's WVUNPACK wrote without the correction file.
        _, alone = run_library(binary, path, work, with_correction=False)
        if alone != original["without_correction"]["pcm_md5"]:
            problems.append(f"libwavpack's samples of the main file alone have the MD5 {alone}, the original decoder's {original['without_correction']['pcm_md5']}")


def check_wvunpack(name, path, problems):
    if shutil.which("wvunpack") is None:
        skip(name, "wvunpack", "wvunpack is not installed")
        return
    result = subprocess.run(["wvunpack", "-v", "-q", str(path)], capture_output=True, text=True, timeout=300)
    text = result.stdout + result.stderr
    if result.returncode == 0:
        problems.append("wvunpack 5.9.0 reads the file; it is built without the legacy decoder and must refuse it")
    elif "legacy WavPack file is deprecated" not in text and "not compatible" not in text and "not a valid" not in text.lower() and "can't" not in text.lower():
        problems.append(f"wvunpack refuses the file with an unexpected message: {text.strip()[-120:]}")


def check_ffmpeg(name, path, manifest, problems):
    if shutil.which("ffprobe") is None:
        skip(name, "ffprobe", "ffprobe is not installed")
        return
    result = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=format_name:stream=codec_name", "-of", "json", str(path)], capture_output=True, text=True, timeout=300)
    try:
        facts = json.loads(result.stdout)
    except json.JSONDecodeError:
        facts = {}
    format_name = facts.get("format", {}).get("format_name", "")
    codecs = [s.get("codec_name") for s in facts.get("streams", [])]
    if format_name == "wv" or "wavpack" in codecs:
        problems.append("FFmpeg reads the file as WavPack: review the design's \"What the files show\" and this script")
    raw = "wav" not in manifest
    if not raw and format_name != "wav":
        problems.append(f"FFmpeg reads the file as {format_name or 'nothing'}, the entry says a WAV file")
    if raw and result.returncode == 0 and format_name:
        problems.append(f"FFmpeg reads a raw file as {format_name}; it refused it before")


def check_mediainfo(name, path, manifest, problems):
    if shutil.which("mediainfo") is None:
        skip(name, "MediaInfo", "mediainfo is not installed")
        return
    result = subprocess.run(["mediainfo", "--Output=General;%Format%", str(path)], capture_output=True, text=True, timeout=300)
    answer = result.stdout.strip()
    if answer == "WavPack":
        problems.append("MediaInfo reads the file as WavPack: review the design's \"What the files show\" and this script")
    raw = "wav" not in manifest
    if not raw and answer != "Wave":
        problems.append(f"MediaInfo reads the file as {answer or 'nothing'}, the entry says a WAV file")
    if raw and answer:
        problems.append(f"MediaInfo reads a raw file as {answer}; it read none before")


def main():
    chosen = [a for a in sys.argv[1:] if not a.startswith("--")]
    if "--any-version" not in sys.argv:
        mi.require()
        tool_versions.require("ffprobe", "wvunpack")
    binary = wavpack_tool.build_legacy(pathlib.Path(tempfile.gettempdir()) / "wavpack-legacy-tools")
    failed = 0
    count = 0
    with tempfile.TemporaryDirectory() as work:
        for manifest_path in sorted(DIRECTORY.glob("*.expected.json")):
            name = manifest_path.name[:-len(".expected.json")]
            if chosen and name not in chosen:
                continue
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            if manifest.get("kind") == "correction":
                continue
            path = next(p for p in DIRECTORY.glob(name + ".*") if p.suffix in (".wv", ".wvc"))
            problems = []
            count += 1
            check_library(name, path, manifest, binary, work, problems)
            check_wvunpack(name, path, problems)
            if name.startswith("external-"):
                check_ffmpeg(name, path, manifest, problems)
                check_mediainfo(name, path, manifest, problems)
            for problem in problems:
                print(f"{name}: {problem}")
            failed += bool(problems)
    for line in sorted(set(SKIPS)):
        print(line)
    print(f"{count} fixtures checked, {failed} with disagreements")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
