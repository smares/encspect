---
id: bit-reservoir
title: The bit reservoir and main_data_begin
related: granules-and-frames, bitrate-modes, huffman-coding, xing-header, wma-bit-reservoir
references: ISO/IEC 11172-3 2.4.1.7, 2.4.2.7, 2.4.3.4; ISO/IEC 13818-3
---

**The problem.** At a constant bitrate every frame has the same number of bytes, but the music does not need the same number of bits all the time. A quiet passage needs few bits, a sudden attack many.

**How it works.** Layer III separates the coded audio of a frame, its main data, from the frame's bytes. A frame that needs less than its share leaves the rest unused, and a later frame may start its main data back in that unused space. Each frame's side information says where: main_data_begin counts the bytes before the frame's header at which its main data starts. 0 means the frame does not borrow. The unused space that later frames can borrow is called the bit reservoir.

**Limits.** main_data_begin has 9 bits in MPEG-1 and 8 bits in MPEG-2 and 2.5, so a frame can reach back at most 511 or 255 bytes. ISO/IEC 11172-3 also bounds the reservoir by the decoder's buffer, which holds one frame at the highest bitrate; LAME has not enforced that limit by default since 3.81, and decoders accept it.

**What you see.** For each Layer III frame the tree shows main_data_begin, the reservoir fill when the frame starts, and how the frame's bytes split between its own main data, main data of later frames ("Bit Reservoir") and [ancillary data](ancillary-data) no frame uses. The graphs show the fill over time. Because frames depend on earlier frames' bytes, cutting a file in the middle of the stream can leave the first frames after the cut without the main data they need. Windows Media Audio has a reservoir of its own kind ([bit reservoir in Windows Media Audio](wma-bit-reservoir)).
