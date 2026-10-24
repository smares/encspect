---
id: truehd-lossless-check
title: The TrueHD lossless check
related: verification, truehd-restart-header, truehd-substreams, crc
references: Encspect's TrueHD and MLP format description (docs/design/truehd-format.md), sections "Lossless check" and "Checksums and check values"
---

**The problem.** The checks on the bytes of a unit say the stored bytes are intact. They do not say the decoder rebuilt the samples the encoder had.

**How it works.** Each restart header stores 8 bits computed over the decoded output of its substream since the substream's previous restart header. A decoder keeps a 32-bit accumulator. For every output sample and matrix channel it XORs in the low 24 bits of the sample, shifted left by the channel's index. At the next restart header it folds the accumulator to 8 bits by XOR of its four bytes and compares the result with the stored byte. Each presentation has its own check, from its own substream.

**What is compared.** The first restart header of a stream, and the first after an end of stream marker, have nothing to compare with, and the output after the last restart header has no check stored. So the compared checks together cover the output from a substream's first restart header to its last.

**What you see.** The stored lossless check in each restart header, and in the verification how many were compared and how many held. Comparing them takes decoding. A check that differs is a warning that names the substream; the analyzer reports it and keeps decoding.
