---
id: truehd-restart-header
title: The restart header
related: truehd-decoding-parameters, truehd-lossless-check, truehd-substreams, crc
references: Encspect's TrueHD and MLP format description (docs/design/truehd-format.md), sections "Restart header" and "Values a restart header resets"
---

**The problem.** Decoding parameters change a little from block to block, so a decoder that joins the stream needs a point where every parameter is stated afresh.

**How it works.** A restart header begins with the 13-bit sync 0x18F5. It then gives the noise type, an output timing counter, the first and last coded channel, the last matrix channel, the noise shift and seed, whether blocks carry size check data, the stored lossless check and, for each channel of the presentation, its position in the channel list. An 8-bit CRC closes it. After it, the substream's parameters are reset: all parameter groups may be present, no matrices, block size 8, output shifts and quantizer steps 0, and for each coded channel no filters, Huffman offset 0, no codebook and 24 least significant bits. The filters keep their sample histories.

**Where it sits.** In Encspect's test files (the three real files and files written by FFmpeg 6.1.1's `truehd` and `mlp` encoders), a restart header opens the first block of every substream in every unit that has a major sync, and appears nowhere else; a crafted file (`built-substream-without-restart-header`) is the exception. The analyzer does not rely on this and reads a header wherever a block states one. A substream whose first block has none cannot be decoded for that unit.

**What you see.** The restart header's fields under the first block of each substream with its CRC and the CRC computed over the fields. A sync that is not 0x18F5, a CRC that differs, a channel position the presentation lacks, or a first block without a header is a finding.
