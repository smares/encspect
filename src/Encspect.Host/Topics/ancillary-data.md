---
id: ancillary-data
title: Ancillary data
related: bit-reservoir, encoder-detection, mp3pro, mp3-surround, mpeg-multichannel
references: ISO/IEC 11172-3 2.4.1.5 to 2.4.1.7, 2.4.2.5 to 2.4.2.7
---

**What it is.** Bits in a frame that the audio does not use are ancillary data. In Layers I and II they follow the samples at the end of the frame. In Layer III they are what no frame's main data claims, between one frame's main data and the next frame's in the [bit reservoir](bit-reservoir). Decoders skip them.

**What it carries.** ISO/IEC 11172-3 leaves the content open, and several uses grew up:

- Encoders fill unused bits with their name and version. LAME writes "LAME3.100" and the like, which helps the [encoder detection](encoder-detection) where a file has no tag; other encoders fill with zeros or patterns of their own.
- Extensions that old decoders must not see: [mp3PRO](mp3pro) and [MP3 Surround](mp3-surround) put their data there, and so does the [MPEG-2 multichannel extension](mpeg-multichannel) in Layer II.
- Fraunhofer's encoders put an Original File Length block at the start of the first frame's ancillary data, for gapless playback.

**What you see.** Ancillary Data nodes in frames, and in the statistics the ancillary bytes as mp3guessenc counts them, with their share, data rate and smallest and largest amount per frame. Where an extension or a signature is found, the region shows it.
