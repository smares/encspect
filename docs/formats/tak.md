# TAK

TAK (Tom's lossless Audio Kompressor, `.tak`) at header level: the metadata objects with their CRC-24s, the stream info, the encoder's version and preset, the stored WAV header and footer, and the end of the audio the last frame info gives. The frames are not walked and the coded audio is not read in 0.2; the frame walk waits for 0.4, and a decoder needs a clean-room description of the coding, which only FFmpeg's LGPL decoder gives.

Sources:

- TAK 2.3.3 (2022-06-30) by Thomas Becker: `Sdk/Source/takStream.pas`, the author's own source of the container ("Do with it, whatever you want", asking that the module is not distributed without the SDK and that a modified version says it is not an official release), the SDK's documentation (read, not copied) and the applications' readme. The author's page is http://www.thbeck.de/Tak/Tak.html. The parser follows takStream.pas where the files agree with it, carries its notice in `src/Encspect.Formats.Tak/Layout.cs` and in [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md), and **is not an official release of TAK**.
- Takc.exe 2.3.3 and the SDK's x64 decoder library as black-box writers and readers of the files in Encspect's tests (run in CI on Windows only, `.github/workflows/tak-fixtures.yml`), and two real files of the FFmpeg sample archive (`lostchord-tak_1_0.tak`, TAK 1.0, and `lostchord-tak_2_2_0.tak`, 2.2.0), walked by `check_tak_samples.py`.
- MediaInfoLib's `File_Tak.cpp` (BSD) as a second reader in the cross-check. Nothing is read from FFmpeg, libav, mutagen or getID3: FFmpeg's TAK decoder is LGPL.

## What is read

- The stream ID `tBaK`, then the metadata objects, each a 4-byte header (a 7-bit ID, a reserved bit, a 24-bit size), the data and a CRC-24 (RFC 2440's: initial value 0xB704CE, polynomial 0x864CFB, over the data, stored little-endian). The End object (ID 0) has neither data nor a CRC-24.
- Stream info (ID 1, must be first): codec (0 "Integer 24 bit (TAK 1.0)", 1 "Experimental!", 2 "Integer 24 bit (TAK 2.0)", 3 "LossyWav (TAK 2.1 Beta)", 4 "Integer 24 bit MC (TAK 2.2)", the names of Takc's readme), profile, frame size type with the frame length it gives, the sample count of each channel, data type, sample rate, bits per sample, channels and, where the extension flag is set (2.2.0 and later), the valid bits per sample and the speaker assignment. Before 2.2 the channel count had 5 bits and no flag; the top bit of the old field reads as the flag, 0, so one reader serves both.
- Seek table (ID 2, before 1.1.1): its size and CRC-24; its points are not read.
- Wave data (ID 3): the header and footer sizes, the stored WAV header ("WAV Header Data") and terminating data ("WAV Terminating Data"), named as Monkey's Audio names them, with the header's RIFF chunks listed, the format tag, channels and channel mask read by the shared `WavHeader`, and lossyWAV's `fact` chunk read with `FactText`.
- Encoder info (ID 4): the version (three digits, the major in the top byte), the preset and the evaluation level (Standard, Extra, Max).
- Padding (ID 5), MD5 (ID 6, shown, not compared) and last frame info (ID 7: the last frame's distance from the first frame, which follows the End object, and its size).
- The audio data as one node from the end of the metadata to the end of the stream.

## Where the files depart from takStream.pas

The files win. Each departure holds in every file Takc wrote, and a test asserts it over all of them; both real files show the first three, and only the pipe-encoded files have a padding object.

- **An object's size counts its CRC-24.** takStream.pas says the size covers the data without the CRC. The stream info of `lostchord-tak_2_2_0.tak` states 13 bytes: 10 of data and the 3-byte CRC-24.
- **Wave data has one CRC-24, at the end,** over the two 3-byte sizes and the header and footer bytes (53 = 6 + 44 + 3). takStream.pas puts a CRC-24 right after the sizes. MediaInfo's reader reads the same order.
- **The End object** is a header with size 0 and no CRC-24.
- **The padding object (ID 5) has no CRC-24:** its size counts only its bytes, which are zero. Takc reserves 46 bytes of wave data when it encodes from a pipe, and a 44-byte header leaves a padding object of 2 bytes (run 42719547527, `-wm1024`: 980 bytes).

## The walk

- Each object is read by its size. A frame's sync word 0xA0FF, stored as the bytes `FF A0`, would read as an object of ID 127 with the reserved bit set, so a frame is tested first: the metadata ends at the first frame whose header CRC-24 holds, and a missing End object is `tak.end-missing`. The walk never stops on a sync word alone.
- An object of ID 127 with the reserved bit set whose bytes are no frame header is read as an unknown object. An unknown ID (8 to 127) is skipped by its size (`tak.object-unknown`, Info); a reserved bit that is set is `tak.object-reserved-bit` (Info).
- The stream info must come first (`tak.stream-info-not-first`; none at all is `tak.stream-info-missing`). The report keeps at most 4096 objects (`tak.objects-limit`).
- Data that ends inside the metadata, or inside an object whose size runs past the data, is `tak.metadata-cut`.

## Detection

- `tBaK` at the start of the data (after leading tags) and a stream info object whose CRC-24 holds is certain (class 100); the signature with an object header of ID 1 and a plausible size but a failing CRC-24 is likely (80); the signature alone, or followed by something else, is possible (70). Nothing is matched further into the data.
- The frame header's CRC-24 decides where the audio starts when the End object is missing; it is not part of the detection.

## Declared end

- The end of the last frame by the last frame info object (TAK 1.1.1 and later), used only when its CRC-24 holds; the APEv2 tag after it is the host's. Without the object, or with a failing CRC-24, there is no declared end, and trailing tags are looked for as in a file with no declared size.
- A declared end past the data is a file cut in the audio (`tak.audio-cut`, with the bytes missing). Bytes after the declared end that are no tag are `tak.data-after-frames`.
- An ID3v2 tag before `tBaK` is the host's, with `tak.leading-tag` (Info): TAK's readme says its decoder throws an error on tags prepended to the file.

## Reported

- The sample rate, channels, bits per sample, samples, duration and average bitrate (the audio data's bytes over the duration).
- Statistics: Codec, Profile, Preset (as Takc prints it: "-p2", "-p3e", "-p4m"; without the encoder info object it reads "-p2 (evaluation unknown)"), Frame Length, Channel Layout, and "Verified Header Checksums" and "Failed Header Checksums" over the metadata objects (the unit "Header", as Musepack SV8 names the CRC of its stream header).
- Channel layout, in the words of TTA and Monkey's Audio: the speaker assignment of the stream info where it has one; else the nonzero channel mask of the stored WAV header; else one channel is center and two are left and right; more have no layout.
- Encoder "TAK 2.3.3" (high confidence) from the encoder info object. A stream of codec 0 with no encoder info object at all is "TAK 1.0" with medium confidence and the stream info as evidence: the SDK says codec 0 is TAK 1.0 and its decoder takes 1.0.0 for such a file, and the version is inferred from a missing object, not stated. An encoder info object whose CRC-24 fails still names its version, with medium confidence and the failing CRC-24 in the evidence; one whose size breaks the layout, or a stream of another codec without the object, names no encoder. Processor lossyWAV from a `fact` chunk in the wave data, high confidence, as for the other formats that keep a WAV header. Codec 3 is named and makes no claim of its own: nothing checkable says whether the beta picked it itself or because the user asked.
- Values takStream.pas does not define are Info findings (`tak.value-undefined`): a codec not listed, a data type other than 0, a bits per sample code above 16, a sample rate above 262,144 Hz, valid bits over the bits per sample, an evaluation of 3, a speaker code of 19 to 63. A reserved frame size type (10 to 15) is `tak.frame-size-type-reserved`, and a frame size that does not fit the rate (a time type of more than 16,384 samples, a size type of more than 250 ms) is `tak.frame-size-invalid`.
- The stored WAV header: `tak.wav-header-damaged`, `tak.wav-channels-differ`, `tak.channel-mask-mismatch`, `tak.header-fact-text-unrecognized`; the writer claims of tags: `tak.writer-claim-differs`. Data that does not begin with the stream ID, when the parser is forced: `tak.no-signature`.
- A CRC-24 that fails is `tak.object-crc-mismatch` (once, with a count); an object whose size breaks its layout is `tak.object-size-invalid`.

## Verification

Four checksums are declared (the metadata CRC-24 is the one the statistic "Verified Header Checksums" counts); no decoder exists in 0.2, so the host reports that no decoder handles the coding.

| Checksum | Covers | Compared |
|---|---|---|
| metadata CRC-24 | a metadata object's data | yes, every object that has one |
| frame header CRC-24 | a frame's header | no: the frames are not walked |
| frame data CRC-24 | a frame's coded data | no: the frames are not walked |
| MD5 | the raw audio | never: it needs a decoder |

A TAK stream is therefore never "verified" in 0.2: its frame checksums are declared and not compared.

## Observations from Encspect's test files

All from Takc 2.3.3 (`tests/fixtures/tak`, [development.md](../development.md)); every file was encoded twice and the two files are equal byte for byte.

- **Objects:** the stream info, last frame info, encoder info, MD5 (with `-md5`), wave data, padding (pipe encoding) and End, in that order; no seek table. `-wm0` and `-ihs` leave out the wave data.
- **Frame size:** 4096 samples at -p0 and -p1, 125 ms at -p2, 8192 samples at -p3 and 250 ms at -p4; `-fsl` below the preset's size lowers it. Takc refuses `-fsl16384` at -p2. The article [tak-presets](../../src/Encspect.Host/Topics/tak-presets.md) lists them.
- **Channels:** six channels store codec 4, with the speaker assignment where the WAV header has a channel mask.

## Not covered

- Decoding, so no MD5 comparison, measurement, entropy figures or coding graphs. A decoder needs a clean-room split (one reader of FFmpeg's LGPL decoder writes a description, another implements from it).
- The frames until 0.4 (the plan's "small separate items"): their count, their CRC-24s and the bitrate over time.
- The seek table's points, and a channel count over 16 (the 4-bit field cannot hold it).
- The meaning of the codecs 1 and 3 beyond their names.
