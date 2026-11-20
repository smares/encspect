---
id: truehd-rematrixing
title: TrueHD and MLP rematrixing
related: truehd-substreams, truehd-decoding-parameters, channel-layouts, left-side-stereo, mid-side-stereo
references: Encspect's TrueHD and MLP format description (docs/design/truehd-format.md), sections "Matrix parameters" and "Rematrixing"
---

**The problem.** The channels a substream codes are not always the channels a presentation outputs, and a larger presentation is built from the channels of the smaller ones and its own.

**How it works.** The filtered channels feed a list of up to 8 primitive matrices, applied in order once per unit. Each matrix replaces one channel by a weighted sum of all channels with its own 14-bit fixed-point coefficients, shifted back, with the low quantized bits cleared and an optional bypassed least significant bit added. A later matrix sees what an earlier one replaced. With noise type 0 two pseudorandom noise channels are also inputs; with type 1 a matrix adds scaled values from a noise table. The channels then get their output shifts and go to the positions the restart header assigns them.

**Arithmetic.** One product reaches 37 bits, so the sums are 64-bit and only the shifted result is kept as 32 bits.

**What you see.** The matrices of a block with the channel each replaces, the fractional bits and whether it bypasses the least significant bit, and the output shifts. A matrix count above 8 (6 for MLP), an output channel past the last matrix channel, or more than 14 fractional bits is a finding.

**Stereo Coding.** For the two-channel presentation (substream 0, two matrix channels) the report counts how the matrices in force at the end of each access unit combine the two coded channels into left and right: left/right stereo, left/side stereo, side/right stereo, mid/side stereo, or a weighted mix (any other combination, also both channels rebuilt from one). This is the arithmetic, not a choice the format names, and every coding is inverted exactly. In a stream of more channels the presentation is a downmix, and says nothing about the other channels.

**Observed.** In Encspect's test files, FFmpeg 6.1.1's encoder codes stereo as one channel less half of the other (a weighted mix) and writes no matrices for the two-channel presentation of more than two channels. Exceptions: the silent file has none in its silent units, and the lossyWAV file has a matrix that keeps its channel in 354 units (both left/right). The analyzer does not use this.
