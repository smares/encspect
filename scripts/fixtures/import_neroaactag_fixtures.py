#!/usr/bin/env python3
"""Takes the four files that neroAacTag 1.5.4.0 tagged into tests/fixtures/aac/sample-kit (plan 0.4, item 9).

    python3 scripts/fixtures/import_neroaactag_fixtures.py path/to/folder-with-the-four-m4a-files

neroAacEnc 1.5.4.0 encoded the files from the sample kit's synthetic source music-44k-stereo.wav (written from nothing, so the audio carries no copyright), and
neroAacTag 1.5.4.0 on Windows tagged them (the commands are in COMMANDS below). Like import_sample_kit_fixtures.py, whose record format it writes, it copies each
file byte for byte as external-<name>.m4a and writes external-<name>.provenance.json (tool, version, command lines, the file's size and SHA-256) and
external-<name>.expected.json (what ffprobe reads). The provenance also holds LISTINGS: what neroAacTag's own -list-meta, -list-covers and -list-chapters
printed for the file, cleaned of the tool's banner lines and of the contributor's paths. NeroTagsTests holds the parsed fields of the Nero tags box to them.

The contributor's log stays private; nothing is copied from it but the commands and listings below.
"""

import hashlib
import json
import pathlib
import sys

import import_sample_kit_fixtures as kit

SOURCE_NAME = "music-44k-stereo.wav"
SOURCE_SHA = "5272121c7f1c5b652f3f94287cd87cdbe5e2351a299a276d973557974166aa27"

# The listings keep what the tool printed, character for character: on the Windows console it printed "?" for the Cyrillic and Japanese text and the
# Windows-1252 letters of the title in the console's code page (the tags box holds the UTF-8 text, which the tests check).
CHAPTER_META = {"cdec": "ndaudio 1.5.4.0 / -cbr 128000 -lc", "tool": "Nero AAC codec / 1.5.4.0"}
SMPB_SINGLE = " 00000000 00000A40 00000318 000000000006BAA8 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000"
SMPB_THREE = " 00000000 00000A40 000001C8 0000000000142FF8 00000000 00000000 00000000 00000000 00000000 00000000 00000000 00000000"
NO_COVERS = []
ONE_CHAPTER = [{"start": "0:00.000", "end": "0:10.000"}]

FILES = {
    "cover": {
        "name_in_kit": "music-44k-stereo-lc-cbr128-cover.m4a",
        "commands": ["neroAacEnc -lc -cbr 128000 -if music-44k-stereo.wav", "neroAacTag -meta:title=\"Encspect cover test\" -write-nd-covers -add-cover:front:cover.jpg"],
        "selected_for": "a cover in neroAacTag's own box and in the iTunes metadata",
        "listings": {
            "meta": {"cdec": CHAPTER_META["cdec"], "itunsmpb": SMPB_SINGLE, "title": "Encspect cover test", "tool": CHAPTER_META["tool"]},
            "covers": ["front cover #1,image/jpeg"],
            "chapters": [{"start": "0:00.000", "end": "0:10.000", "title": "Encspect cover test"}],
        },
    },
    "unicode": {
        "name_in_kit": "music-44k-stereo-lc-cbr128-unicode.m4a",
        "commands": ["neroAacEnc -lc -cbr 128000 -if music-44k-stereo.wav",
                     "neroAacTag with -meta:title, -meta:artist and -meta:album set to a title of German letters, an artist in Cyrillic and an album in Japanese katakana"],
        "selected_for": "non-ASCII text in neroAacTag's box and in the iTunes metadata",
        "listings": {
            "meta": {"album": "???", "artist": "????", "cdec": CHAPTER_META["cdec"], "itunsmpb": SMPB_SINGLE, "title": "Encspect ─÷³▀ test", "tool": CHAPTER_META["tool"]},
            "covers": NO_COVERS,
            "chapters": [{"start": "0:00.000", "end": "0:10.000", "title": "Encspect ─÷³▀ test"}],
        },
    },
    "userfield": {
        "name_in_kit": "music-44k-stereo-lc-cbr128-userfield.m4a",
        "commands": ["neroAacEnc -lc -cbr 128000 -if music-44k-stereo.wav", "neroAacTag -meta-user:encspect=\"user field test\" -meta:comment=\"Encspect comment\""],
        "selected_for": "a user field and a comment in neroAacTag's box and in the iTunes metadata",
        "listings": {
            "meta": {"cdec": CHAPTER_META["cdec"], "comment": "Encspect comment", "encspect": "user field test", "itunsmpb": SMPB_SINGLE, "tool": CHAPTER_META["tool"]},
            "covers": NO_COVERS,
            "chapters": ONE_CHAPTER,
        },
    },
    "chapters": {
        "name_in_kit": "music-44k-stereo-lc-cbr128-chapters.m4a",
        "commands": ["neroAacEnc -lc -cbr 128000 -if music-44k-stereo.wav (three times; the log does not say how the three encodes were joined)",
                     "neroAacTag -meta:album=\"Encspect chapters\"", "neroAacTag -chapter:1 -meta:title=\"Chapter 1\"", "neroAacTag -chapter:2 -meta:title=\"Chapter 2\"",
                     "neroAacTag -chapter:3 -meta:title=\"Chapter 3\""],
        "selected_for": "three chapters with titles of their own in neroAacTag's box and in the chapter list",
        "listings": {
            "meta": {"album": "Encspect chapters", "cdec": CHAPTER_META["cdec"], "itunsmpb": SMPB_THREE, "tool": CHAPTER_META["tool"]},
            "meta_per_chapter": [{"title": f"Chapter {i}", "track": str(i)} for i in (1, 2, 3)],
            "covers": NO_COVERS,
            "chapters": [{"start": "0:00.000", "end": "0:10.000", "title": "Chapter 1"}, {"start": "0:10.000", "end": "0:20.000", "title": "Chapter 2"},
                         {"start": "0:20.000", "end": "0:30.000", "title": "Chapter 3"}],
        },
    },
}


def main():
    if len(sys.argv) != 2 or sys.argv[1].startswith("--"):
        raise SystemExit(__doc__)
    folder = pathlib.Path(sys.argv[1])
    directory = kit.FIXTURES / "aac" / "sample-kit"
    for key, entry in FILES.items():
        data = (folder / entry["name_in_kit"]).read_bytes()
        stem = f"external-neroaactag-{key}"
        target = directory / f"{stem}.m4a"
        target.write_bytes(data)
        provenance = {
            "description": f"neroAacEnc 1.5.4.0 and neroAacTag 1.5.4.0: {'; '.join(entry['commands'])}, on the synthetic source {SOURCE_NAME}.",
            "encoder": {"tool": "neroAacEnc (Nero AAC Codec) with neroAacTag", "version": "1.5.4.0", "build": "win32", "package_build_date": "Feb 18 2010",
                        "settings": "neroAacEnc -lc -cbr 128000, then neroAacTag", "commands": entry["commands"]},
            "input": {"name": SOURCE_NAME, "sha256": SOURCE_SHA, "generator": "sources/make_sources.py of the sample kit (written from nothing, reproducible byte for byte)"},
            "selected_for": entry["selected_for"],
            "file": {"name_in_kit": entry["name_in_kit"], "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()},
            "listings": {"tool": "neroAacTag 1.5.4.0 -list-meta, -list-covers and -list-chapters", **entry["listings"]},
            "reproducible": False,
            "note": "The encoder stamps the time of the encode into the file; the file is committed as written, and checked by its content.",
            "license": kit.NERO_ACCESS,
        }
        (directory / f"{stem}.provenance.json").write_text(json.dumps(provenance, indent=1, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
        manifest = {"description": "What ffprobe reads of the file (crosscheck_sample_kit_fixtures.py recomputes it and adds MediaInfo's reading).", "ffprobe": kit.probe(target)}
        (directory / f"{stem}.expected.json").write_text(json.dumps(manifest, indent=1, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
        print(f"{stem}: {len(data)} bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
