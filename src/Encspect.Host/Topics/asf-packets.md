---
id: asf-packets
title: ASF data packets and payloads
related: asf-container, asf-index
references: Advanced Systems Format (ASF) Specification, revision 01.20.03, sections 5.2 and 7.3; FFmpeg libavformat/asfdec_f.c
---

**The problem.** A stream is a series of media objects: an audio superframe, a video frame. They differ in size, but a network or a disk wants equal pieces, and several streams must share the connection.

**How it works.** The Data Object holds data packets of one size, sorted by the time they are to be sent. A packet may start with error correction data. Its payload parsing information follows: flags that give the width of each field, then the packet length, padding length, send time and duration. After that come one or more payloads and padding. A payload is a piece of a media object, or a whole one: its header gives the stream number, a key frame bit, the number of the media object, the offset of the piece in it and replicated data, which repeats in every piece of an object and starts with the object's size and presentation time. A media object that does not fit in the packet continues in the next one. Several short objects of one stream can be packed as a compressed payload, which spares the headers.

**What you see.** Each packet with its flags, lengths and times, and each payload with its fields; the statistics count payloads, padding, headers and fragmented objects. Findings report packets that run past their size, padding lengths that are wrong, and counts that disagree with the File Properties Object.

**Specified in** sections 5.2 and 7.3 of the ASF specification.
