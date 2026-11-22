---
id: nero-tags
title: Nero's tags box in MP4
related: itunes-metadata, quicktime-user-data, iso-base-media-format
references: files that neroAacEnc 1.0.0.2 to 1.3.3.0, Nero Burning ROM and neroAacTag 1.5.4.0 wrote, with neroAacTag's own listings (-list-meta, -list-covers, -list-chapters); no specification is known
---

**Where it lives.** Nero's software writes a file's tags twice: the iTunes metadata (an `ilst` in the `meta` box of the movie's user data, `udta`) and, beside that `meta` box, a `tags` box of its own. In the committed files neroAacEnc 1.0.0.2 to 1.3.3.0 write it with `tool` (and `cdec`), Nero Burning ROM's encoder with twelve mostly empty fields, and neroAacTag 1.5.4.0 with what it was given; neroAacEnc 1.5.x writes none. No specification is known: the analyzer reads the layout from the bytes of those files and checks neroAacTag's against its own listings.

**Fields.** A `meta` box inside it holds a list of fields, a 32-bit count and then for each a flags byte, a key (its length is 16 bits), a 16-bit value type and a 32-bit value length with the value. The fields seen are text in UTF-8: title, artist, album, album artist, composer, genre, year, comment, a field a user named with `-meta-user`, and what the encoders write: `cdec` (its version and command line), `iTunSMPB` (the [gapless playback](gapless-playback) figures) and `tool`. The flags byte is 0x00 in every `iTunSMPB` and `tool` field and in `cdec` of neroAacEnc 1.3.3.0, and 0x80 in the others; the files do not show what it selects.

**Covers.** A `cvrx` box holds a cover image: two 32-bit values, a text that names the kind of cover ("front cover" in the file seen), and the image with its length. The box states no image type; the cover seen is a JPEG.

**Segments.** A `tseg` box holds a segment: a `tshd` header with its number, counting from 0, and where the segment has fields of its own, a field list. A file without chapters has one segment and no fields in it; a file with chapters has one for each, and its fields are the chapter's title and a track number. The chapter list the same tool writes (`chpl`) repeats the titles.

**What you see.** A Nero Tags node with its fields, covers and segments. Where a field, a cover or a chapter title differs from the iTunes metadata or the `chpl` box beside it, a finding names both. In Encspect's test files the text is UTF-8 even where neroAacTag's listing prints question marks for it.
