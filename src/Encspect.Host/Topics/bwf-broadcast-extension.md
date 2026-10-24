---
id: bwf-broadcast-extension
title: Broadcast Wave Format metadata
related: riff-chunks, rf64, wave-cue-points
references: EBU Tech 3285 (Broadcast Wave Format) and its supplements 1 to 7; EBU R 128
---

**The problem.** Broadcasters exchange audio files and need to know where a recording came from, when, how it was coded, and how loud it is, in a form that any WAV reader still opens.

**How it works.** The Broadcast Wave Format is a WAV file with a bext chunk. It holds a description, the originator and its reference, the date and time of creation, a time reference that counts the samples from midnight to the first sample, a version number, a SMPTE UMID (from version 1), loudness values (from version 2) and a coding history: one line for each coding step the sound went through. The loudness fields give integrated loudness, loudness range, maximum true peak and the highest momentary and short-term loudness, each multiplied by 100 in a 16-bit integer, where 0x7FFF means the value is not used.

Supplements add chunks: a peak envelope, a link to the other files of a recording split in parts, XML metadata (axml), Dolby metadata and the chna chunk, which ties each track to the Audio Definition Model. Other makers add the iXML chunk for production metadata.

**What you see.** The fields of the bext chunk, the loudness values in their units, the coding history as text, and the XML chunks as text. Chunks the analyzer recognizes but does not read are named and sized.
