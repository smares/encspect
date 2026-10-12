---
id: iso-base-media-format
title: The ISO base media file format (MP4)
related: mp4-sample-tables, fragmented-mp4, edit-lists-and-priming, itunes-metadata
references: ISO/IEC 14496-12 4.2, 4.3, 8.1.1, 8.2.1; ISO/IEC 14496-14; QuickTime File Format
---

**The structure.** An MP4 file is a run of boxes. Each box starts with a 32-bit size and a four-character type, such as `moov` or `mdat`, followed by its contents; a size of 1 means a 64-bit size follows, and 0 means the box runs to the end of the file. Container boxes hold further boxes, so the file forms a tree. The format grew out of Apple's QuickTime, whose atoms are the same structure, and MP4, M4A, M4B and .mov files all share it. The `ftyp` box at the start names the brands the file follows, such as M4A.

**Description apart from data.** The `mdat` box holds the coded media as plain bytes, without headers or sync words. The `moov` box describes it: one track per stream, each with [sample tables](mp4-sample-tables) that give every sample's size, time and position in `mdat`. A player can therefore jump to any moment without scanning the audio, and tracks can be interleaved in the file.

**Fast start.** An encoder learns the table sizes only when it finishes, so many writers put `moov` after `mdat`. A player that streams such a file over a network must fetch the end of the file before it can start. Moving `moov` before `mdat`, as ffmpeg's -movflags +faststart does, is called fast start. [Fragmented files](fragmented-mp4) avoid the problem in another way.

**What you see.** The tree of boxes, each with its Size and Type and the fields the analyzer reads, an info finding where `moov` follows `mdat`, and warnings where a box does not fit its parent or the file. The media data itself is read only where the sample tables point, by the codec's parser.
