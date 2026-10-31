---
id: wma-packets
title: Windows Media Audio packets and frames
related: wma-pro, wma-lossless, asf-packets, wma-subframes
references: Encspect's format descriptions docs/design/wma-pro-format.md and wma-lossless-format.md
---

**The problem.** A frame of Windows Media Audio Professional or Lossless has no fixed size, but the container carries data in packets of one block alignment. A frame has to be able to start anywhere in a packet and continue in the next.

**The packet header.** Every packet starts with a four-bit sequence number that counts packets modulo 16, two more bits, and a count of bits that finish the frame before. The count says how many bits right after the header end the frame that began in an earlier packet; the first frame that starts in the packet follows them. A count that covers the whole payload means the packet only continues a frame. The width of the count is the base-2 logarithm of the block alignment, rounded down, plus 4 bits.

**Frames in a packet.** Frames follow each other with nothing between them. The last bit of a frame says whether another frame starts after it in the same packet. Where the decode flags set the length prefix, every frame starts with its length in bits, so a reader finds a frame's end without reading it. Without the prefix a frame's end is found only by reading it to the end, and the frames that start in a packet are read when the next packet has supplied their last bits.

**Loss.** A sequence number that does not follow the one before tells a decoder that a packet is missing. It then drops the frame that was crossing into the packet, and, without the length prefix, the frames that began in the packet before.

**What you see in a file.** Encspect lists each packet with its header fields and the frames that start in it, and the bits left after the last frame.
