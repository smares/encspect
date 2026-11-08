---
id: lossy-decoding
title: Decoding a lossy stream
related: withheld-samples, measurements, verification, samples-and-lost-samples, edit-lists-and-priming, gapless-playback
references: Encspect's design for lossy decoders (docs/design/lossy-decoders.md); RFC 6716, section 6
---

**The problem.** A lossless stream decodes to exactly the samples that were coded, and a checksum can prove it. A lossy stream has no such samples: its decoded audio is a reconstruction, and the specifications of most lossy codings leave the last bits of it to the decoder.

**What Encspect's decoding is.** It is a reconstruction made for [measurements](measurements) and for front ends that ask for the samples, never for playback. The decoder reads the coded syntax with the same code as the parser, adds only the reconstruction, and writes floating-point samples at full scale 1.0 without clipping, dithering or mixing the channels down. The parser's findings and tree are the same with or without it.

**Not bit-exact with other decoders.** Two correct decoders can differ in the last bits of a float, in the noise a format leaves to the decoder, or in how they conceal a lost frame. Encspect's cross-checks compare its samples with other decoders' by position, within a stated tolerance (docs/development.md), and list what a reference hides. They do not claim that the output is what a particular player outputs. The arithmetic itself is the same on every platform, so a report reads the same everywhere.

**No verdict.** A lossy stream stores no checksum over its decoded audio, so decoding says which units could not be decoded and how much output was held back ([withheld samples](withheld-samples)), and never that a stream is intact.

**Priming and trims.** A decoder outputs the samples its coding needs to start up, and positions stay untrimmed. Where the stream's own syntax states a range, as Opus does with its [pre-skip](opus-pre-skip), the measurements cover that range and count the rest as trimmed samples. Trims that only a container or an encoder states ([edit lists and priming](edit-lists-and-priming)) are not applied.
