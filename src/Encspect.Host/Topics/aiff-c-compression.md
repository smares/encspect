---
id: aiff-c-compression
title: AIFF-C compression types
related: aiff-common-chunk, pcm-samples, aiff-container
references: Apple, Audio Interchange File Format AIFF-C (draft of 8/26/91), section 4.0 and appendix C; Apple, QuickTime File Format Specification (2001), Table 3-3; Apple, Core Audio Format Specification, Audio Description Chunk
---

**The problem.** AIFF could only hold uncompressed samples. AIFF-C added a way to name how the sound data is coded, so that one file format carries both, and so that a program that does not know a coding can say so.

**How it works.** The Common Chunk of an AIFF-C file holds a compression type, four characters for programs, and a compression name for people. The draft lists NONE (uncompressed), the Apple IIGS codings ACE2 and ACE8 and the Macintosh codings MAC3 and MAC6. Apple's QuickTime File Format Specification lists further sound codes that files use: twos (the same samples as NONE), sowt (samples with the bytes in little-endian order, 16-bit in Apple's description; the analyzer reads it at the width the Common Chunk states), in24 and in32 (24 and 32-bit integers), raw (8-bit unsigned), fl32 and fl64 (IEEE floating-point numbers), alaw and ulaw (companded 8-bit samples) and ima4 (IMA 4:1 ADPCM). The analyzer also names the code GSM as GSM 6.10; no Apple document read lists it, so that name is the analyzer's choice.

For the uncompressed types the sound data is a list of sample frames, so its size and the number of frames agree. A compressed type packs the samples of each channel in frames of its own, and the Common Chunk counts those frames. Appendix C of the draft gives them: 2 bytes for 6 samples in MACE 3:1, 1 byte for 6 in MACE 6:1, 1 byte for 2 in ACE 2:1 and 3 bytes for 8 in ACE 8:3, for each channel. A frame of ima4 is 34 bytes for 64 samples (Apple's Core Audio Format Specification). The length of the sound is the frames times the samples one holds. For GSM and for types the analyzer does not know, no source says what the count counts, so it states no length.

**What you see.** The type and the name as the file states them, and the coding the analyzer makes of them: PCM, floating-point PCM, A-law, Mu-law or the name of the compression. A type the analyzer does not know is shown as stated and reported as Other.
