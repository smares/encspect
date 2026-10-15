---
id: writer-detection
title: Writer detection
related: encoder-detection, iso-base-media-format, asf-container, itunes-metadata, vorbis-comments, ape-tag, id3v2, dtshd-file
references: ID3v2.3 section 4.2.1 and ID3v2.4 frames section 4.2.5 (TSSE); Vorbis I specification section 5.2.2; RFC 7845 section 5.2; ATSC A/103:2014 Table E.1; Apple QuickTime File Format user data atoms; FFmpeg n6.1.1 libavformat; the project's fixtures
---
**What it is.** The writer is the software that put the coded audio into the file: a muxer, a container writer or a tagger. It need not be the [encoder](encoder-detection): a file remuxed by ffmpeg keeps its audio's encoder and shows the new writer. The two are resolved apart, each with its own confidence and evidence, and never contradict each other.

**The evidence:**

- A container layout (medium): the Windows Media Format SDK's writer adds a Codec List, a Compatibility Object and WMFSDKVersion, where Media Foundation's ASF sink has MediaFoundationVersion and neither ([ASF](asf-container)). Apple's file writer sets an [MP4](iso-base-media-format) stream type byte of 0x14 and pads the audio data to 4,096 bytes, and writes ADTS headers with the original/copy bit set (low).
- A name the file states (low): the software that wrote the tag fills it in, and a tag survives a rewrite.
- A muxer's version such as ffmpeg's `Lavf60.16.100`, in any tag. (Its `Lavc…` encoder ID names the encoder.)

**Where each tag goes.** The field's specification decides; where it leaves the field open, the claim is the writer's.

- ID3v2 TSSE: encoder (ID3v2.3 section 4.2.1, ID3v2.4 frames section 4.2.5). DTS-HD BUILDVER: encoder (A/103 Table E.1, "DTS-HD Encoder Version Identification").
- Vorbis comment vendor string: encoder. ENCODER field: writer; Vorbis I section 5.2.2 lists none, and RFC 7845 section 5.2 has applications "identify themselves" with it.
- QuickTime ©swr and ASF WM/ToolName: writer. MP4 ©too has no Apple specification: writer.
- APEv2 Encoder item and ASF WMFSDKVersion: unverified, so writer.

**Versions.** The encoder may take the version of a writer claim that starts with the encoder's own name and goes on, and the evidence says so. Another name, as in a remux by MP4Box or ffmpeg, lends nothing.

**Conflicts.** Two writer claims that name different software are a finding. So is a claim that names software known to write another format (LAME on a WavPack stream; a table of the tools the fixtures show). A writer that differs from the encoder, unknown software, and FFmpeg or GPAC, which write many formats, raise none.

**What you see.** A Writer node with its Confidence and each piece of Evidence, at its bytes.
