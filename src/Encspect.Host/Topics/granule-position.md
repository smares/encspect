---
id: granule-position
title: Granule positions
related: ogg-pages, gapless-playback, speex-modes
references: RFC 3533, sections 4 and 6; Speex manual, section 7.3; Vorbis I specification, appendix A.2; RFC 7845, section 4
---

**What it is.** Ogg has no notion of time. Instead, every page carries a 64-bit granule position, a marker whose meaning the codec's mapping defines. For audio it counts samples: the position at the end of the last packet that ends on the page. A page on which no packet ends has the granule position -1. Positions never go back within a stream, so a reader takes the duration from the last page and seeks by searching for the page with the position it wants.

**How codecs define it.** Each codec counts in its own units. Speex and Vorbis count samples at the stream's sample rate, Opus always at 48 kHz. Pages that hold only header packets have the position 0.

**Start offset.** An encoder's first output samples may be its own delay rather than audio. speexenc and ffmpeg shift Speex positions by that delay, the lookahead, so the first audio page's position is smaller than the samples its packets decode to. The difference is what a player drops at the start.

**End trimming.** The last packet usually codes more samples than the source had, since frames have a fixed size. The last page's position says where the audio really ends, and the decoder drops what lies beyond it. How far a decoder trims depends on the codec: speexdec trims only within the last packet. Together, the start offset and the end trimming give gapless playback without a separate tag.

**What you see.** The granule position of every page and the last one of each stream, and in the statistics the encoder delay, the end trimming, the samples and the duration they give, with findings where positions go back or do not match the packets.
