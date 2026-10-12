---
id: xing-header
title: The Xing and Info header
related: bitrate-modes, vbri-header, lame-tag, bit-reservoir
references: LAME libmp3lame/VbrTag.c; Xing Technology VBR header
---

**The problem.** In a VBR file, frames have different sizes, so a player cannot compute the duration from the file's size, nor find the byte at which minute two starts. Reading every frame first would take too long.

**How it works.** Xing Technology's encoder introduced a header written into the first frame of the stream, a frame that holds no audio. It starts with the text "Xing" and holds, as its flags announce:

- the number of frames, from which the duration follows;
- the number of bytes of the stream;
- a table of contents of 100 entries for seeking: entry i says how far into the file, in 256ths of the byte count, i percent of the playing time is reached;
- a quality indicator the encoder chose.

LAME writes the same header with "Info" instead of "Xing" for CBR streams, and puts its own [LAME tag](lame-tag) after it.

**What you see.** The header frame shows the header's fields instead of side information and audio. The statistics compare the frame and byte counts it claims with those the analyzer counted; a mismatch suggests the file was cut or joined after encoding. A header in the middle of a stream suggests joined files, and players that only read the first one show the wrong duration. The header's frame decodes as silence in decoders that do not know it. Fraunhofer's encoders write a [VBRI header](vbri-header) instead.
