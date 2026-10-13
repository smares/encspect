---
id: asf-container
title: The ASF container
related: asf-packets, asf-metadata, asf-index, waveformatex, wma-standard
references: Advanced Systems Format (ASF) Specification, revision 01.20.03, sections 2, 3, 4 and 10; FFmpeg libavformat/asfdec_f.c
---

**The problem.** Windows Media files carry audio, video and scripts together, for playing from a disk and for streaming over a network. The container has to describe each stream, interleave them in the order they are needed, and let new kinds of information be added without breaking old readers.

**How it works.** An ASF file is a run of objects. Each starts with a 128-bit GUID that says what it is and a 64-bit size, in little-endian byte order, followed by its data. A reader skips objects it does not know. The Header Object comes first and holds other objects: the File Properties Object (duration, packet size, flags), a Stream Properties Object for every stream, the Header Extension Object (which holds further objects, such as the Extended Stream Properties, Language List and Metadata objects), the Codec List, the Content Description and the Padding Object, which lets the header grow without moving the data. The Data Object follows, and the index objects come last.

**What you see.** The tree shows every object with its GUID, its size and its fields at their bytes. Objects that the specification does not define are shown with their GUID and size, and noted as unknown. The File Properties Object's file size and packet count are compared with what the file holds.

**Specified in** the Advanced Systems Format Specification, which Microsoft publishes. Windows Media Audio files (.wma), Windows Media Video files (.wmv) and plain .asf files all use it.
