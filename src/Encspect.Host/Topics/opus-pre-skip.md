---
id: opus-pre-skip
title: Opus pre-skip, output gain and granule positions
related: granule-position, gapless-playback, opus-toc, replaygain
references: RFC 7845, sections 4 and 5.1
---

**Pre-skip.** The first samples an Opus decoder outputs are not audio: the decoder's overlap and stereo mixing, and the encoder's own analysis and resampling, delay the signal, so the encoder starts with padding. The identification header's pre-skip says how many samples, at 48 kHz, a player drops after decoding them. They still have to be decoded, since each frame's output depends on the frames before it. A pre-skip of at least 80 ms is recommended when a stream is cut at its start.

**Granule positions.** Ogg pages of Opus count [granule positions](granule-position) in samples at 48 kHz, whatever the input's sample rate, and the count includes the pre-skip, so a page's playback time is its granule position less the pre-skip, over 48,000. Each page's position must equal the previous one plus the samples of the packets that end on it. The first audio page's may be larger than its samples, which starts the stream later, as when a stream is cut from a longer one, but never smaller.

**End trimming.** Only the end-of-stream page may give fewer samples than its packets decode to; the decoder then drops the rest, which ends the stream between frame boundaries. Pre-skip and end trimming together give [gapless playback](gapless-playback).

**Output gain.** The header also carries a gain in steps of 1/256 dB that players apply to the decoded audio by default, so a stream's level can change without re-encoding. Any gain from tags such as R128_TRACK_GAIN comes on top of it.

**What you see.** The header's Pre-skip and Output Gain, and in the statistics the decoded samples, start offset, end trimming, samples and duration, with findings where positions do not match the packets.
