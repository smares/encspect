---
id: wave-compressed-formats
title: Compressed formats in WAV
related: waveformatex, pcm-samples, riff-chunks
references: Microsoft, Multimedia Data Standards Update, April 1994 (WAVE types, MPEG-1 audio, fact chunk); Microsoft, mmreg.h
---

**The problem.** A WAV file was made for PCM, yet it can hold any coding for which Microsoft registered a format tag: ADPCM, GSM, MPEG audio and many more. Each needs figures its decoder starts from.

**How it works.** The format tag in the fmt chunk names the coding, and cbSize announces extra bytes after the common fields. For Microsoft ADPCM they are the samples per block and the table of prediction coefficients; for IMA ADPCM and GSM 6.10 the samples per block; for MPEG audio the layers, bitrate, modes and emphasis of the stream (the MPEG1WAVEFORMAT) or, for Layer III, an identifier, padding flags, block size and codec delay (the MPEGLAYER3WAVEFORMAT). Because the data is not a plain list of samples, its size does not give the number of samples: the fact chunk states it, and the specification requires the chunk for every format but PCM.

The data chunk of an MPEG audio stream, an AC-3 stream or a DTS stream holds the same frames a file of that coding would hold. The analyzer passes it to the parser of that coding, so the frames are reported as in a file of their own.

**What you see.** The format tag with the coding's name, the extra fields the specifications give, the fact chunk, and for MPEG audio, AC-3 and DTS the frames. A coding the analyzer has no parser for is described by the figures the fmt and fact chunks state, with a note that its audio is not read.
