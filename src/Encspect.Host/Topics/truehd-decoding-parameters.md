---
id: truehd-decoding-parameters
title: TrueHD decoding parameters
related: truehd-restart-header, truehd-residual-coding, truehd-prediction-filters, truehd-rematrixing
references: Encspect's TrueHD and MLP format description (docs/design/truehd-format.md), section "Decoding parameters"
---

**The problem.** The best settings for coding a stretch of audio change over time, but sending all of them with every block would waste the bits lossless coding is trying to save.

**How it works.** A block may begin with decoding parameters, introduced by a flag. Eight presence flags say which groups can occur, and each group that can occur starts with a bit that says whether it changes. The groups come in a fixed order: the flags themselves, the block size (8 samples up to the unit's size), the primitive matrices ([rematrixing](truehd-rematrixing)), the output shifts, the quantizer step sizes, and then, for each coded channel, an optional FIR filter, an optional IIR filter ([prediction filters](truehd-prediction-filters)), a Huffman offset, the codebook and the number of least significant bits ([residual coding](truehd-residual-coding)). A group that does not change keeps its earlier value, so most blocks carry few parameters.

**In files.** The files Encspect was tested on keep the presence flags at 0xFF and change matrices, output shifts and quantizer steps only in the first block of a unit.

**What you see.** Each block's parameters with the values that changed, and the block sizes. A value out of range, a filter whose orders add up to more than 8, or a block that takes the unit past its samples is a finding and ends that substream's reading.
