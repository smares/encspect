---
id: aiff-container
title: AIFF and AIFF-C files
related: riff-chunks, aiff-common-chunk, aiff-sound-data, aiff-markers-and-loops, id3v2-in-chunks
references: Apple, Audio Interchange File Format AIFF-C (draft of 8/26/91), sections 1 to 3; Apple, Audio Interchange File Format AIFF, version 1.3; Electronic Arts, EA IFF 85
---

**The problem.** Sound files for the Apple II and the Macintosh had to hold samples, a few parameters and whatever else an application wanted to add, without breaking other applications.

**How it works.** AIFF is a file of the EA IFF 85 kind: one FORM chunk, holding chunks of a four-character ID, a 32-bit size and the data, with a zero pad byte when the size is odd. Unlike RIFF, all integers are big-endian. The FORM chunk's type is AIFF for uncompressed sound and AIFC for AIFF-C, which can hold compressed sound as well. The Common Chunk (COMM) and the Sound Data Chunk (SSND) are required, unless the sound has no frames; there is no order among chunks. An AIFC file also holds a Format Version Chunk (FVER).

Other chunks are optional and at most one of each, apart from application chunks, annotations and MIDI data: markers (MARK), an instrument (INST), comments (COMT), the name, author and copyright text, an audio recording chunk (AESD) and MIDI data. Applications store their own data in APPL chunks. Taggers add an ID3v2 tag in a chunk with the ID "ID3 ".

**What you see.** The FORM chunk with its type, each chunk with its fields, and the pad bytes. A chunk whose size runs past the end, a missing required chunk or a second chunk of a kind the specification allows once is reported as a finding.
