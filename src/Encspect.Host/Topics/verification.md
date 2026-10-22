---
id: verification
title: Verified streams and stored checksums
related: crc
references: RFC 9639 (FLAC), sections 8.2, 9.1.8 and 9.3
---

**The problem.** A lossless file can carry checksums that let a program tell whether the audio is intact. What a checksum vouches for depends on the bytes it covers.

**What a checksum covers.** A checksum over a header, such as the CRC-8 of a FLAC frame header, says the header is intact and nothing about the audio. A checksum over coded data, such as the CRC-16 of a whole FLAC frame, says those bytes are intact; the analyzer compares it in every analysis because it only needs the bytes. A checksum over decoded audio, such as the MD5 that a FLAC STREAMINFO block stores for all samples, can only be compared after the samples are rebuilt, which takes decoding. A FLAC MD5 of zero means the encoder did not compute it.

**What verified means here.** A stream is verified when every checksum it stores was compared and holds, and at least one of them covers all of its audio. A checksum that every frame carries covers all of the audio only where no frame is missing: a file cut after a whole frame, or with frames removed, can pass every frame checksum and still be incomplete. A checksum that differs makes the stream not verified; one that was not compared leaves it unknown.

**What you see.** A "Verified" line under the stream, with the checksums and how many values were compared of those stored, and in JSON a verification record. Decoding is off unless you ask for it (the --verify option of the command line), so a checksum over decoded audio then reads "not compared: decoding was not asked for". Where a stream is verified but its audio is a lossy coding's output, the line says so.
