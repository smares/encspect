---
id: writer-detection
title: Writer detection
related: encoder-detection, iso-base-media-format, asf-container, itunes-metadata, vorbis-comments, ape-tag, id3v2, dtshd-file
references: ID3v2.3 and ID3v2.4 frames sections on TSSE; Vorbis I specification section 5.2.2; ISO/IEC 14496-12; the project's Apple, Windows and ffmpeg fixtures
---

**What it is.** The writer is the software that put the coded audio into the file: a muxer, a container writer or a tagger. It need not be the [encoder](encoder-detection). A file remuxed by ffmpeg or copied by Apple's tools keeps its audio's encoder and shows the new writer, so the two are resolved apart, each with a confidence and its own evidence. They never contradict each other; only two pieces of evidence for the same role can.

**The evidence:**

- A container layout the writer leaves in the file (medium): the Windows Media Format SDK's writer adds a Codec List and a Compatibility Object and a WMFSDKVersion attribute, where Media Foundation's ASF sink has a MediaFoundationVersion attribute and neither object ([ASF](asf-container)); Apple's file writer sets an [MP4](iso-base-media-format) stream type byte of 0x14 and pads the audio data to a 4,096-byte boundary, and writes ADTS headers with the original/copy bit set and a buffer fullness of 0 (low).
- A name the file states (low): whatever software wrote the tag fills it in, and a tag survives a rewrite.
- A muxer's version text such as ffmpeg's `Lavf60.16.100`, in whichever tag holds it, even one that otherwise names the encoder.

**Where each tag goes.** The specification of the field decides; where it leaves the field open, the claim is the writer's.

- ID3v2 TSSE: encoder, since the frame is defined as the used audio encoder and its settings (ID3v2.4 frames section 4.2.5).
- Vorbis comment vendor string: encoder. ENCODER field: writer, since section 5.2.2 lists no such field.
- APEv2 Encoder item: writer, since the item key list has none.
- MP4 ©too and QuickTime ©swr: writer; no specification at hand defines them as the encoder.
- ASF attributes (WMFSDKVersion, MediaFoundationVersion, WM/ToolName, WM/EncodingSettings): writer.
- DTS-HD BUILDVER: writer, since it may name the authoring tool.

**Conflicts.** Two claims of the writer role that name different software are a finding, since one was probably kept when other software rewrote the file. A writer that differs from the encoder is not.

**What you see.** A Writer node with its Confidence and each piece of Evidence, at its bytes.
