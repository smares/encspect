---
id: wma-lossless
title: Windows Media Audio Lossless
related: wma-pro, asf-container, waveformatex, channel-layouts, encoder-detection
references: FFmpeg 6.1.1 libavcodec/wmalosslessdec.c, wmaprodec.c
---

**The problem.** A lossless coder has to give back every sample exactly, so it cannot discard what the ear misses the way the transform codecs ([Windows Media Audio Professional](wma-pro), AAC, MP3) do. Instead it predicts each sample from the ones before and stores only the error.

**How the stream is laid out.** Lossless (format tag 0x0163) uses the same packets, frames and subframes as Professional: a packet header with a sequence number and the bits that finish the frame before, frames that start with their length, and a tile header that divides a frame into subframes per channel. The codec-specific data has the same 18 bytes: the sample size (16 or 24 bits), the channel mask and the decode flags.

**How a subframe is coded.** A subframe either holds raw samples, or for each channel the residuals of prediction, entropy coded with parameters that follow a running average of the earlier residuals. A decoder undoes the prediction in turn: a cascade of adaptive filters for each channel, a filter across channels, a decorrelation between channels and a further filter, and scales by the quantization step if it is not 1 (wmalosslessdec.c).

**What you see in a file.** The analyzer reads the codec-specific data and counts the packets. The frames are not read yet.

**Where it is specified.** Microsoft has published no specification the project could check. FFmpeg's decoder, written from the format, is the reference here, and decoding every Lossless fixture bit for bit against its source signal confirms the files.
