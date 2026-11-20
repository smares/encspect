---
id: correction-files
title: Correction files
related: optimfrog-blocks, wavpack-hybrid-mode, verification, crc
references: WavPack 4 and 5 binary file/block format (David Bryant, April 2020); libwavpack 5.6.0, src/open_utils.c (read_wvc_block, match_wvc_header); OptimFROG 5.100 (ofs with --correction) as a black-box writer of the files in Encspect's tests
---

**The problem.** A lossy file plays anywhere but cannot give the original audio back, and a lossless file can but is large. A pair of files offers both: the main file plays alone, and a second holds what the lossy coding left out.

**How it works.** The correction file is read together with its main file, one block for each block. WavPack's .wvc file holds blocks that libwavpack matches to the .wv file's blocks by block index, sample count and flags. In Encspect's test files (OptimFROG 5.100), an .ofc file holds one correction block for each data block of its .ofs file, in the same order. A decoder given both rebuilds the original audio; given the main file alone, the lossy audio.

**How Encspect finds it.** The host looks for the file, never the parser: the input's name with the correction file's extension in the same folder (x.ofs to x.ofc). A stream or standard input has no folder, so the pair is reported as unchecked.

**What "consistent" rests on.** For WavPack, the structure and the block checksums, as libwavpack uses them: each hybrid block takes the correction block with the same index, sample count and flags, the correction file states the same stream, and every correction block is whole and passes libwavpack's check. A correction file of another encode whose blocks agree in all of these passes; only decoding tells it apart, by the CRC of the lossless audio in each correction block's header. WavPack 3's correction file has no blocks, so only decoding tells: the pair is consistent where the audio decoded with it has the header's second CRC. For OptimFROG, which Encspect cannot decode, block counts and the CRC-32 of every block of both files. A correction block states no sample count, rate or position that is known, so a file of another encode with as many blocks passes: the two files belong together and are intact, not necessarily the decoded pair equal to the source audio.

**What you see.** A line under the stream with the status (consistent, inconsistent, missing, none written or unchecked), findings for each kind of problem, and the correction file's own tree, with the offsets of its own file. Only a consistent pair is called lossless with its correction file. A consistent OptimFROG pair also raises the claim that the file is the lossy part of a DualStream ([OptimFROG blocks](optimfrog-blocks)).
