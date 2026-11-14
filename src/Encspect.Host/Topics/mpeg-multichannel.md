---
id: mpeg-multichannel
title: The MPEG-2 multichannel extension
related: ancillary-data, subband-coding, mpeg-versions, mp3-surround, channel-layouts
references: ISO/IEC 13818-3 2.5.1.12 to 2.5.1.17, 2.5.2.13 to 2.5.2.17, 2.5.3.1, 2.5.3.2.1, 2.5.3.2.4, 2.5.3.2.5
---

**The problem.** MPEG-2 wanted surround sound without leaving behind the many MPEG-1 decoders that only play stereo.

**How it works.** The encoder mixes the multichannel programme, up to 5.1 channels plus additional language channels, down into two channels by a known matrix, and codes those as an ordinary MPEG-1 stream, which every decoder plays. The extra channels' data, which an MPEG-2 decoder needs to undo the matrix and recover all channels, travels in the [ancillary data](ancillary-data) of each frame, where MPEG-1 decoders skip it. Tools such as dynamic crosstalk, where channels share some subbands, and prediction between channels save bits. Where the data does not fit the base frame, it continues in a separate extension bitstream.

A multichannel decoder first decodes the stereo base, then the additional transmission channels. Which channels they carry (the center, a surround channel, or a left or right channel the base mixes) is chosen per group of subbands, and the decoder undoes the matrix in the subbands with the equations for that choice and the stream's matrix procedure, then multiplies each channel by the factor that undoes the encoder's attenuation. The LFE channel is coded at 1/96 of the sample rate.

The standard defines the extension for Layers I, II and III. **What you see.** In Layer II files the analyzer reads the multichannel header (center, surround and LFE channels, the matrix procedure, multilingual channels), the configuration such as 3/2 + LFE, its CRC and the extra channels' bit allocation. The frame header has no flag for the extension, so the analyzer accepts it only when its CRC holds across frames, as random ancillary data would not. The stream then reports the configuration's channels as its [channel layout](channel-layouts), with multilingual channels counted apart. Encspect decodes all channels where the whole extension is in the base frames; where an extension bitstream, a separate file, holds part of it, the decoder outputs the base stereo. The ISO conformance streams show every configuration.
