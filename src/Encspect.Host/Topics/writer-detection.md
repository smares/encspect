---
id: writer-detection
title: Writer detection
related: encoder-detection, iso-base-media-format, asf-container, itunes-metadata, vorbis-comments, ape-tag, id3v2, dtshd-file
references: ID3v2.3 and ID3v2.4 frames sections on TSSE; Vorbis I specification section 5.2.2; ISO/IEC 14496-12; the project's Apple, Windows and ffmpeg fixtures
---

**What it is.** The writer is the software that put the coded audio into the file: a muxer, a container writer or a tagger. It need not be the [encoder](encoder-detection): a file remuxed by ffmpeg keeps its audio's encoder and shows the new writer. The two are resolved apart, each with a confidence and its own evidence, and never contradict each other; only two pieces of evidence for one role can.

**The evidence:**

- A container layout the writer leaves (medium): the Windows Media Format SDK's writer adds a Codec List, a Compatibility Object and WMFSDKVersion, where Media Foundation's ASF sink has MediaFoundationVersion and neither object ([ASF](asf-container)); Apple's file writer sets an [MP4](iso-base-media-format) stream type byte of 0x14 and pads the audio data to 4,096 bytes, and writes ADTS headers with the original/copy bit set (low).
- A name the file states (low): the software that wrote the tag fills it in, and a tag survives a rewrite.
- A muxer's version text such as ffmpeg's `Lavf60.16.100`, in whichever tag holds it.

**Where each tag goes.** The specification of the field decides; where it leaves the field open, the claim is the writer's.

- ID3v2 TSSE: encoder, since the frame is defined as the used audio encoder and its settings (ID3v2.4 frames section 4.2.5).
- Vorbis comment vendor string: encoder. ENCODER field: writer, since section 5.2.2 lists no such field.
- APEv2 Encoder item: writer, since the item key list has none.
- MP4 ©too and QuickTime ©swr: writer; no specification at hand defines them as the encoder.
- ASF attributes (WMFSDKVersion, WM/ToolName and the like) and DTS-HD BUILDVER: writer.

**Versions.** The encoder may take the version of a writer claim whose name starts with the encoder's own and goes on, and the evidence says where it came from. A writer of another name (a remux by MP4Box or ffmpeg) lends nothing.

**Conflicts.** Two claims of the writer role that name different software are a finding, since one was probably kept when other software rewrote the file. A writer that differs from the encoder is not.

**What you see.** A Writer node with its Confidence and each piece of Evidence, at its bytes.
