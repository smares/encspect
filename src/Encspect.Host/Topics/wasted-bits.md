---
id: wasted-bits
title: Wasted bits
related: flac-subframes, bit-depth-usage
references: RFC 9639 (FLAC), section 9.2
---

**The problem.** Some audio has low bits that never change. Audio that was scaled up from a lower bit depth, or that came from a source with fewer bits, has zeros in the bottom of every sample, and coding them is wasted effort.

**How it works.** A subframe has a flag for wasted bits. When it is set, a count follows in unary, and the encoder has removed that many low bits from every sample of the subframe, because they were all zero. The decoder shifts the samples left by the count after it has rebuilt them. The count is found by the encoder looking at the subframe: it is the number of trailing zero bits that all its samples share.

**In practice.** Wasted bits are per subframe and per frame, so a file may use them in some places only.

**What you see.** Wasted Bits in each subframe, and in the statistics how many subframes use them. A file whose subframes all report the same number of wasted bits has less real depth than its header says.

**Measured too.** When the audio is decoded and measured, [Low Bits Unused](bit-depth-usage) counts the same thing over every sample of the stream instead of per subframe.
