---
id: bit-reservoir
title: The bit reservoir and main_data_begin
related: granules-and-frames, bitrate-modes, huffman-coding, xing-header
references: ISO/IEC 11172-3 2.4.1.7, 2.4.2.7, 2.4.3.4.3, 2.4.3.4.4; ISO/IEC 13818-3 2.4.1.7
---

**The problem.** At a constant bitrate every frame has the same number of bytes, but the music does not need the same number of bits all the time. A quiet passage needs few bits, a sudden attack many.

**How it works.** Layer III separates the coded audio of a frame, its main data, from the frame's bytes. A frame that needs less than its share leaves the rest unused, and a later frame may start its main data back in that unused space. Each frame's side information says where: main_data_begin counts the bytes back from the first byte of the frame's sync word at which its main data starts, not counting the bytes of the headers and side information in between. 0 means the frame does not borrow. The unused space that later frames can borrow is called the bit reservoir.

**Limits.** main_data_begin has 9 bits in MPEG-1 and 8 bits in MPEG-2 and 2.5, so a frame can reach back at most 511 or 255 bytes. ISO/IEC 11172-3 also bounds the reservoir by a decoder buffer of 7,680 bits, the length of a frame at 320 kbps and 48 kHz, and says main_data_begin is always 0 at 320 kbps; for lower sampling frequencies it says only that the buffer should be constrained to the same physical size, and ISO/IEC 13818-3 states no buffer rule. Encoders such as LAME exceed these limits, and decoders accept it. In Encspect's test files, LAME's VBR and ABR streams show it in releases from 3.20 to 4.0.

**What you see.** For each Layer III frame the tree shows main_data_begin, the reservoir fill when the frame starts, and how the frame's bytes split between its own main data, main data of later frames ("Bit Reservoir") and [ancillary data](ancillary-data) no frame uses. The graphs show the fill over time. Because frames depend on earlier frames' bytes, cutting a file in the middle of the stream can leave the first frames after the cut without the main data they need.
