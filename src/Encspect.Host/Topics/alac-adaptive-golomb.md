---
id: alac-adaptive-golomb
title: Adaptive Golomb coding in Apple Lossless
related: rice-coding, alac, alac-prediction
references: Apple Lossless Audio Codec source (macosforge/alac, Apache License 2.0), ag_dec.c and ag_enc.c
---

**The problem.** Prediction errors are small numbers that are mostly near zero, and how large they are changes from one stretch of audio to the next. A fixed code wastes bits, so the code has to follow the signal.

**How it works.** Each error is first folded into an unsigned number, so that 0, -1, +1, -2 ... become 0, 1, 2, 3 ... It is then written in the style of a Rice code ([Rice coding](rice-coding)): the quotient in unary (that many 1 bits and a 0), then a remainder of k bits. The parameter k is not stored: both sides work it out from a running mean of recent values. The mean starts at the cookie's `mb` value and is updated after each number with a weight, the cookie's `pb` (40 in Apple's encoder); k is derived from the mean and is never larger than the cookie's `kb` (14 in Apple's encoder).

Two cases leave the pattern. A quotient of nine or more 1 bits means the number is escaped and follows as a raw value of the sample's bit width, which keeps loud, noisy audio from costing unbounded bits. When the mean falls very low, the coder enters a zero-run mode: after such a number the next stretch is a count of zero errors, stored as a run length, which makes silence cheap.

**What you see in a file.** The analyzer walks the residuals of every channel to find where the element ends; the figures it reports are the number of zero runs and escaped values, and the bits the walk used. The walk must end exactly at the end of the packet.

**Where it is specified.** In Apple's source, the files `ag_dec.c` and `ag_enc.c`.
