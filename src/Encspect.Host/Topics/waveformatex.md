---
id: waveformatex
title: WAVEFORMATEX
related: asf-container, channel-layouts, wma-standard, wave-format-extensible, wave-compressed-formats, riff-chunks
references: Advanced Systems Format (ASF) Specification, revision 01.20.03, sections 9.1 and 11.1; Microsoft Windows SDK, mmreg.h; Microsoft, Multimedia Programming Interface and Data Specifications 1.0, chapter 3 (WAVE format chunk)
---

**The problem.** Several containers, among them WAV, AVI and ASF, need to say how audio is coded, and all want the same answer without each defining its own fields.

**How it works.** They share Microsoft's WAVEFORMATEX structure: a format tag that names the codec (1 for PCM, 0x0055 for MPEG audio layer III, 0x0160 to 0x0163 for the Windows Media Audio codecs), the number of channels, the sample rate, the average bytes per second, the block alignment and the bits per sample. Its last field, cbSize, gives the number of codec-specific bytes that follow, which hold whatever the codec needs: for Windows Media Audio, the coding options. The block alignment is the unit audio is cut into: audio payloads are multiples of it, and for Windows Media Audio it is the size of a superframe.

**Extensible.** A format tag of 0xFFFE announces WAVEFORMATEXTENSIBLE. Its extra bytes start with the valid bits per sample, a channel mask that says which speakers the channels feed, and a sub-format GUID whose first 32 bits are the codec's real format tag ([WAVEFORMATEXTENSIBLE](wave-format-extensible)).

**What you see.** The type-specific data of an audio stream in an ASF file and the fmt chunk of a WAV file are shown field by field, with the format tag named. In ASF the codec-specific bytes are passed on to the parser of the codec; in WAV the figures the specifications give for the tag are read, and the data chunk of MPEG audio, AC-3 and DTS goes to the parser of that coding ([compressed formats](wave-compressed-formats)).
