---
id: flac-in-ogg
title: FLAC in Ogg
related: flac-stream, ogg-pages, granule-position, flac-metadata-blocks
references: RFC 9639 (FLAC), section 10.1
---

**The problem.** FLAC can be carried in an Ogg stream, to be mixed with other streams or streamed, and the Ogg packets need a mapping that says what each one holds.

**How it works.** The first packet of the logical stream is 51 bytes: the byte 0x7F, the characters "FLAC", the version of the mapping (1.0), the number of header packets that follow as a 16-bit number, the marker "fLaC" and the STREAMINFO block with its header. The header packets are the other metadata blocks, one in each packet, and the one with the last-block flag ends them. Every packet after them holds exactly one frame. A frame that is longer than a page spans pages, as any packet may. The number of header packets may be 0, which says it is not stated; the last-block flag then ends the headers.

**Granule positions.** The granule position of a page is the number of samples up to the end of the last frame that ends on the page, so the last page tells the length of the stream ([granule positions](granule-position)).

**What you see.** The mapping header and each metadata block, and the frames of the stream under the logical stream, read as a native FLAC stream's are. Findings name a header count that does not match the blocks, a packet that holds more than a frame and granule positions that do not match the frames.
