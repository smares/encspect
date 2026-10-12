---
id: mpeg-multichannel
title: The MPEG-2 multichannel extension
related: ancillary-data, subband-coding, mpeg-versions, mp3-surround
references: ISO/IEC 13818-3
---

**The problem.** MPEG-2 wanted surround sound without leaving behind the many MPEG-1 decoders that only play stereo.

**How it works.** The encoder mixes the multichannel programme, up to 5.1 channels plus additional language channels, down into two channels by a known matrix, and codes those as an ordinary MPEG-1 stream, which every decoder plays. The extra channels' data, which an MPEG-2 decoder needs to undo the matrix and recover all channels, travels in the [ancillary data](ancillary-data) of each frame, where MPEG-1 decoders skip it. Tools such as dynamic crosstalk, where channels share some subbands, and prediction between channels save bits. Where the data does not fit the base frame, it continues in a separate extension bitstream.

**What you see.** In Layer II files the analyzer reads the multichannel header (center, surround and LFE channels, the matrix procedure, multilingual channels), the configuration such as 3/2 + LFE, its CRC and the extra channels' bit allocation. The frame header has no flag for the extension, so the analyzer accepts it only when its CRC holds across frames, as random ancillary data would not. The ISO conformance streams show every configuration.
