---
id: wave64
title: Wave64
related: riff-chunks, rf64, waveformatex
references: Wave64 as the MultimediaWiki describes it, checked against files written by FFmpeg 6.1.1; National Archives PRONOM fmt/951 (Sonic Foundry WAVE 64)
---

**The problem.** A RIFF chunk size is 32 bits, so a WAVE file cannot pass 4 GiB. Wave64, which the National Archives' PRONOM registry lists as Sonic Foundry WAVE 64, widens every field of the layout instead of adding a chunk to patch the sizes, as RF64 does.

**How it works.** A Wave64 file has the layout of a RIFF file with these changes. A chunk's ID is a 128-bit GUID where RIFF has four characters, and its size is 64 bits. The file starts with the GUID of the riff chunk, the size of the whole file and the GUID of the form wave. In the GUID of fmt, data, fact and the other chunks that RIFF names with four characters, the first four bytes are those characters and the other twelve are the same in every such chunk.

The riff chunk's size counts the whole file, and a chunk's size counts its own header, the 16 bytes of GUID and the 8 of size, which RIFF's does not. Chunks start at multiples of 8 bytes from the start of the file, and the padding that gets them there is not in the size. Inside, the fmt, fact and data chunks are those of a WAVE file, with 32-bit integers, so the format tags, the channel mask and the sample coding are the same.

The files are named .w64.

**What you see.** The riff chunk with its GUIDs, each chunk with its GUID and size, the padding after a chunk, and findings where a size is under the 24 bytes of a header, runs past the file or leaves the padding short.
