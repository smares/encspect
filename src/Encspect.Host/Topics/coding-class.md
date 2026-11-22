---
id: coding-class
title: Coding class
related: correction-files, wavpack-hybrid-mode, lossywav, verification, spectral-cutoff, bit-depth-usage, lossy-decoding
references: the codings' own specifications, as each format's page lists them; ITU-T G.711 (A-law and mu-law); RFC 9639 (FLAC)
---

**What it is.** The coding class says what kind of reconstruction a stream's coding defines. It is read from the coding, not from the audio. The report words it as "lossless coding", "lossy coding" or "hybrid coding".

**The three classes.**
- **Lossless coding** gives back exactly the samples it was given: linear and floating-point PCM, FLAC, Apple Lossless, Monkey's Audio, TTA, TAK, WavPack and OptimFROG without a lossy part, TrueHD and MLP, DTS-HD Master Audio.
- **Lossy coding** drops information by design. A-law and mu-law round 13- or 14-bit samples to 8 bits (ITU-T G.711), ADPCM codes sample differences with a few bits, and perceptual codecs such as MPEG audio, AAC, AC-3, Vorbis and Opus drop what they judge least audible.
- **Hybrid coding** is a lossy stream plus an optional correction file that makes it lossless ([correction files](correction-files)). A hybrid stream is lossless with its correction file and lossy without it, and the line beside the class says which.

**What it cannot show.** The class describes how the stream is coded now. A lossless coding can hold audio that was lowpassed, truncated or lossy-decoded before it was stored, and a lossy coding can hold audio from a lossless source. A coding whose class the stream alone does not settle, such as an unlisted one, has no class.

**The figures beside it.** The processor claim names software that changed the audio before encoding, from a mark it left ([lossyWAV](lossywav)). The spectral cutoff is where the decoded audio's spectrum ends ([spectral cutoff](spectral-cutoff)), and the low bits unused are the bits that are 0 in every sample ([bit depth](bit-depth-usage)). They are figures with a basis, not verdicts.

**Verified.** "Verified" says what the checksums cover: that the decoded audio, or the coded data, matches what the file stores ([verification](verification)). It says nothing about where the audio came from.
