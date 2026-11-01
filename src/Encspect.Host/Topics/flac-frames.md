---
id: flac-frames
title: FLAC frames
related: flac-stream, flac-blocking-strategy, flac-subframes, flac-channel-assignment, crc, verification
references: RFC 9639 (FLAC), sections 9.1, 9.1.8 and 9.3
---

**The problem.** A decoder has to find where each piece of audio starts and be sure of it, whether it begins at the start of the stream, jumps in at a seek point, or recovers after damage.

**How it works.** A frame holds a block of samples, the same number of every channel. It starts with a header: a 15-bit sync code, a bit for the blocking strategy, the block size, the sample rate, the channel assignment, the sample size, a number and a CRC-8 over the header. Common block sizes and rates have 4-bit codes; the others are stated after the number. One subframe per channel follows, then padding to a whole byte and a CRC-16 over the whole frame. Because a frame depends on no other, a player can start at any frame.

**Finding frames.** The sync code can occur inside audio data, so a candidate counts only if its header's CRC-8 holds, its fields agree with STREAMINFO, and the frame it starts ends with a CRC-16 that holds and is followed by another frame or the end of the data. This is how the analyzer resynchronizes after damage.

**What you see.** Each frame with its header fields, subframes and CRC-16. A frame whose header CRC-8 or CRC-16 differs, a frame the data cuts short, bytes between frames and numbers that jump are findings. The checksums are compared in every analysis ([verification](verification)).
