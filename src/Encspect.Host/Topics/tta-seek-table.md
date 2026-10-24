---
id: tta-seek-table
title: The TTA seek table
related: tta-stream, crc, flac-seek-table
references: TTA format description (tta.sourceforge.net, "Format Description"); FFmpeg 6.1.1 as a black-box reader and writer of the files in Encspect's tests
---

**The problem.** TTA frames have no header of their own and differ in length, so a player cannot tell where the tenth frame starts without reading the nine before it.

**How it works.** The seek table follows the header. It has one 4-byte entry, little-endian, for each frame, then a CRC-32 over the entries. The number of frames is the number of samples in the header over the samples in a frame, rounded up, so the header alone says how long the table is. An entry holds the length of its frame in bytes. In Encspect's test files, written by FFmpeg 6.1.1, the length includes the 4 bytes of the frame's CRC-32 and the entries add up to the length of the frames. A player adds up the lengths before a frame to find where it starts, and the first frame starts right after the table.

**If it is damaged.** The description says a file with a damaged table can still be decoded, but only in an "unseekable" mode. The analyzer treats the lengths as the stream's own: it walks the frames by them, so a table whose CRC-32 fails is reported and its lengths may place frames wrongly, which their CRC-32s then show. A table that puts the end of a frame past the end of the data is a cut frame, and the frames after it are missing.

**What you see.** The table's entries as byte lengths, its CRC-32 as stored and as computed, and findings where the table is cut off, its CRC-32 differs, an entry is shorter than a CRC-32, or the frames end before or after the data does. The end the table gives also tells the analyzer which bytes after the frames are a tag and which are the stream's own.
