---
id: dtshd-file
title: The DTS-HD file (.dtshd)
related: dts-extension-substream, dts-lossless-extension, crc, encoder-detection
references: ATSC A/103:2014 Annex E; FFmpeg 6.1.1 libavformat/dtshddec.c; MediaInfoLib v24.01 File_Dts.cpp
---

**The problem.** A raw DTS-HD stream has no place for the facts an authoring tool needs: where the stream starts on the time line, how long it is, how many frames to skip at the start, what the encoder was.

**How it works.** A .dtshd file is a run of chunks. Each is an 8-character ID, a 64-bit big endian size and a payload; the size covers the payload, reserved bytes and one to three zero bytes that bring the next chunk to a 32-bit boundary. DTSHDHDR comes first. Readers skip the chunks they do not know:

- DTSHDHDR, the version, a time code and flags for the stream (variable bit rate, core, extension substreams);
- FILEINFO and AUPRINFO, text;
- CORESSMD, EXTSS_MD and AUPR-HDR, bit rates, sampling frequency, length, channel masks and the codec delay;
- NAVI-TBL, BITSHVTB, BRANCHPT, tables of offsets and frame indexes;
- STRMDATA, the stream itself, a DTS stream as in a .dts file;
- TIMECODE, BLACKOUT and BUILDVER, which may follow the stream.

**What you see.** Each chunk with its fields, the stream inside STRMDATA analyzed as DTS, and checks of the metadata against it (sampling frequency and channels). BUILDVER is a string the standard calls the encoder's version identification and describes as compiler information; the analyzer reports it as a low confidence claim about the encoder, since the standard does not say whether it names the encoder or the tool that wrote the file.
