---
id: vorbis-blocks
title: Vorbis blocks and the overlap rule
related: block-switching, vorbis-setup-header, granule-position, gapless-playback
references: Vorbis I specification, sections 1.3, 4.2.2, 4.3.1, 4.3.8 and A.2
---

**Two block sizes.** Vorbis transforms the audio with an MDCT in blocks of one of two sizes, a short one and a long one, powers of two from 64 to 8192 samples that the identification header gives; libvorbis typically uses 256 and 2048 at 44.1 kHz. Long blocks resolve frequencies finely; short blocks keep the noise of a sudden attack close to it, the pre-echo problem [block switching](block-switching) describes for MP3.

**Modes.** Each audio packet codes one block. Its first bit is 0 for audio, then come a few bits that name one of the setup header's modes, and the mode says whether the block is long or short. A long block also carries two flags that say whether its neighbors are long or short, since the window's halves must match the blocks it overlaps.

**The overlap rule.** Consecutive blocks overlap by half: the window's three-quarter point of one block meets the quarter point of the next. A decoder returns the samples from the center of the previous block to the center of the current one, a quarter of the previous block plus a quarter of the current one. The first block returns nothing; it only primes the decoder.

**Granule positions.** In Ogg, each page's granule position is the number of the last sample its last complete packet returns. The first page's may differ, to start the stream later or to discard samples at the start, and the last page's may be smaller, to trim the end.

**What you see.** Each packet's mode, block size and window flags, the share of long and short blocks, and the samples the overlap rule gives, checked against the granule positions.
