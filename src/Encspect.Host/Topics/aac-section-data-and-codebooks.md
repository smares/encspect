---
id: aac-section-data-and-codebooks
title: AAC sections, Huffman codebooks and escapes
related: huffman-coding, aac-windows-and-blocks, pns, intensity-stereo
references: ISO/IEC 14496-3 4.4.2, 4.6.2, 4.6.3, Annex 4.A
---

**The idea.** As in MP3's [Huffman coding](huffman-coding), most quantized spectral values are small, so short codewords for frequent values save bits. AAC lets the encoder pick, for each run of scalefactor bands, the codebook that fits their values best.

**Sections.** section_data splits the bands of each window group into sections, each a 4-bit codebook number and a length in bands. Codebook 0 marks bands whose values are all zero and cost nothing more. Codebooks 1 to 11 are Huffman codebooks: 1 to 4 code four values per codeword, 5 to 11 two; 1, 2, 5 and 6 include the sign, the others send a sign bit for each nonzero value. Each pair of codebooks covers a larger largest value: 1 for codebooks 1 and 2, 2, 4, 7 and 12, and 16 for codebook 11, where 16 escapes to an escape sequence of up to eight leading ones and a word four bits longer than them, for values up to 8191. Codebook 12 is reserved; 13 marks [noise substitution](pns) and 14 and 15 [intensity stereo](intensity-stereo) bands, which code no spectral values.

**Scalefactors.** Each band that is not zero then gets a scalefactor, coded as the difference to the previous one, starting from global_gain, with a codebook of its own of 121 entries; intensity positions and noise energies are coded the same way.

**How long.** The elements state no lengths: where a channel's data ends follows only from decoding every codeword, so a reader decodes the spectrum to find the next element.

**What you see.** Each channel's sections with their codebooks, the bits of its scalefactors and spectral data, and the share of each codebook among all bands in the statistics.
