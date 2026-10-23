---
id: wavpack-in-matroska
title: WavPack in Matroska
related: wavpack-blocks, wavpack-audio-checksums, matroska-container
references: FFmpeg 6.1.1, libavformat/matroskadec.c (matroska_parse_wavpack)
---

**The problem.** A WavPack block starts with a 32-byte header, most of which a container already knows: where the block lies in the stream, how long the stream is and how big the block is.

**How it works.** Matroska's A_WAVPACK4 stores each block without its header. The CodecPrivate holds the stream version in two bytes. A packet starts with the number of samples that each of its blocks counts, then holds one or more blocks. Each has its flags and the CRC of its decoded audio in four bytes apiece, the size of its sub-blocks in four more unless the flags make it the only block of its set, and the sub-blocks. A reader puts a header back together from the version (FFmpeg assumes 0x410 where there is no CodecPrivate), a block index and a total of 0, and what the packet gives. The checksum sub-block of a version 5 block covers the original header, which the container does not keep, so it cannot be checked; the CRC of the decoded audio can.

**What you see.** Each packet with its samples and its blocks, each block with its flags, CRC and sub-blocks listed by name. Verifying the audio decodes the rebuilt blocks and compares each block's CRC ([checks over the decoded audio](wavpack-audio-checksums)).
