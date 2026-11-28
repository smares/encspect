---
id: wma-exponents
title: Windows Media Audio exponents
related: wma-standard, wma-frames-and-blocks, noise-substitution, line-spectral-pairs, wma-coefficients
references: docs/design/wma-standard-format.md, "Exponents"; docs/design/wma-standard-decoding.md, "Exponents"
---

**The problem.** A block's coefficients are small integers. To turn them back into a spectrum the decoder needs to know how loud each part of it is, and that envelope changes from block to block and from channel to channel.

**How it works.** Each coded channel of a block has an exponent for every line, the shape of the envelope. A decoder multiplies a coefficient by the exponent of its line and by a gain the whole block shares (the total gain), and divides by the channel's largest exponent so that the gain alone sets the level. Windows Media Audio versions 1 and 2 send the exponents in one of two ways, chosen for the stream by its encode options. Scale factor codes give one value per band, as a difference from the band before, a value v standing for the exponent 10 to the power v/16. Line spectral pairs give ten numbers that define a smooth curve over the block, which the decoder evaluates at every line.

**Reuse.** A block shorter than the frame may send a flag instead of exponents, and then each coded channel reuses the ones it received last, possibly from a block of another size: the line takes the exponent at the same frequency. This is also why a lost frame can leave later blocks without the envelope they were coded against.

**What you see in a file.** The "Exponents" nodes show how many bits the codes or the ten indexes take, the "Exponents Sent" flag shows the blocks that reuse them, and the statistics count those blocks. The decoder reads the values.

**Where it is specified.** Not in a public specification; Encspect follows a description of the syntax and of the reconstruction written for it.
