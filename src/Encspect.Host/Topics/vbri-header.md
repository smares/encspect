---
id: vbri-header
title: The VBRI header
related: xing-header, bitrate-modes, encoder-detection
references: Fraunhofer VBRI header; ffmpeg libavformat/mp3dec.c
---

**What it is.** Fraunhofer's encoders write their own header into the first frame of a VBR stream, instead of a [Xing header](xing-header): the text "VBRI" 36 bytes into the frame, followed by a version, a delay, a quality, the stream's byte and frame counts, and a seek table.

**The seek table.** Unlike Xing's table of 100 percentages, the VBRI table lists the bytes each stretch of frames takes, scaled by a factor, with entries of 1 to 4 bytes. A player adds them up to find where a point in time lies.

**What you see.** The header's fields in the first frame, and the counts compared with the frames counted, as for a Xing header. The analyzer takes a VBRI header, which Fraunhofer's encoders write, to name Fraunhofer as the encoder with high confidence. A version other than 1 is reported as a warning, since ffmpeg then ignores the header.
