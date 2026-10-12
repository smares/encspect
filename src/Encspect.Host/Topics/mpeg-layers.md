---
id: mpeg-layers
title: Layers I, II and III
related: subband-coding, filter-bank, huffman-coding, bit-reservoir, mpeg-versions
references: ISO/IEC 11172-3 2.4; ISO/IEC 13818-3
---

MPEG audio defines three layers, three codings of increasing complexity that share the frame header and the filterbank.

**Layer I** splits the audio into 32 subbands with a polyphase filterbank and codes 12 samples of each subband per frame with a bit allocation and a scalefactor ([subband coding](subband-coding)). It was used by the Digital Compact Cassette.

**Layer II**, known as MP2, codes 36 samples per subband and frame, with finer allocation tables and scalefactors shared across the frame where they change little. It became the audio of DAB radio, Video CD and many broadcast systems.

**Layer III**, known as MP3, splits each subband further with a transform into 18 frequency lines ([filter bank](filter-bank)), quantizes them non-uniformly, codes them with Huffman tables ([Huffman coding](huffman-coding)), switches to short blocks for sudden sounds ([block switching](block-switching)), and lets frames borrow unused bytes of earlier frames ([bit reservoir](bit-reservoir)). It reaches a given quality at lower bitrates than the other layers, at the cost of more work in the encoder and decoder.

Each layer is a separate coding: a file is normally one layer throughout, and the Layer field of every frame header says which.
