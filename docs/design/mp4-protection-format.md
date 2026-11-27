# iTunes and Audible protection in MP4: format description

**Status:** written on 2026-11-27 by a Claude agent (Opus), the reading half of the clean-room split for MP4 protection (class C-1 of the copyleft audit, [plan.md](../plan.md) 0.5 item 15). The agent read the LGPL sources listed under [Sources](#sources) and Encspect's current protection code, and ran the reference tools as black boxes on the committed fixtures. It contains no code. Whoever rewrites the analyzer's iTunes and Audible paths must not read the LGPL sources, nor the Encspect regions that [Implementer's sources](#implementers-sources) names; ffprobe, MediaInfo and MP4Box serve as black-box cross-checks only.

This is the description in the form of [truehd-format.md](truehd-format.md) and [wma-voice-format.md](wma-voice-format.md). It states the facts of three structures (the iTunes protected entries `drms` and `drmi`, Audible's entry `aavd`, and the `adrm` box) as each source reads them, where the sources disagree, and what none of them settles. What the analyzer reads from the user's side is in [mp4.md](../formats/mp4.md), "Protected tracks". Neither Apple nor Audible has published a specification of these structures, and none was found.

## Sources

**Read (LGPL):**

| Source | Version | File and SHA-256 | License | What was read |
| --- | --- | --- | --- | --- |
| FFmpeg | 6.1.1, the release `scripts/fixtures/ffmpeg_sources.py` pins (archive `8684f4b0…f968`) | `libavformat/mov.c` (`ddf6e4bf…1cfd`) | LGPL-2.1 or later | the `adrm` reader, how a sample entry and the boxes inside it are read, the `frma` and `schm` readers, the box walker's handling of a reader that reads past its box, the `aaxc` brand and the decryption of samples |
| FFmpeg | 8.0.1, the release CI uses | `libavformat/mov.c` (`d3af210b…935a`, from the `n8.0.1` tag) | LGPL-2.1 or later | the `adrm` and `frma` readers only, compared with 6.1.1 |
| GStreamer | 1.24.12 (gst-plugins-good) | `gst/isomp4/qtdemux.c` (`8e326bed…5dc4`), `gst/isomp4/qtdemux_types.c` (`e1e461b9…bc19`) | LGPL-2 or later | the `aavd` and `adrm` handling, the sample entry reading, the rejection of `drms` and `drmi`, the box names table |
| VLC | 3.0.21 | `modules/demux/mp4/libmp4.c` (`b9bff91e…dec9`), `libmp4.h` (`09377951…7e7f`) | LGPL-2.1 or later | the `drms` and `drmi` entries, the reading of `sinf`, `schi` and the four atoms, their four-character codes |

**Read (permissive):** MediaInfoLib v26.01, `Source/MediaInfo/Multiple/File_Mpeg4_Elements.cpp` (`26de2881…3e8d`), BSD-2-Clause: the sample entry codes it names as encrypted, and its readers of `sinf`, `frma`, `schi` and `schm`. It reads no `drmi`, `aavd` or `adrm`. Where it agrees with an LGPL source, it is cited below as the second source; the implementer may read it.

**Specifications (not read for this text, cited for the implementer):** ISO/IEC 14496-12, 8.12 (the protection scheme information box `sinf` and its `frma`, `schm` and `schi` boxes, and how a protected sample entry replaces the original entry's code); Apple's QuickTime File Format, "Sound sample descriptions" (versions 0, 1 and 2 of the sound sample entry).

**Tools run as black boxes:** ffprobe 6.1.1 (Ubuntu build), MediaInfo 24.01 (command line), MP4Box from GPAC 2.2.1 (`-info` and `-diso`). GStreamer and VLC are not installed and were not run.

**Test files:** the three committed fixtures, all hand-built by `scripts/fixtures/make_protection_fixtures.py` (no real protected file was available; the FFmpeg sample archive holds none of these kinds):

| File | SHA-256 | What it is |
| --- | --- | --- |
| `tests/fixtures/mp4/itunes-drms.m4a` | `4dd6d18b…df8a` | brand `M4P `; a `drms` entry of AAC LC (version 0 sound entry, stereo, 44.1 kHz) with `esds` and `sinf` (`frma` = `mp4a`, `schi` with `user`, `key `, `iviv` of 16 bytes each and `priv` of 60 bytes; no `schm`) |
| `tests/fixtures/mp4/audible-aavd.m4a` | `66c0a35d…57a1` | brand `aax `; an `aavd` entry of AAC LC with `esds` and an `adrm` box of 88 payload bytes |
| `tests/fixtures/mp4/adrm-short.m4a` | `2d9608ac…bb93` | brand `M4A `; the same `aavd` entry with an `adrm` box of 40 payload bytes |

Three mutations of these, made in the session's scratch folder and not committed, were also run through the tools: the `aavd` fixture with `adrm` renamed to `free`, the same with the entry code `aavd` changed to `mp4a`, and the `drms` fixture with `sinf` renamed to `free`.

## Conventions

- Offsets are in bytes. "Payload" is the content of a box after its 8-byte header (size and type), or 16 bytes with a 64-bit size.
- Four-character codes are written in backticks with their trailing spaces (`key ` is the four bytes `6B 65 79 20`).
- "The sources" means the four source trees above; "the tools" means the three black boxes.

## Protected sample entries in general

ISO/IEC 14496-12 8.12 describes the mechanism the iTunes entries follow: a protected track keeps its original sample entry, its code is replaced by one that marks it as protected, and a `sinf` box appended to the entry's boxes names the original code in `frma`. Every source reads `drms`, `drmi` and `aavd` with the layout of the entry they replace; none of them reads a field that the original entry does not have, outside the boxes described below.

## The `drms` entry

**What it is:** the audio sample entry of a track protected by iTunes. Its code `drms` replaces `mp4a` (the only original format the sources and fixtures show).

**Layout:** that of an audio sample entry: the 8 bytes of the sample entry (6 reserved, the 2-byte data reference index), the sound sample description (version 0, 1 or 2, as in QTFF), then boxes. In the fixture the boxes are `esds` and then `sinf`.

| Box | Inside | Content |
| --- | --- | --- |
| `esds` | `drms` | the original entry's elementary stream descriptor, unchanged (AAC object type 0x40, audio object type 2 in the fixture) |
| `sinf` | `drms` | the protection scheme information (14496-12 8.12.1), holding the boxes below |
| `frma` | `sinf` | 4 bytes: the original format's code, `mp4a` (14496-12 8.12.2) |
| `schm` | `sinf` | the scheme type box (14496-12 8.12.5); no source names the scheme type that iTunes writes, and the fixture has none |
| `schi` | `sinf` | the scheme information (14496-12 8.12.6), a container of the four atoms below |
| `user`, `key `, `iviv`, `priv` | `schi` | the DRM atoms of iTunes protection; no source gives their sizes or contents |

**How each source reads it:**

- **VLC:** reads `drms` with the audio sample entry layout (sound description versions 0, 1 and 2), wherever it appears, then stops: it reads no box inside the entry (so neither `esds` nor `sinf`) and warns that protected streams are not supported. Its box table also lists `sinf` and `schi` as containers, and `user`, `key `, `iviv` and `priv` as the four atoms it skips whole with the same warning; these four are accepted under any parent, not only `schi`. Its header defines `name` beside them, but registers `name` only under `udta` and iTunes metadata, not as a DRM atom.
- **MediaInfo** (second source for the entry): reads a `drms` entry as an audio sample entry and the boxes inside it, fills the codec ID from the code, the `esds` and the `frma`, and reports the audio stream's encryption as "iTunes". It reads `frma` as the 4-byte original format, `schm` as scheme type, scheme version and, when flag bit 0 is set, a URI, and skips all of `schi` as scheme-specific data without looking inside.
- **FFmpeg:** knows no code `drms` or `drmi`; it reads the entry as an audio entry because the track's handler is `soun`, then reads the boxes inside it. The codec comes from `esds`. Its `frma` reader takes the 4-byte format, but for an entry other than `enca` and `encv` only compares it with the entry's code and warns when they differ; it never takes the codec from it here. It reads `schm`'s scheme type, and walks `schi` as a container whose four atoms it has no reader for, so it skips them.
- **GStreamer:** rejects a track whose sample entry is `drms` or `drmi` before reading the entry, with a decryption error; the track is not exposed.

**Where the sources disagree:** whether the boxes inside `drms` are read at all (MediaInfo and FFmpeg read them, VLC stops after the sound description, GStreamer reads nothing).

## The `drmi` entry

**What it is:** the video sample entry of a track protected by iTunes, named by VLC and GStreamer.

**Layout:** that of a visual sample entry (the 8 bytes of the sample entry, the fields of the visual sample description up to depth and color table, then boxes), with a `sinf` box inside as for `drms`, by 14496-12 8.12; no source reads the `sinf` of a `drmi` entry specifically.

**How each source reads it:**

- **VLC:** reads `drmi` only as an entry of `stsd`, with the visual sample entry layout up to the color table, then stops with the same warning; it reads no box inside it.
- **GStreamer:** rejects it, as `drms`.
- **FFmpeg:** no code of its own; read as a video entry by the track's handler, and its boxes read.
- **MediaInfo:** does not name `drmi`. In its video branch it reports encryption "iTunes" for a video entry whose code is `drms`, and "Encrypted" for `encv`.

**Where the sources disagree:** the video entry's code. VLC and GStreamer name `drmi`; MediaInfo's video branch names `drms`. No fixture or real file has a protected video track, so neither is confirmed.

## The atoms of `schi`

`user`, `key ` (with a trailing space), `iviv` and `priv`, in that order in the fixture, each a plain box (size, type, payload). No source reads a field of them: VLC skips each whole, FFmpeg skips them as unknown boxes, MediaInfo skips the whole `schi`, GStreamer never reaches them. The fixture's payload sizes (16, 16, 16 and 60 bytes) are the fixture writer's choice, not facts of the format.

The names suggest a user identifier, a key index, an initialization vector and a private key, but no source states this, so the analyzer should not.

## The `aavd` entry

**What it is:** the audio sample entry of an Audible AAX track. GStreamer's box table names it "AAX encrypted audio".

**Layout:** that of `mp4a`: the 8 bytes of the sample entry, the sound sample description, and boxes: an `esds` box of the original AAC and an `adrm` box. In the fixture `esds` comes first.

**How each source reads it:**

- **GStreamer:** reads `aavd` with the same reader as `mp4a`, `alac`, `fLaC` and `Opus`: the sound description version is the 2 bytes at entry offset 16, and the boxes start at entry offset 36 for version 0, 52 for version 1 and 72 for version 2 (the offsets the QTFF versions give); for another version it reads no boxes. It looks for boxes inside the entry only when the entry is at least 40 bytes long. An `aavd` entry in a track whose handler is not `soun` is a corrupt file. The `adrm` box must be a direct child of the entry; without it the track is a corrupt file. It takes the codec from the entry's `esds` and treats the track as `mp4a`, marked protected, and attaches the whole `adrm` box (header included, as long as its size field says) to every sample as opaque protection data, without reading a field of it.
- **FFmpeg:** knows no code `aavd`; it reads the entry as an audio entry by the handler and the boxes inside it, takes the codec from `esds`, and reads `adrm` (below).
- **VLC** and **MediaInfo** know neither `aavd` nor `adrm`; MediaInfo reads the entry as audio and its `esds`, and reports no encryption.

**Where the sources disagree:** GStreamer requires `adrm` inside `aavd` and `aavd` in a sound track; FFmpeg checks neither, and reads an `adrm` box wherever its box walk meets one (see the mutation under [Black-box behavior](#black-box-behavior)).

## The `adrm` box

**What it is:** Audible's DRM data for the file. GStreamer's box table names it "AAX DRM key data".

**Layout,** as FFmpeg reads it from the start of the payload (no version or flags are read first):

| Payload offset | Size | Content | What FFmpeg does with it |
| --- | --- | --- | --- |
| 0 | 8 | not described | skips it |
| 8 | 56 | an encrypted blob (ffprobe's error message calls it the "drm blob") | decrypts it with AES-128 in CBC mode, under a key and an initialization vector it derives with SHA-1 from a fixed key and the account's 4 activation bytes; the decrypted blob holds the activation bytes, which it checks, and the material of the key and initialization vector that decrypt the samples |
| 64 | 4 | not described | skips it |
| 68 | 20 | a SHA-1 digest (ffprobe's log calls it the "file checksum") | prints it in hexadecimal at the information level; with activation bytes given, recomputes it from the derived key and initialization vector and rejects the file if it differs, before decrypting the blob |

Total: 88 bytes. Where inside the decrypted blob each value lies is left out on purpose: the analyzer does not decrypt, and holds no key.

**What FFmpeg does around it:**

- It reads the 88 bytes whatever the box's size says. A shorter box is read on into the bytes after it; FFmpeg's box walker then warns that the reader read past the end of the box and returns to the box's end. A longer box's remaining bytes are skipped. There is no minimum size and no finding for a short box.
- Without activation bytes it prints the checksum, warns that they are missing and goes on, so ffprobe still lists the stream.
- FFmpeg 8.0.1 reads the same 88 bytes the same way, with one change: if the file ends before the 20 checksum bytes are read, the reader fails instead of going on.
- Once it has read an `adrm` box, FFmpeg decrypts the samples of the file's tracks: each sample's whole 16-byte blocks with AES-128 in CBC mode, restarting from the file's initialization vector at every sample; the bytes after the last whole block are not encrypted.
- A file whose `ftyp` major brand is `aaxc` is decrypted with a 16-byte key and a 16-byte initialization vector given from outside the file; no `adrm` box is read for it.

**GStreamer** keeps the box whole and reads none of its fields; **VLC** and **MediaInfo** do not know it.

## Black-box behavior

**ffprobe 6.1.1:**

- `itunes-drms.m4a`: one audio stream, AAC LC, codec tag `drms`, 44,100 Hz, stereo, with the warning that the `frma` atom of `mp4a` is ignored because the stream format is `drms`. (The fixture writer's note that ffmpeg 6.1 lists no audio stream for a `drms` entry does not hold for 6.1.1.)
- `audible-aavd.m4a`: AAC LC, codec tag `aavd`; it logs the file checksum `c8c9…dadb` (payload bytes 68 to 87) and warns that the activation bytes option is missing.
- `adrm-short.m4a`: the same stream; the logged checksum comes from the bytes after the box, and it warns that it read past the end of the `adrm` atom by 48 bytes (88 less the 40 bytes of payload).
- The `aavd` mutation without `adrm`: AAC, codec tag `aavd`, no Audible messages. The mutation whose entry is `mp4a` with an `adrm` box inside: the same Audible messages as `audible-aavd.m4a`, so FFmpeg reads `adrm` in any entry. The `drms` mutation without `sinf`: AAC, codec tag `drms`, no warning.
- AAC decoding of the pseudo-random samples fails in every file, as expected.

**MediaInfo 24.01:**

- `drms`: Format AAC LC, codec ID `drms-40 / mp4a-2` (entry code, object type, the `frma` format and the audio object type), Encryption "iTunes". Without `sinf`: codec ID `drms-40-2`, Encryption still "iTunes", which therefore depends on the entry code alone.
- `aavd` (both fixtures): Format AAC LC, codec ID `aavd-40-2`, no Encryption field, and the error "Missing ID_END": MediaInfo parses the encrypted samples as AAC.

**MP4Box (GPAC 2.2.1):**

- Warns "Unknown box type" for `drms` and `aavd` in `stsd`, but reads both as audio sample entries (version 0, 2 channels, 16 bits, 44,100 Hz) and the boxes inside them; `-info` shows the media type `soun:drms` or `soun:aavd`, and for `aavd` the RFC 6381 codec `aavd`.
- Knows `sinf`, `frma` (data format `mp4a`) and `schi` from 14496-12; lists `user`, `key `, `iviv`, `priv` and `adrm` as unknown boxes with their raw bytes.

## Not determined

- The sizes and contents of `user`, `key `, `iviv` and `priv`; whether a real `schi` carries other atoms; and whether a real `sinf` carries `schm`, with which scheme type. No source and no real file settles them.
- The video entry's code, `drmi` (VLC, GStreamer) or `drms` (MediaInfo's video branch).
- What the first 8 bytes and the 4 bytes after the blob of `adrm` are; whether real `adrm` boxes are longer than 88 bytes, and what follows.
- A minimum size of `adrm`: no source rejects a short box, so a finding for one is the analyzer's own rule, from the 88 bytes the layout needs.
- Whether an `aaxc` file carries an `aavd` entry or an `adrm` box: FFmpeg needs neither for it.
- More than one `adrm` box, or one outside `aavd`: FFmpeg reads each it meets, GStreamer only the first child of the entry; no real file shows either.

## What the rewrite keeps

The analyzer's reports already follow these facts, so the goldens of the three fixtures stay equal, with one exception:

- `drms`, `drmi` and `aavd` are protected whatever they contain; the codec is described from `esds`; `aavd` is read as `mp4a` (GStreamer; FFmpeg by its handler).
- The four atoms of `schi` are shown by size, with their payload as data that is not read.
- `adrm` is split into the four parts of the table, under the node names the goldens hold ("Leading Bytes", "DRM Blob", "Skipped Bytes", "File Checksum"); bytes past 88 are data that is not read; a payload under 88 bytes is the warning `mp4.adrm-short` with its fields not read.
- The exception: the message of `mp4.adrm-short` names the layout, not a reader ("fewer than the 88 bytes of its layout", keeping "fewer than the 88", which the fixture's expected results test). The golden of `adrm-short.m4a` differs in that message only.
- Descriptions and the topic cite this description, MediaInfo and the black-box behavior above, never the LGPL files.

## Implementer's sources

**Safe:**

- This description.
- ISO/IEC 14496-12 (8.12) and Apple's QTFF.
- MediaInfoLib's `File_Mpeg4_Elements.cpp` (BSD-2-Clause).
- The ISO BMFF parts of the MP4 parser: every file of `src/Encspect.Formats.Mp4` but the regions below, including the Common Encryption parts of `Mp4Parser.Protection.cs` (scheme, `tenc`, `pssh`, `senc`, `saiz`, `saio`, the `seig` group, the protection decision) and the names of `Mp4Parser.SampleTable.cs` 151-153, which the goldens show.
- The fixtures and their golden reports, expected results and decode records (`tests/fixtures/mp4/*`), and `make_protection_fixtures.py` outside the regions below (its box helpers and the Common Encryption builders).
- ffprobe, MediaInfo and MP4Box as black boxes.

**Never read:**

- FFmpeg's `libavformat/mov.c`, GStreamer's `gst/isomp4/qtdemux.c` (and `qtdemux_types.c`), and VLC's `modules/demux/mp4/libmp4.c` (and `libmp4.h`), in any version, copy or mirror, including the copies in `.cache/mp4-protection-src`.
- The reading half's notes and scratch files of this session (the folder `mp4prot` in its scratchpad), and rows M1 to M3 of the copyleft audit's inventory, which restate the LGPL facts.
- The tainted regions of Encspect, as the audit's rows M1 to M3 list them, with today's line numbers on main (`19a0046a0`) and two regions the audit does not list (marked):
  - `src/Encspect.Formats.Mp4/Mp4Parser.Protection.cs`: the file's opening comment where it names iTunes and Audible (lines 9 to 11), the constant `AudibleDrmLength` and its comment (14 to 15), `ItunesProtectionBox` with its comment (389 to 399) and the `adrm` reader `AudibleDrm` with its comment and finding (401 to 421).
  - `src/Encspect.Formats.Mp4/Mp4Parser.Movie.cs` 114 to 118 (not in the audit): the cases that send `adrm` inside `aavd` and the four atoms inside `sinf` to those readers.
  - `src/Encspect.Formats.Mp4/Mp4Parser.State.cs` 244 to 246 (the audit's 245): `EffectiveFormat`, which reads `aavd` as `mp4a`, and its comment.
  - `src/Encspect.Formats.Mp4/Explain.cs` 178 to 182 (`ItunesProtectionData` and the four `Adrm` descriptions) and 444 to 450 (the box descriptions of `adrm`, the four atoms, `drms`, `drmi` and `aavd`).
  - The topic [itunes-and-audible-protection.md](../../src/Encspect.Host/Topics/itunes-and-audible-protection.md), whole.
  - `scripts/fixtures/make_protection_fixtures.py`: the module docstring's iTunes and Audible sentence (lines 3 to 5), `itunes()` (550 to 570), `audible()` (573 to 584) and the `adrm-short` variant (753 to 755).
  - [mp4.md](../formats/mp4.md) 125 (its `drms`, `drmi` and `aavd` sentence), 138 and 139, and the same lines on any branch that rewords them (such as `docs/mp4-drms-wording`).
  - [development.md](../development.md) 1923: the clause on iTunes' and Audible's names and layouts in "Sources of the names and layouts".
