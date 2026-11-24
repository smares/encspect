---
id: tak-residual-coding
title: TAK residual coding
related: rice-coding, linear-prediction, tak-frames
references: Encspect's description of the TAK frame data (docs/design/tak-format.md, section 3)
---

**The problem.** What a predictor leaves over is small numbers, near zero and of a size that changes from one stretch of audio to the next. A code that spends few bits on small numbers needs to follow that size.

**How it works.** A residual block is cut into partitions of a length that follows from the sampling rate (the last one takes the remainder, so it may be somewhat longer or shorter), or is left whole. Each partition has a mode from 0 to 50, which picks a row of a fixed table. Mode 0 means every value is zero and no bits are stored. In the other modes each value is a code that starts with a read of a fixed number of bits, which grows with the mode, and continues with escapes for larger values. The first partition's mode is stored as it is, and each further one as a change from the one before it (the same, one more or less, a few more or less, or a new mode stored in full), so a block whose modes stay the same costs little. The signed values are folded to unsigned codes first, as in Rice coding.

**What you see.** The highest mode of each frame as "Highest Residual Mode", and of each second as "Highest Residual Mode Over Time". A higher mode starts each value with a longer read. The mode says how the residual was coded, not why.
