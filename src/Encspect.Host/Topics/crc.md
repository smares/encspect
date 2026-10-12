---
id: crc
title: The frame CRC
related: frame-header, lame-tag, granules-and-frames, adts
references: ISO/IEC 11172-3 2.4.3.1; ISO/IEC 14496-3 1.A.2.2.2
---

**What it is.** A frame may carry a 16-bit cyclic redundancy check right after its header. The protection bit of the header says whether it does. The encoder computes the CRC over the last 16 bits of the header and the bits after the CRC that matter most for decoding the rest: the side information in Layer III, the bit allocation in Layer I, and the bit allocation and scfsi in Layer II. A decoder computes it again and, where it differs, knows those bits were damaged and can conceal the frame instead of producing loud errors.

**What it does not cover.** The CRC does not protect the audio data itself, such as Layer III's main data, which may lie in earlier frames. A matching CRC says the frame's control data is intact, not that every bit is.

**In practice.** Most encoders write no CRC by default; LAME writes one with -p. The two bytes cost a little bitrate.

**In AAC.** An [ADTS](adts) header may carry the same CRC-16. It covers the header and the first bits of every channel element, so checking it means knowing where each element ends; where that takes the spectral data, which is not read yet, the analyzer says the CRC is not verified.

**What you see.** CRC and Computed under each protected frame, and a finding for frames whose CRCs differ. Other CRCs in a file serve other purposes: the LAME tag has a CRC of its own and one over the music, and the multichannel extension carries its own.
