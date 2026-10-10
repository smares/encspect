#!/usr/bin/env python3
"""Writes tag fixtures with independent tag writers: mutagen and ffmpeg.

The hand-built fixtures from make_tag_fixtures.py share the author's reading of the specs with
the parsers. These files come from tools written by other people, so a parser that agrees with
them has not just agreed with itself. Each <name>.expected.json lists the values handed to the
writer; region offsets are left out because the writer chooses them.

The output is committed. CI does not run this script, because the result depends on the tool
versions; rerun it by hand with the versions below when a fixture needs to change:

    python3 -m venv /tmp/fixture-venv && /tmp/fixture-venv/bin/pip install mutagen==1.47.0
    /tmp/fixture-venv/bin/python scripts/fixtures/make_external_tag_fixtures.py

ffmpeg 6.1 with libmp3lame provides the audio and the ffmpeg-written tags.
"""

import json
import pathlib
import subprocess
import sys

import mutagen
from mutagen.apev2 import APEv2, APEBinaryValue, APEExtValue
from mutagen.id3 import APIC, COMM, ID3, POPM, PRIV, TALB, TDRC, TIT2, TPE1, TRCK, TXXX, UFID, USLT, WOAR, WXXX, Encoding

OUT = pathlib.Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "tags"
MUTAGEN_VERSION = "1.47.0"


def run_ffmpeg(path, extra_args):
    args = [
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=0.2",
        "-ac", "1", "-c:a", "libmp3lame", "-b:a", "64k",
        "-fflags", "+bitexact", "-flags:a", "+bitexact",
    ]
    subprocess.run(args + extra_args + [str(path)], check=True)


def untagged_mp3(path):
    run_ffmpeg(path, ["-id3v2_version", "0", "-write_id3v1", "0", "-write_xing", "0"])


def write_manifest(name, description, regions):
    manifest = {"description": description, "regions": regions, "findings": []}
    text = json.dumps(manifest, indent=2, ensure_ascii=False) + "\n"
    (OUT / f"{name}.expected.json").write_text(text, encoding="utf-8", newline="\n")


def data_region():
    # The audio is a real MP3 stream, so the format parser claims it.
    return {"name": "MPEG Audio"}


def mutagen_id3v23():
    name = "external-mutagen-id3v23"
    path = OUT / f"{name}.mp3"
    untagged_mp3(path)
    tags = ID3()
    tags.add(TIT2(encoding=Encoding.UTF8, text=["Ünïcödé Title"]))
    tags.add(TPE1(encoding=Encoding.LATIN1, text=["Plain Artist"]))
    tags.add(TALB(encoding=Encoding.UTF16, text=["UTF-16 Album"]))
    tags.add(TRCK(encoding=Encoding.LATIN1, text=["3/12"]))
    tags.add(COMM(encoding=Encoding.UTF16, lang="eng", desc="Note", text=["Comment text"]))
    tags.add(TXXX(encoding=Encoding.LATIN1, desc="REPLAYGAIN_TRACK_GAIN", text=["-6.50 dB"]))
    tags.add(APIC(encoding=Encoding.LATIN1, mime="image/png", type=3, desc="Front", data=b"\x89PNG\r\n\x1a\n" + bytes(range(256))))
    tags.save(path, v2_version=3, v1=0)
    write_manifest(
        name,
        f"ID3v2.3 written by mutagen {MUTAGEN_VERSION}. mutagen converts UTF-8 text to UTF-16 for v2.3.",
        [
            {
                "name": "ID3v2",
                "version": "2.3.0",
                "confidence": "High",
                "offset": 0,
                "fields": {
                    "TIT2/Encoding": "UTF-16",
                    "TIT2/Text": "Ünïcödé Title",
                    "TPE1/Text": "Plain Artist",
                    "TALB/Text": "UTF-16 Album",
                    "TRCK/Text": "3/12",
                    "COMM/Language": "eng",
                    "COMM/Description": "Note",
                    "COMM/Text": "Comment text",
                    "TXXX/Description": "REPLAYGAIN_TRACK_GAIN",
                    "TXXX/Text": "-6.50 dB",
                    "APIC/MIME Type": "image/png",
                    "APIC/Picture Type": "3 (Cover (front))",
                    "APIC/Description": "Front",
                    "APIC/Picture Data": "264 bytes",
                },
            },
            data_region(),
        ],
    )


def mutagen_id3v24_with_v1():
    name = "external-mutagen-id3v24-id3v1"
    path = OUT / f"{name}.mp3"
    untagged_mp3(path)
    tags = ID3()
    tags.add(TIT2(encoding=Encoding.UTF8, text=["Song Title"]))
    tags.add(TPE1(encoding=Encoding.UTF8, text=["Artist One", "Artist Two"]))
    tags.add(TALB(encoding=Encoding.UTF16BE, text=["Album Name"]))
    tags.add(TDRC(encoding=Encoding.LATIN1, text=["2024"]))
    tags.add(TRCK(encoding=Encoding.LATIN1, text=["7"]))
    tags.add(USLT(encoding=Encoding.UTF8, lang="eng", desc="", text="Line one\nLine two"))
    tags.add(POPM(email="rater@example.com", rating=128, count=5))
    tags.add(PRIV(owner="com.example", data=b"\x00\x01\x02"))
    tags.add(UFID(owner="http://musicbrainz.org", data=b"0f0e0d0c-0000-1111-2222-333344445555"))
    tags.add(WXXX(encoding=Encoding.UTF8, desc="Home", url="https://example.com/"))
    tags.add(WOAR(url="https://example.com/artist"))
    tags.save(path, v2_version=4, v1=2)
    write_manifest(
        name,
        f"ID3v2.4 plus ID3v1.1 written by mutagen {MUTAGEN_VERSION} (v1=2 forces the ID3v1 tag, which holds only the first artist).",
        [
            {
                "name": "ID3v2",
                "version": "2.4.0",
                "confidence": "High",
                "offset": 0,
                "fields": {
                    "TIT2/Encoding": "UTF-8",
                    "TIT2/Text": "Song Title",
                    "TPE1/Text": ["Artist One", "Artist Two"],
                    "TALB/Encoding": "UTF-16BE",
                    "TALB/Text": "Album Name",
                    "TDRC/Text": "2024",
                    "TRCK/Text": "7",
                    "USLT/Language": "eng",
                    "USLT/Text": "Line one\nLine two",
                    "POPM/Email": "rater@example.com",
                    "POPM/Rating": "128",
                    "POPM/Counter": "5",
                    "PRIV/Owner": "com.example",
                    "PRIV/Data": "3 bytes",
                    "UFID/Owner": "http://musicbrainz.org",
                    "UFID/Identifier": "0f0e0d0c-0000-1111-2222-333344445555",
                    "WXXX/Description": "Home",
                    "WXXX/URL": "https://example.com/",
                    "WOAR/URL": "https://example.com/artist",
                },
            },
            data_region(),
            {
                "name": "ID3v1",
                "version": "1.1",
                "confidence": "Medium",
                "length": 128,
                "fields": {"Title": "Song Title", "Artist": "Artist One", "Album": "Album Name", "Year": "2024", "Track": "7"},
            },
        ],
    )


def mutagen_apev2_with_v1():
    name = "external-mutagen-apev2-id3v1"
    path = OUT / f"{name}.mp3"
    untagged_mp3(path)
    # APEv2.save drops an existing ID3v1 tag, so the APE tag goes first and ID3 appends ID3v1 after it.
    ape = APEv2()
    ape["Title"] = "Ünïcödé APE Title"
    ape["Artist"] = ["First", "Second"]
    ape["Cover Art (Front)"] = APEBinaryValue(b"cover.png\x00\x89PNG\r\n\x1a\n")
    ape["Related"] = APEExtValue("https://example.com/related")
    ape.save(path)
    id3 = ID3()
    id3.add(TIT2(encoding=Encoding.LATIN1, text=["V1 Title"]))
    id3.save(path, v2_version=3, v1=2)
    write_manifest(
        name,
        f"APEv2 written by mutagen {MUTAGEN_VERSION} into a file with ID3v2.3 and ID3v1 tags; mutagen keeps ID3v1 last.",
        [
            {"name": "ID3v2", "version": "2.3.0", "confidence": "High", "offset": 0, "fields": {"TIT2/Text": "V1 Title"}},
            data_region(),
            {
                "name": "APE",
                "version": "2.0",
                "confidence": "High",
                "fields": {
                    "Header/Flags/IsHeader": "true",
                    "Items/Title/Value": "Ünïcödé APE Title",
                    "Items/Artist/Value": ["First", "Second"],
                    "Items/Cover Art (Front)/Flags/Item Type": "Binary",
                    "Items/Cover Art (Front)/Value": "18 bytes",
                    "Items/Related/Flags/Item Type": "External",
                    "Items/Related/Value": "https://example.com/related",
                },
            },
            {"name": "ID3v1", "version": "1.0", "confidence": "Medium", "length": 128, "fields": {"Title": "V1 Title"}},
        ],
    )


def ffmpeg_tags(version):
    name = f"external-ffmpeg-id3v2{version}-id3v1"
    path = OUT / f"{name}.mp3"
    metadata = {"title": "FFmpeg Title", "artist": "FFmpeg Artist", "album": "FFmpeg Album", "track": "4", "date": "2019"}
    args = ["-id3v2_version", str(version), "-write_id3v1", "1", "-write_xing", "0"]
    for key, value in metadata.items():
        args += ["-metadata", f"{key}={value}"]
    run_ffmpeg(path, args)
    year_frame = "TDRC" if version == 4 else "TYER"
    write_manifest(
        name,
        f"ID3v2.{version} and ID3v1 written by the ffmpeg 6.1 MP3 muxer.",
        [
            {
                "name": "ID3v2",
                "version": f"2.{version}.0",
                "confidence": "High",
                "offset": 0,
                "fields": {
                    "TIT2/Text": "FFmpeg Title",
                    "TPE1/Text": "FFmpeg Artist",
                    "TALB/Text": "FFmpeg Album",
                    "TRCK/Text": "4",
                    f"{year_frame}/Text": "2019",
                },
            },
            data_region(),
            {
                "name": "ID3v1",
                "version": "1.1",
                "confidence": "Medium",
                "length": 128,
                "fields": {"Title": "FFmpeg Title", "Artist": "FFmpeg Artist", "Album": "FFmpeg Album", "Year": "2019", "Track": "4"},
            },
        ],
    )


def main():
    if mutagen.version_string != MUTAGEN_VERSION:
        sys.exit(f"mutagen {MUTAGEN_VERSION} is required, found {mutagen.version_string}")
    OUT.mkdir(parents=True, exist_ok=True)
    mutagen_id3v23()
    mutagen_id3v24_with_v1()
    mutagen_apev2_with_v1()
    ffmpeg_tags(3)
    ffmpeg_tags(4)
    print(f"Wrote external fixtures to {OUT}", file=sys.stderr)


if __name__ == "__main__":
    main()
