---
id: optimfrog-blocks
title: OptimFROG blocks
related: crc, verification, riff-chunks, lossywav, optimfrog-modes
references: OptimFROG 5.100 SDK, SDK/Documentation/format.txt (compressed file format specification); OptimFROG 5.100 (ofr, off, ofs) as a black-box writer of the files in Encspect's tests
---

**The problem.** A lossless file should hold the audio's original header and footer, be decodable in independent pieces, and let a reader skip what it does not know.

**How it works.** Every part of an OptimFROG file is a block: a 4-character ID, a 4-byte length and the data. The main block ("OFR ") holds the number of samples over all channels, the sample type, the channel configuration, the sample rate, the encoder ID and the compression byte. "HEAD" keeps the bytes of the source file up to its sample data, "TAIL" those after it, and each "COMP" block holds compressed audio that can be decoded alone, with a CRC-32 over its data and its own sample count. "MD5 " and "RECV" are optional. The specification gives the order ['ID3'] "OFR " [...] "HEAD" "COMP"* "TAIL" [...] ["MD5 "] ["RECV"] ["APET"] ["TAG"], and asks a reader to skip blocks it does not know.

**What the files show.** In Encspect's test files from OptimFROG 5.100 the CRC-32 is zlib's, over the block's data after the CRC field, and the blocks add up to the main block's total. The sample counts are values over all channels: with `--seek normal` a block holds 882,000 values (20 seconds of mono at 44.1 kHz), with `--seek fast` 441,000, and with `--seek min` one block holds the file. The float encoder `off` writes the main block as "OFRX", which the specification does not define, with the same fields, and a "DETA" block after the data blocks, which it does not define either. The analyzer reads "OFRX" as the main block and shows "DETA" as a block of unknown ID, skipped by its length.

**What you see.** The blocks with their lengths, the main block's fields, every data block with its CRC-32 as stored and as computed, and the bitrate of each block. A CRC-32 that differs is a finding. The CRC-32s cover the compressed data, not the decoded audio: only OptimFROG's own library decodes it, so the MD5 over the audio is shown and not compared ([verification](verification)).
