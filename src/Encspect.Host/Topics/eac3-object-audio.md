---
id: eac3-object-audio
title: Object audio in E-AC-3
related: eac3-substreams, channel-layouts, ac3-frames-and-bsi
references: ETSI TS 103 420 V1.2.1 5, 6, 8, C.3; ETSI TS 102 366 V1.4.1 H.2, F.6
---

**The problem.** Channel-based audio fixes every sound to a speaker before it is delivered. Object-based audio sends sounds with positions that a player maps onto whatever speakers it has. Existing E-AC-3 decoders and links, however, only know channels.

**How it works.** The stream stays an ordinary E-AC-3 stream: a channel-based downmix, such as 5.1 or 7.1, that any decoder plays. Next to it, two payloads ride in an EMDF container, which sits in a frame's auxiliary data or a skip field, where older decoders skip it. Payload 11 is the object audio metadata (OAMD): how many objects there are and where each one is and how large, with timing. Payload 14 is joint object coding (JOC): per-band parameters that let a decoder rebuild the objects from the downmix. A container starts with the sync word 0x5838 and a length, lists payloads by ID, and ends with protection bits. A flag in the frame's additional bit stream information and in the MP4 dec3 box, flag_ec3_extension_type_a, also announces the extension and gives a complexity index, the number of objects the metadata names, at most 16.

**What you see in a file.** The report shows the flag and complexity index, each EMDF container with its payloads, and from the JOC header the downmix configuration and the number of objects. Dolby Atmos in Dolby Digital Plus is this mechanism. Only the JOC header and the OAMD object count are read; the objects themselves are not decoded.
