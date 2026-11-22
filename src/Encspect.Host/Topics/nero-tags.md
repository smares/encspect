---
id: nero-tags
title: Nero's tags box in MP4
related: itunes-metadata, quicktime-user-data, iso-base-media-format
references: files that neroAacEnc and neroAacTag 1.5.4.0 wrote, with neroAacTag's own listings (-list-meta, -list-covers, -list-chapters); no specification is known
---

**Where it lives.** neroAacTag tags a file twice. It writes the iTunes metadata (an `ilst` in `moov`'s `meta` box) and, beside it in the movie's user data (`udta`), a `tags` box of its own. The same fields therefore appear in both. No specification of the `tags` box is known: the analyzer reads it from the bytes of files that neroAacEnc and neroAacTag 1.5.4.0 wrote and checks the result against neroAacTag's own listings.

**Fields.** A `meta` box inside it holds a list of fields, a 32-bit count and then for each a flags byte, a key (its length is 16 bits), a 16-bit value type and a 32-bit value length with the value. The fields seen are text in UTF-8: title, artist, album, comment, a field a user named with `-meta-user`, and three that the encoder writes, `cdec` (its version and command line), `iTunSMPB` (the [gapless playback](gapless-playback) figures) and `tool`.

**Covers.** A `cvrx` box holds a cover image: two 32-bit values, a text that names the kind of cover ("front cover" in the file seen), and the image with its length. The box states no image type; the cover seen is a JPEG.

**Segments.** A `tseg` box holds a segment: a `tshd` header with its number, counting from 0, and where the segment has fields of its own, a field list. A file without chapters has one segment and no fields in it; a file with chapters has one for each, and its fields are the chapter's title and a track number. The chapter list the same tool writes (`chpl`) repeats the titles.

**What you see.** A Nero Tags node with its fields, covers and segments. Where a field, a cover or a chapter title differs from the iTunes metadata or the `chpl` box beside it, a finding names both. In Encspect's test files the text is UTF-8 whatever neroAacTag's listing prints: its console output replaces Cyrillic and Japanese letters with question marks, and shows the German letters in the console's code page, but the box and the iTunes metadata hold the letters themselves.
