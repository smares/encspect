---
id: monkeys-audio-seek-table
title: The Monkey's Audio seek table
related: monkeys-audio-file, monkeys-audio-frames, flac-seek-table
references: Monkey's Audio SDK 13.44, Source/MACLib/APEHeader.cpp (Analyze, AnalyzeCurrent) and APEInfo.cpp; MACLib.h (APE_DESCRIPTOR)
---

**The problem.** Frames have no sync pattern and differ in length, so a player cannot find the one it wants by scanning. It needs to know where each frame starts.

**How it works.** The seek table is a list of 32-bit little-endian entries, one for each frame, each the offset of the frame's first byte counted from the start of the descriptor (which is not the start of the file when bytes such as an ID3v2 tag come first). A frame ends where the next one starts; the last ends where the frame data does. The descriptor states the table's size in bytes. A file bigger than 4 GiB wraps the 32-bit offsets, so a reader adds 2^32 whenever an entry is lower than the one before. Frames start on any byte, and the bytes of the audio are stored in 32-bit words with the bytes of each word reversed, so a frame's first byte is not at the offset its position suggests until the words are put back in order ([frames](monkeys-audio-frames)).

**What you see.** A Seek Table node with a Frame Offset for each frame. The analyzer finds the frames from this table alone: it reports an entry that points outside the frame data or backwards, a first entry that does not lead to the first byte after the WAV header data, a table that holds more or fewer entries than the header counts frames, and a table cut off by the end of the file. Frames it cannot place are not decoded.
