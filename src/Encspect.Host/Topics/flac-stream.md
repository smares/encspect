---
id: flac-stream
title: The FLAC stream
related: flac-metadata-blocks, flac-frames, flac-in-ogg, verification, rice-coding
references: RFC 9639 (FLAC), sections 8 and 9
---

**The problem.** A lossless file has to hold the audio exactly, say how to play it, and survive being cut, joined and damaged, without a table that says where its parts are.

**How it works.** A native FLAC stream is the four characters "fLaC", then metadata blocks, then frames. The first metadata block is always [STREAMINFO](flac-streaminfo), which holds the sample rate, channels, bit depth and an MD5 of the audio; the others, such as a seek table or a Vorbis comment, are optional ([metadata blocks](flac-metadata-blocks)). Frames follow the last metadata block. Each [frame](flac-frames) holds the same number of samples of every channel, starts with a sync code and a header, ends with a CRC-16, and decodes without the frames before it, so a player can start at any frame and find the next one after damage.

**What the stream does not hold.** There is no index of frames in the stream itself; the optional [seek table](flac-seek-table) is one. Tags that other formats append, an ID3v2 tag in front or an APEv2 tag behind, are not part of FLAC, though files carry them.

**In Ogg.** The same blocks and frames travel in Ogg packets, one block or frame to a packet ([FLAC in Ogg](flac-in-ogg)).

**What you see.** The marker, each metadata block with its fields, and each frame with its header, subframes and CRC-16. The statistics count frames, samples and the kinds of subframe, and the stream's verification says which checksums were compared.
