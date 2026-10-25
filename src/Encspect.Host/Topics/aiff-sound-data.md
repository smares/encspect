---
id: aiff-sound-data
title: AIFF sound data and block alignment
related: aiff-common-chunk, pcm-samples, aiff-container
references: Apple, Audio Interchange File Format AIFF-C (draft of 8/26/91), section 5.0
---

**The problem.** Software that records and plays sound in real time reads from disk in blocks, and its samples are fastest to read when the first one starts at the beginning of a block, wherever the chunk header ends.

**How it works.** The Sound Data Chunk (SSND) starts with two numbers. The offset says how many bytes to skip before the first sample frame, and the block size how large the disk blocks are that the data is aligned to; both are zero if the file is not aligned. The sample frames follow, and may be followed by unused bytes up to a block boundary. The chunk's size includes the eight bytes of the two numbers, and, as with every chunk, a pad byte follows if it is odd.

The count of sample frames comes from the Common Chunk, not from the chunk's size. Uncompressed samples are signed, with the bits at the left of the smallest whole number of bytes that holds them: a 12-bit sample is stored in two bytes, the four low bits being zero.

**What you see.** The offset and block size and the sound data as one block of bytes. Findings appear where the chunk holds fewer frames than the Common Chunk states, or whole frames more than it states while no block size explains them.
