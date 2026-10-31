---
id: alac
title: Apple Lossless (ALAC)
related: alac-prediction, alac-adaptive-golomb, mid-side-stereo, wasted-bits, channel-layouts
references: Apple Lossless Audio Codec source (macosforge/alac, Apache License 2.0), ALACMagicCookieDescription.txt
---

**The problem.** Lossless audio takes less space when the samples are predicted and only the prediction errors are stored, and a decoder must give back every input sample exactly.

**How it works.** Apple Lossless cuts the audio into frames, 4096 samples each unless the cookie says otherwise. Each frame is a list of elements: a single channel element (SCE) for one channel, a channel pair element (CPE) for two, a low-frequency element (LFE), then optional data stream, fill and end elements. A file with more channels lists several elements, such as one single channel element, two pairs and one more single channel element for six channels.

A compressed element states how its channels are mixed (for a pair, left/right, [left/side](left-side-stereo), [mid/side](mid-side-stereo) or a weighted mix), then, per channel, the filter that predicts it ([prediction](alac-prediction)); the prediction errors follow, written with an [adaptive Golomb code](alac-adaptive-golomb). An element may instead hold its samples as plain PCM (the escape flag), and a 24-bit or 32-bit stream may set aside its low bytes ([wasted bits](wasted-bits)).

**What you see in a file.** The decoder needs a configuration, the magic cookie: frame length, bit depth, the three Golomb parameters, channel count, sample rate and sizes. MP4 files keep it in an `alac` box inside the sample entry, Matroska in the track's CodecPrivate. A frame has no checksum, so the analyzer can check its structure (that the walk ends where the packet ends) but not its samples.

**Where it is specified.** Apple published the source and a cookie description; there is no other document.
