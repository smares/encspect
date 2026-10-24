---
id: aiff-c-compression
title: AIFF-C compression types
related: aiff-common-chunk, pcm-samples, aiff-container
references: Apple, Audio Interchange File Format AIFF-C (draft of 8/26/91), section 4.0 and appendix C; FFmpeg libavformat/aiff.c
---

**The problem.** AIFF could only hold uncompressed samples. AIFF-C added a way to name how the sound data is coded, so that one file format carries both, and so that a program that does not know a coding can say so.

**How it works.** The Common Chunk of an AIFF-C file holds a compression type, four characters for programs, and a compression name for people. The draft lists NONE (uncompressed), the Apple IIGS codings ACE2 and ACE8 and the Macintosh codings MAC3 and MAC6. Apple registers further types. In files, the types that FFmpeg's reader and writer know include twos (the same samples as NONE), sowt (16-bit samples with the bytes in little-endian order), in24 and in32 (24 and 32-bit integers), raw (8-bit unsigned), fl32 and fl64 (IEEE floating-point numbers), alaw and ulaw (companded 8-bit samples), ima4 (IMA ADPCM) and GSM.

For the uncompressed types the sound data is a list of sample frames, so its size and the number of frames agree. A compressed type packs frames in blocks, and what the Common Chunk's count then counts depends on the coding. FFmpeg's reader takes it as a count of packets for ima4, which hold 64 samples of each channel in 34 bytes. For the other packed types the draft does not say, so the analyzer states no length for them.

**What you see.** The type and the name as the file states them, and the coding the analyzer makes of them: PCM, floating-point PCM, A-law, Mu-law or the name of the compression. A type the analyzer does not know is shown as stated and reported as Other.
