---
id: tta-coding
title: How TTA codes samples
related: tta-stream, rice-coding, linear-prediction
references: TTA format description and "Compression Theory" (tta.sourceforge.net); docs/design/tta-format.md, checked against libtta-c 2.3 and FFmpeg as black boxes
---

**The problem.** Audio samples cost less to store when each is predicted from the ones before it and only the difference is written.

**How it works.** The authors' description lists four stages: cutting the signal into blocks, which are the frames of about a second; decorrelating the channels; modeling the signal; and entropy coding. For channel decorrelation it gives the example of two channels turned into a mean and a difference, so that channels that resemble each other cost less. For the model it describes a two-stage adaptive filter: a first stage it calls a zero-level predictor, then wide-band filters whose weights change from sample to sample by a least-mean-squares rule. The authors report this beat the polynomial and linear predictors they tried, at a lower cost in time, and name its weakness: it needs frames long enough to adapt, which makes editing a compressed stream harder.

In the files decoded here the stages are: neighboring channels coded as differences, with a mid-like last channel; a fixed first-order predictor of 31/32 (15/16 for 8-bit samples); and an adaptive filter of 8 taps whose weights move by the signs of the history (sign-sign least mean squares). What is left is written with Rice codes ([Rice coding](rice-coding)) of two parameters that follow running sums, with no division of the frame into smaller parts that each carry a parameter.

**Inside a frame.** A decoder can start at any frame, so a frame does not depend on the ones before it. It carries no header: the compressed samples of the channels follow one another as in plain PCM, then the CRC-32. Nothing in it states a choice for the analyzer to report.

**What you see.** A frame shows its coded bytes as one run and its CRC-32; what varies from frame to frame in the tree is the length, which the bitrate and its distribution show. Where the samples are asked for, the decoder reconstructs them, and its output equals libtta-c's and FFmpeg's on Encspect's test files.
