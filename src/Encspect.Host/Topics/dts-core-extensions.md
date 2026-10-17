---
id: dts-core-extensions
title: DTS core extensions: XCh, X96 and XXCH
related: dts-core-frames, dts-extension-substream, dts-channel-arrangements, channel-layouts
references: ETSI TS 102 114 V1.6.1 (2019-08), 6.2, 6.4, 6.5; FFmpeg 6.1.1 libavcodec/dca_parser.c
---

**The problem.** The 5.1 core at 48 kHz had to grow to more channels and higher frequencies without breaking the decoders already sold, which play the core and ignore the rest.

**How it works.** Extensions sit at the end of the core frame, inside the size FSIZE gives, behind the core's own data:

- XCh adds one channel, a center surround, which makes 6.1 (the profile FFmpeg names DTS-ES). Its sync word is 0x5A5A5A5A.
- X96 adds the high frequencies of a 96 kHz signal, in 64 subbands instead of the core's 32 (DTS 96/24 in FFmpeg). Its sync word is 0x1D95F262.
- XXCH adds channel sets with more channels, beyond 5.1, each placed by a speaker mask. Its sync word is 0x47004A03.

The header's EXT_AUDIO_ID names which extension is present. Each begins on a 32-bit boundary with a sync word. XCh and X96 state a size, which tells a true sync word from the same bytes in audio data: the distance from it to the frame's end must match. XXCH states the size of its header and its channel sets, and a CRC-16 over the header. The analyzer finds them from the end of the frame.

**What you see.** Each extension's header fields, with the sampling frequency and layout it adds. The stream's extension rate is a field apart from the core's rate. The names DTS-ES and DTS 96/24 are the profile names FFmpeg gives, not the specification's. The extensions' own audio data is not decoded.
