---
id: wma-lossless
title: Windows Media Audio Lossless
related: wma-lossless-subframes, wma-packets, wma-subframes, wma-pro, asf-container, waveformatex, channel-layouts
references: Encspect's format description docs/design/wma-lossless-format.md (Microsoft has published no specification)
---

**The problem.** A lossless coder has to give back every sample, so it cannot discard what the ear misses the way the transform codecs ([Windows Media Audio Professional](wma-pro), AAC, MP3) do. Instead it predicts each sample from the ones before and stores only the error, the residual.

**How a stream is laid out.** Windows Media Audio Lossless (format tag 0x0163) uses the packets, frames, tile headers and subframes of Professional ([packets](wma-packets), [subframes](wma-subframes)). The codec-specific data has the same 18 bytes: the sample size, 16 or 24 bits, the channel mask and the decode flags. Bit 8 of the flags says that subframes carry an LPC flag.

**What a frame holds.** A subframe is either raw, with the samples as they are, or it holds residuals for the channels it codes ([subframe coding](wma-lossless-subframes)). A subframe that is seekable states every filter parameter and resets the state, so a decoder can start at it. A coded subframe is read only after a seekable one; a raw subframe can be read at any time.

**What you see in a file.** Encspect reads the packets, the frames in them and each frame's layout, down to where the residuals of every channel end. It does not run the filters that turn residuals into samples, so it does not check the samples; the samples it reports are the frames' samples less the trim at the end of each. In Encspect's three committed Lossless test files, written by Microsoft's Windows Media encoder (wmvcore 12.0.26100; its settings are not recorded in the files), every frame is one subframe of 2,048 samples of all channels, and the frames do not start with their length, so a frame's end is found by reading it. Encspect reads any layout the format allows and does not rely on these observations.

**Where it is specified.** Microsoft has published no specification. The layout is described in the project's own format description, which was checked against files written by Microsoft's encoders and by a decoder.
