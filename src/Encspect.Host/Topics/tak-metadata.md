---
id: tak-metadata
title: TAK metadata objects
related: crc, verification, riff-chunks, lossywav, tak-presets
references: TAK 2.3.3 SDK, Sdk/Source/takStream.pas (the author's source of the container) and the SDK's documentation; Takc.exe 2.3.3 as a black-box writer of the files in Encspect's tests, and two TAK files of the FFmpeg sample archive (TAK 1.0 and 2.2.0)
---

**The problem.** A lossless file should keep the header and footer of the audio file it came from, let a reader check them without decoding, and tell it where the audio starts.

**How it works.** After the stream ID "tBaK" the file is a run of metadata objects, then the audio frames. An object has a 4-byte header (a 7-bit ID, a reserved bit and a 24-bit size), its data and a CRC-24 (RFC 2440's, the OpenPGP one) over the data. The IDs are 0 End, 1 stream info, 2 seek table, 3 wave data, 4 encoder info, 5 padding, 6 MD5 and 7 last frame info. The stream info must come first: it holds the codec, the profile, the frame size, the sample count and the audio format. The wave data keeps the WAV file's header and footer. The last frame info gives the last frame's distance from the first frame and its size, so a reader finds the end of the audio. The End object ends the metadata.

**What the files show.** In Encspect's test files (Takc 2.3.3, and the two real files) the object's size counts its CRC-24, though takStream.pas says it does not; the wave data has one CRC-24, at its end, over the two sizes and the bytes; the End object is a header with size 0; and the padding that pipe encoding reserves has no CRC-24 and holds zero bytes. Takc 2.3.3 writes the stream info, the last frame info, the encoder info, the MD5 when asked, the wave data, the padding and the End object, in that order, and no seek table; the TAK 1.0 file has one. Every CRC-24 holds in all of them, and the last frame info ends the audio where the file ends.

**What you see.** The objects with their sizes and CRC-24s as stored and as computed, and the fields of each. The first frame starts with the sync bytes FF A0 and a header of its own with a CRC-24, which the analyzer uses to find the audio when the End object is missing. The frames' CRC-24s are not compared in 0.2 ([verification](verification)).
