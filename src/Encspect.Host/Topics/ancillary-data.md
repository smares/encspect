---
id: ancillary-data
title: Ancillary data
related: bit-reservoir, encoder-detection, mp3pro, mp3-surround, mpeg-multichannel
references: ISO/IEC 11172-3 2.4.1.8, 2.4.2.8; ISO/IEC 13818-3 2.5.1.9
---

**What it is.** Bits in a frame that the audio does not use are ancillary data. The standard counts them as the bits an audio frame has available minus those its header, error check and audio data use. In Layers I and II that is the space between the end of the audio data and the next header, the padding slot included. In Layer III it is the space between the end of a frame's Huffman code bits and the place the next frame's main_data_begin points to, in the [bit reservoir](bit-reservoir). Decoders skip them.

**What it carries.** ISO/IEC 11172-3 leaves the content user definable, and several uses grew up:

- Encoders fill unused bits with their name and version. LAME writes "LAME3.100" and the like, which helps the [encoder detection](encoder-detection) where a file has no tag; other encoders fill with zeros or patterns of their own.
- Extensions that old decoders must not see: [mp3PRO](mp3pro) and [MP3 Surround](mp3-surround) put their data there, and so does the [MPEG-2 multichannel extension](mpeg-multichannel) in Layer II.
- Fraunhofer's encoders put an Original File Length block at the start of the first frame's ancillary data, for gapless playback.

**What you see.** Ancillary Data nodes in frames, and in the statistics the ancillary bytes as mp3guessenc counts them, with their share, data rate and smallest and largest amount per frame. Where an extension or a signature is found, the region shows it.
