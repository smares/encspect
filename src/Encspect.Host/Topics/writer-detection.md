---
id: writer-detection
title: Writer detection
related: encoder-detection, matroska-container, iso-base-media-format, asf-container, itunes-metadata, vorbis-comments, ape-tag, id3v2, dtshd-file
references: ID3v2.3 section 4.2.1 and ID3v2.4 frames section 4.2.5 (TSSE); Vorbis I specification section 5.2.2; RFC 7845 section 5.2; ATSC A/103:2014 Table E.1; Apple QuickTime File Format user data atoms; FFmpeg n6.1.1 libavformat; the project's fixtures
---
**What it is.** The writer is the software that put the coded audio into the file: a muxer, a container writer or a tagger. It need not be the [encoder](encoder-detection): a file remuxed by ffmpeg keeps its audio's encoder and shows the new writer. Each has its own confidence and evidence.

**The evidence:**

- A name the file states (low).
- A muxer's version such as ffmpeg's `Lavf60.16.100`, or the vendor string "ffmpeg" of a bit-exact Ogg file; ffmpeg's own layout of an MP3's Xing and LAME tag names FFmpeg (low).
- A container layout that Encspect's test files show one writer producing:
  - [ASF](asf-container), medium: files of the Windows Media Format SDK (wmvcore 12.0.26100) carry a Codec List Object and a Compatibility Object, those of Media Foundation (2.112) do not.
  - [MP4](iso-base-media-format) and ADTS, low: AAC files afconvert wrote on macOS 15.7 have stream type byte 0x14 and a free box that pads the audio data, or the original/copy bit set and a buffer fullness of 0.

**Where each tag goes.** The specification decides; where it leaves the field open, the claim is the writer's: Vorbis ENCODER, QuickTime ©swr, MP4 ©too, ASF WM/ToolName, WMFSDKVersion and MediaFoundationVersion, the APEv2 Encoder item, Matroska WritingApp and MuxingApp. ID3v2 TSSE, DTS-HD BUILDVER and the Vorbis vendor string name the encoder. Elsewhere a `Lavc…` ID names the encoder and a `Lavf…` version the writer; in ASF a `Lavf…` stamp outranks a copied WMFSDKVersion or MediaFoundationVersion, which stays as a differing claim.

**Versions and conflicts.** The encoder may take the version from a writer claim that is its own name plus a version. Two writer claims that name different software are a finding, and so is one that names software known to write another format (LAME on a WavPack stream); a differing encoder, unknown software, and FFmpeg or GPAC, which write many formats, raise none.

**What you see.** A Writer node with its Confidence and Evidence, at the bytes that hold it; for WMA it covers the stream and the evidence text gives the offset.
