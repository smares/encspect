---
id: wma-lossless-subframes
title: Windows Media Audio Lossless subframes and residuals
related: wma-lossless, wma-subframes, linear-prediction, rice-coding, wasted-bits, mid-side-stereo
references: Encspect's format description docs/design/wma-lossless-format.md
---

**The problem.** A decoder that predicts samples from earlier ones must start from a known state, and the coder must store the prediction errors in few bits.

**Seekable subframes.** A subframe with the seekable flag set states every parameter of the filters that undo the prediction: a recursive filter, a mid/side flag, a filter across channels, and for each channel one to eight cascaded adaptive filters with their orders and scalings. It also states a mean scaling and a quantization step. It resets the running state, so a decoder can start there.

**Residuals.** Each coded channel stores one residual per sample. A residual is a quotient in unary, with an escape for long ones, and a remainder whose width follows a running sum of the residuals before. The sum is kept per channel from one subframe to the next until a seekable subframe resets it. A reader therefore cannot find the end of a frame without the sums of the frames before.

**Raw subframes.** A subframe can hold the samples uncoded, with their width reduced by the zero low bits it states. A zero low bits count leaves out low bits that are 0 in every sample, such as the 8 of a 24-bit stream that holds 16 significant bits (see [wasted bits](wasted-bits)).

**What you see in a file.** Encspect shows, for each subframe, the seekable and raw flags, the parameters of a seekable one, which channels are coded, the zero low bits, and for each channel how many residuals and escapes it holds. Its statistics count seekable and raw subframes and the filters they state. In Encspect's three committed Lossless test files, written by Microsoft's Windows Media encoder (wmvcore 12.0.26100, VBR Quality 100), the first frame has a seekable subframe and the next ones follow 8 to 12 frames apart (frames 0, 9, 18, 30 and 39 in the 16-bit stereo file, 0, 8, 19, 28 and 39 in the 24-bit stereo file, 0 and 9 in the 5.1 file). Encspect does not use this spacing.
