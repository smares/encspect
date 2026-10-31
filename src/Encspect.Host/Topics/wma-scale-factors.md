---
id: wma-scale-factors
title: Windows Media Audio quantization step and scale factors
related: wma-pro, wma-coefficients, scalefactors, huffman-coding
references: Encspect's format description docs/design/wma-pro-format.md
---

**The problem.** Quantization decides how coarse the coefficients are, and the coder has to tell the decoder. A coarse step saves bits, and the step can differ between parts of the spectrum so that the error stays where it is least heard.

**The quantization step.** Each subframe that sends coefficients states one step. It starts from a value that follows the sample size (90 for 16 bits, 135 for 24 bits), a signed six-bit change is added, and the end values of that change are followed by groups of five bits that extend the range. In a subframe of several channels a three-bit width and one bit per channel add a modifier to individual channels.

**Scale factors.** The spectrum is divided into bands that follow the critical bands of hearing, up to 28 of them depending on the subframe length and the sampling rate. Each channel has one scale factor per band. A channel's first subframe in a frame always sends them; later ones send a flag. A new set is coded as differences from band to band with a step of 1 to 4 and Huffman codes. A later set is coded as differences from the last set: Huffman codes of run and level pairs, or an escape, until an end code.

**What you see in a file.** Encspect shows the quantization step of each subframe and, for each channel that sends coefficients, whether its scale factors are a new set, differences from the last set, or as before. Its statistics count how often each occurs.
