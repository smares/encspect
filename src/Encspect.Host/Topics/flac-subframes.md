---
id: flac-subframes
title: FLAC subframes
related: flac-frames, linear-prediction, rice-coding, wasted-bits, flac-channel-assignment
references: RFC 9639 (FLAC), section 9.2
---

**The problem.** Audio samples of neighboring moments are alike, so a coder that spends the same number of bits on each wastes most of them. Different stretches of audio also call for different methods: silence needs almost nothing, noise cannot be predicted.

**How it works.** A frame holds one subframe for each channel, and each chooses its own method with a 6-bit type. A constant subframe holds one value for all its samples. A verbatim subframe stores every sample as it is. A fixed predictor of order 0 to 4 predicts each sample from the last few with fixed coefficients, and a linear predictor of order 1 to 32 does so with coefficients it carries ([linear prediction](linear-prediction)). The predictors store their first samples, the warm-up, as they are, and then the residual, what is left of each sample after the prediction, coded with [Rice codes](rice-coding). A subframe may also leave out low bits that are zero in every sample ([wasted bits](wasted-bits)).

**Choosing.** The encoder chooses a method for every subframe, so the types a file uses show what its encoder did.

**What you see.** Each subframe of the first frames with its type, order and fields, and in the statistics how many subframes use each type. A subframe that cannot be read, with a reserved type or a residual that runs past the data, is a finding and ends the frame.
