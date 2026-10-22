---
id: flac-metadata-blocks
title: FLAC metadata blocks
related: flac-stream, flac-streaminfo, flac-seek-table, flac-cuesheet, flac-picture, vorbis-comments, padding
references: RFC 9639 (FLAC), section 8
---

**The problem.** A stream needs facts that are not audio, such as its sample rate, a seek table, tags and cover art, and they must be able to grow or be skipped without touching the audio.

**How it works.** Each metadata block starts with a header of four bytes: a flag that marks the last block, a 7-bit type and a 24-bit length of the data that follows. A reader that does not know a type skips it by its length. The types are STREAMINFO (0), PADDING (1), APPLICATION (2), SEEKTABLE (3), VORBIS_COMMENT (4), CUESHEET (5) and PICTURE (6); 7 to 126 are reserved. The type 127 is forbidden, because the header byte of a last block of that type would be 0xFF, which starts a frame's sync code.

**Order and number.** STREAMINFO comes first and only once. A seek table, a Vorbis comment and a cue sheet may each appear once; pictures and application blocks as often as needed.

**Padding.** A PADDING block holds only zeros. An editor that makes a tag longer takes the room from the padding instead of rewriting the whole file.

**Application blocks.** An application block starts with a four-character ID of the application and then holds that application's own data.

**What you see.** Every block as a node with its header fields and its contents. Findings name a block type that appears too often, a length that does not match the block's fields, a block the data ends inside, and a last-block flag that never appears before the frames.
