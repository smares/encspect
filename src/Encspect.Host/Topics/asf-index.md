---
id: asf-index
title: ASF index objects
related: asf-container, asf-packets
references: Advanced Systems Format (ASF) Specification, revision 01.20.03, sections 4.9 to 4.11, 6 and 8.2.12
---

**The problem.** To jump to a time in a file, a player must find the data packet that holds it. With packets in order of send time, but streams of varying bitrate, the byte offset of a time cannot be worked out.

**How it works.** The index objects follow the Data Object. The Simple Index Object indexes one video stream at a fixed time interval, most often one second: each entry names the packet that holds the closest key frame at or before that time, and how many packets to send. The Index Object indexes any stream, at an interval in milliseconds. It is cut into blocks; each block has a 64-bit base position for every indexed stream, and its entries add 32-bit offsets from the first data packet. An offset of 0xFFFFFFFF marks a time that cannot be indexed. The Index Parameters Object in the header says which streams are indexed and by what kind of index: nearest past data packet, media object or cleanpoint. The Media Object Index Object and Timecode Index Object index by frame number and by timecode.

**What you see.** The index objects with their entries, and findings where an offset does not point to the start of a packet or lies past the data, where the specifiers differ from the Index Parameters Object, or where counts and sizes disagree. An audio file may have no index at all, since seeking in audio can sometimes be estimated from the bitrate (section 4.1).
