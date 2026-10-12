---
id: id3v2
title: ID3v2 and its versions
related: id3v2-unsynchronization, id3v2-padding, id3v2-text-encodings, id3v1, tag-placement
references: id3.org, ID3v2.2 (1998), ID3v2.3.0 (1999), ID3v2.4.0 (2000)
---

**What it is.** ID3v2 replaced the fixed fields of [ID3v1](id3v1) with a flexible container: a 10-byte header with "ID3", the version and the tag's size, then frames, each a piece of metadata with an ID such as TIT2 (title) or APIC (picture), a size and flags. Frames can hold any text in Unicode, pictures, lyrics, play counts, links and data of programs' own. The tag sits at the start of the file, so a player streaming the file has the metadata before the audio.

**Versions.** Three versions are in use, and readers must know which they read:

- **ID3v2.2** uses three-letter frame IDs (TT2, TP1) and was written by early versions of iTunes.
- **ID3v2.3** uses four-letter IDs and is the most widely supported version.
- **ID3v2.4** adds UTF-8, syncsafe frame sizes, a footer for tags at the end of a file, and per-frame [unsynchronization](id3v2-unsynchronization), and replaces some date frames with TDRC and its relatives.

**Structure.** An optional extended header may carry a CRC and restrictions. After the frames comes [padding](id3v2-padding), or in ID3v2.4 a footer.

**What you see.** Every frame with its header, flags and decoded content, the [text encoding](id3v2-text-encodings) of each string, and findings where the tag breaks its version's rules, such as duplicate frames or a wrong size. Each frame's description says what its ID means.
