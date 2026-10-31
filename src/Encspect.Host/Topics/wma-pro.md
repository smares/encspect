---
id: wma-pro
title: Windows Media Audio Professional
related: wma-packets, wma-subframes, wma-channel-transforms, wma-scale-factors, wma-coefficients, wma-standard, wma-lossless, asf-container, waveformatex
references: Encspect's format description docs/design/wma-pro-format.md (Microsoft has published no specification)
---

**The problem.** Multichannel music at 24 bits and sampling rates up to 96 kHz needs a coder that handles more channels and finer detail than the first Windows Media Audio versions ([Windows Media Audio, versions 1 and 2](wma-standard)). Windows Media Audio Professional (format tag 0x0162) is the transform codec of the Windows Media platform for that.

**How a stream is laid out.** The container hands over one media object per packet. A packet holds the end of the frame that began in an earlier packet, whole frames, and the start of a frame that goes on ([packets](wma-packets)). A frame holds the same number of samples of every channel, and a tile header divides each channel's share into subframes ([subframes](wma-subframes)). Each subframe codes its channels' spectrum: a channel transform for groups of channels ([channel transforms](wma-channel-transforms)), a quantization step and scale factors ([scale factors](wma-scale-factors)), and the coefficients themselves ([coefficients](wma-coefficients)).

**The codec-specific data.** Eighteen bytes: the sample size, a channel mask, and decode flags that set the frame length, the most subframes a channel may have, whether frames start with their length, and whether they carry a dynamic range gain.

**What you see in a file.** Encspect reads the packets, the frames in them and each frame's layout, down to how many non-zero coefficients each channel of a subframe codes. It does not decode the audio. The samples it reports are the frames' samples less the first frame, which only fills the overlap of the transform, and less the trims frames state.

**Where it is specified.** Microsoft has published no specification. The layout is described in the project's own format description, which was checked against files written by Microsoft's encoders.
