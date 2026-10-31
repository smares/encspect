---
id: wma-frames-and-blocks
title: Frames and blocks of Windows Media Audio
related: wma-superframes, wma-bit-reservoir, mid-side-stereo, noise-substitution, block-switching, huffman-coding
references: docs/design/wma-standard-format.md
---

**The problem.** A transform codec trades time resolution against frequency resolution: long blocks code steady sound efficiently, short blocks follow an attack without spreading it over a long stretch.

**How it works.** A frame is 512, 1,024 or 2,048 samples per channel, by the sampling rate, coded as one or more blocks whose lengths add up to it. With variable block lengths a block has one of several sizes, each half the one before, and the stream sends the sizes of the previous, current and next block. A block of a stereo stream has a mid/side flag, and each channel a coded flag. Then come the total gain, the noise substitution flags, the exponents and the coefficients.

**Exponents.** The exponents are the envelope of the spectrum, one value per band. They are sent as scale factor codes, each a difference from the band before, or as line spectral pairs. A block shorter than the frame may send a flag and reuse the exponents it last received.

**Coefficients.** The coefficients are run-level codes, each a run of zero coefficients and a non-zero one, with a sign. An end-of-block code ends the channel early, and an escape carries a level and run that no code covers.

**What you see.** The blocks of each frame with their fields at their bits, and statistics over all blocks: the sizes used, the mid/side blocks, the blocks that reuse exponents. A frame has no length field and no checksum, so only reading its syntax finds where it ends.
