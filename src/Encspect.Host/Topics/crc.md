---
id: crc
title: The frame CRC
related: dts-core-frames, dts-extension-substream, wavpack-sub-blocks, frame-header, lame-tag, granules-and-frames, adts, ac3-frames-and-bsi, musepack-sv8-packets
references: ISO/IEC 11172-3 2.4.3.1; ISO/IEC 14496-3 1.A.2.2.2; ATSC A/52:2018 7.10.1; ETSI TS 102 114 5.4.2, Annex B
---

**What it is.** A frame may carry a 16-bit cyclic redundancy check right after its header. The protection bit of the header says whether it does. The encoder computes the CRC over the last 16 bits of the header and the bits after the CRC that matter most for decoding the rest: the side information in Layer III, the bit allocation in Layer I, and the bit allocation and scfsi in Layer II. A decoder computes it again and, where it differs, knows those bits were damaged and can conceal the frame instead of producing loud errors.

**What it does not cover.** The CRC does not protect the audio data itself, such as Layer III's main data, which may lie in earlier frames. A matching CRC says the frame's control data is intact, not that every bit is.

**In practice.** Most encoders write no CRC by default; LAME writes one with -p.

**In AAC.** An [ADTS](adts) header may carry the same CRC-16. It covers the header and the first bits of every channel element, so checking it means knowing where each element ends, which takes decoding its spectral data; the analyzer reads every element and verifies it, and says it is not verified only where an element cannot be read.

**In AC-3.** An [AC-3 frame](ac3-frames-and-bsi) has crc1 over its first 5/8 and crc2 over all of it; E-AC-3, one.

**In DTS.** The headers of an [extension substream](dts-extension-substream), of XXCH and of XBR end in a CRC-16 that is verified. A [core frame](dts-core-frames) with CPF set has CRC words that the specification says not to test, so they are reported as not verified.

**What you see.** CRC and Computed under each protected frame, and a finding for frames whose CRCs differ. Other CRCs serve other purposes: the LAME tag has a CRC of its own and one over the music, the multichannel extension its own, every [Ogg page](ogg-pages) a CRC-32 over the page, Musepack SV8's stream header a CRC-32 ([SV8 packets](musepack-sv8-packets)), and each WavPack block a checksum ([sub-blocks](wavpack-sub-blocks)).
