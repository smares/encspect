# TAK

TAK (Tom's lossless Audio Kompressor, `.tak`): the metadata objects with their CRC-24s, the stream info, the encoder's version and preset, the stored WAV header and footer, the end of the audio the last frame info gives, and the frames: each frame's header, its data and the two CRC-24s, compared. The coded samples are read by the decoder ([Decoding](#decoding)), which follows a clean-room description of the coding (the only detailed source of which is FFmpeg's LGPL decoder), for the MD5, the measurements and the front ends' output.

Sources:

- TAK 2.3.3 (2022-06-30) by Thomas Becker: `Sdk/Source/takStream.pas`, the author's own source of the container ("Do with it, whatever you want", asking that the module is not distributed without the SDK and that a modified version says it is not an official release), the SDK's documentation (read, not copied) and the applications' readme. The author's page is http://www.thbeck.de/Tak/Tak.html. The parser follows takStream.pas where the files agree with it, carries its notice in `src/Encspect.Formats.Tak/Layout.cs` and in [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md), and **is not an official release of TAK**.
- Takc.exe 2.3.3 and the SDK's x64 decoder library as black-box writers and readers of the files in Encspect's tests (run in CI on Windows only, `.github/workflows/tak-fixtures.yml`), and two real files of the FFmpeg sample archive (`lostchord-tak_1_0.tak`, TAK 1.0, and `lostchord-tak_2_2_0.tak`, 2.2.0), walked by `check_tak_samples.py`.
- MediaInfoLib's `File_Tak.cpp` (BSD) as a second reader in the cross-check. Nothing is read from FFmpeg, libav, mutagen or getID3 by the parser or the decoder: FFmpeg's TAK decoder is LGPL, so the decoder follows [a description of the coded samples](../design/tak-format.md) that one agent wrote from it and another implemented from without reading it ([development.md](../development.md#tak-decoding-clean-room)); FFmpeg runs as a black box in the cross-check.

## What is read

- The stream ID `tBaK`, then the metadata objects, each a 4-byte header (a 7-bit ID, a reserved bit, a 24-bit size), the data and a CRC-24 (RFC 2440's: initial value 0xB704CE, polynomial 0x864CFB, over the data, stored little-endian). The End object (ID 0) has neither data nor a CRC-24.
- Stream info (ID 1, must be first): codec (0 "Integer 24 bit (TAK 1.0)", 1 "Experimental!", 2 "Integer 24 bit (TAK 2.0)", 3 "LossyWav (TAK 2.1 Beta)", 4 "Integer 24 bit MC (TAK 2.2)", the names of Takc's readme), profile, frame size type with the frame length it gives, the sample count of each channel, data type, sample rate, bits per sample, channels and, where the extension flag is set (2.2.0 and later), the valid bits per sample and the speaker assignment. Before 2.2 the channel count had 5 bits and no flag; the top bit of the old field reads as the flag, 0, so one reader serves both.
- Seek table (ID 2, before 1.1.1): its size and CRC-24; its points are not read.
- Wave data (ID 3): the header and footer sizes, the stored WAV header ("WAV Header Data") and terminating data ("WAV Terminating Data"), named as Monkey's Audio names them, with the header's RIFF chunks listed, the format tag, channels and channel mask read by the shared `WavHeader`, and lossyWAV's `fact` chunk read with `FactText`.
- Encoder info (ID 4): the version (three digits, the major in the top byte), the preset and the evaluation level (Standard, Extra, Max).
- Padding (ID 5), MD5 (ID 6, shown, not compared) and last frame info (ID 7: the last frame's distance from the first frame, which follows the End object, and its size).
- The frames, from the end of the metadata to the end of the stream (takStream.pas, "Audio frames", "Frame-Info", "Frame-Header"). Each is a header, the coded data and a CRC-24 over the coded data:
  - the header: the sync word 0xA0FF (the bytes `FF A0`), the flags for the last frame, an info frame and metadata (not supported: a header that sets it is no frame header), the 21-bit frame number, for the last frame its sample count less 1 (14 bits) and 2 padding bits (read and ignored, as TAK's decoder does; the CRC-24 covers them), for an info frame the stream info (as in the metadata) with the previous-distance flag, 5 reserved bits and the 25-bit distance, padded to a byte, and the CRC-24 over all of it, the sync word included;
  - the data CRC-24, the 3 bytes that end the frame, over the coded data between the header's CRC-24 and itself;
  - an info frame comes every 2 seconds at least (takStream.pas), and the last frame states its samples.

## Where the files depart from takStream.pas

The files win. Each departure holds in every file Takc wrote, and a test asserts it over all of them; both real files show the first three, and only the pipe-encoded files have a padding object.

- **An object's size counts its CRC-24.** takStream.pas says the size covers the data without the CRC. The stream info of `lostchord-tak_2_2_0.tak` states 13 bytes: 10 of data and the 3-byte CRC-24.
- **Wave data has one CRC-24, at the end,** over the two 3-byte sizes and the header and footer bytes (53 = 6 + 44 + 3). takStream.pas puts a CRC-24 right after the sizes. MediaInfo's reader reads the same order.
- **The End object** is a header with size 0 and no CRC-24.
- **The padding object (ID 5) has no CRC-24:** its size counts only its bytes, which are zero. Takc reserves 46 bytes of wave data when it encodes from a pipe, and a 44-byte header leaves a padding object of 2 bytes (run 42719547527, `-wm1024`: 980 bytes).
- **The frames:** no departure. takStream.pas's frame layout holds in all 33 Takc files and both real files: the header CRC-24 covers the header from the sync word, the data CRC-24 covers only the coded data and the frame ends where the next begins.

## The metadata walk

- Each object is read by its size. A frame's sync word 0xA0FF, stored as the bytes `FF A0`, would read as an object of ID 127 with the reserved bit set, so a frame is tested first: the metadata ends at the first frame whose header CRC-24 holds, and a missing End object is `tak.end-missing`. The walk never stops on a sync word alone.
- An object of ID 127 with the reserved bit set whose bytes are no frame header is read as an unknown object. An unknown ID (8 to 127) is skipped by its size (`tak.object-unknown`, Info); a reserved bit that is set is `tak.object-reserved-bit` (Info).
- The stream info must come first (`tak.stream-info-not-first`; none at all is `tak.stream-info-missing`). The report keeps at most 4096 objects (`tak.objects-limit`).
- Data that ends inside the metadata, or inside an object whose size runs past the data, is `tak.metadata-cut`.

## The frame walk

- A frame states no length: it ends where the next one begins. The walk finds each frame by its sync word and reads its header with the CRC-24 (`FrameHeader`).
  - The first frame is where the metadata ends. A header there that fails its CRC-24 starts a skipped stretch (below).
  - The end of a frame is the next candidate in the data that has a sync word and one of: a header whose CRC-24 holds; a header that fails its CRC-24 but states the number that follows this frame's; or a header that fails with the 3 bytes before it holding the CRC-24 of this frame's data. Each is evidence of 24 bits or more, so a sync word that occurs inside coded data (it does, about once in 65,536 bytes) is not taken for a frame unless its header holds.
  - A header whose CRC-24 fails states a number that cannot be trusted: the sequence goes on from the one expected.
  - A frame ends at the end of the data (the declared end where the last frame info states one) when no candidate follows. A candidate more than 4 MiB away is out of reach: the header stands alone and the bytes after it are skipped.
- Bytes before a frame whose header holds, where no frame was found, are **skipped data** (`tak.data-skipped`, a "Skipped Data" node): the walk looks for the next header whose CRC-24 holds. A damaged sync word merges its frame into the one before, which then fails its data CRC-24.
- The frames are read from the file in windows; the records keep about 64 bytes for each of the first 1,048,576 frames and the nodes are built in blocks of 1,024 when read, so the memory stays bounded whatever the length. Frames past the cap are counted and compared and have no nodes (`tak.frames-limit`). The frame number has 21 bits, so a stream holds at most 2,097,152 frames.
- The walk is linear: it reads each byte a few times, never more.

## Detection

- `tBaK` at the start of the data (after leading tags) and a stream info object whose CRC-24 holds is certain (class 100); the signature with an object header of ID 1 and a plausible size but a failing CRC-24 is likely (80); the signature alone, or followed by something else, is possible (70). Nothing is matched further into the data.
- The frame header's CRC-24 decides where the audio starts when the End object is missing; it is not part of the detection.

## Declared end

- The end of the last frame by the last frame info object (TAK 1.1.1 and later), used only when its CRC-24 holds; the APEv2 tag after it is the host's. Without the object, or with a failing CRC-24, there is no declared end, and trailing tags are looked for as in a file with no declared size.
- A declared end past the data is a file cut in the audio (`tak.audio-cut`, with the bytes missing); the last frame is then `tak.frame-cut` where its data CRC-24 fails (below). Bytes after the declared end that are no tag are `tak.data-after-frames`.
- The frames end at the declared end. Without a declared end they end at the end of the data, and a file cut inside its last frame can only fail that frame's data CRC-24.
- An ID3v2 tag before `tBaK` is the host's, with `tak.leading-tag` (Info): TAK's readme says its decoder throws an error on tags prepended to the file.

## Reported

- The sample rate, channels, bits per sample, samples, duration and average bitrate (the bits of the frames over the time their samples last, as TTA and FLAC work it out).
- Statistics: Codec, Profile, Preset (as Takc prints it: "-p2", "-p3e", "-p4m"; without the encoder info object it reads "-p2 (evaluation unknown)"), Frame Length, **Frames** (the frames the data holds whole, a cut one not counted), Channel Layout, and the figures of the frames under the names of TTA and FLAC: Average Bitrate, Bitrate Mode, Smallest Frame and Largest Frame (in bytes), Minimum Bitrate, Maximum Bitrate and the Bitrate Distribution in ranges of 32 kbps; "Lost Samples" where a frame is cut.
- **Checksums:** "Verified Header Checksums" and "Failed Header Checksums" count the headers whose CRC-24 holds: the metadata objects (the unit "Header", as Musepack SV8 names the CRC of its stream header) and the frames' headers (as FLAC counts its frame headers); "Verified Frame Checksums" and "Failed Frame Checksums" count the frames whose data CRC-24 holds.
- **Compression Ratio**, a statistics node ("Lowest", "Overall" and "Highest", in percent) and the series "Compression Ratio" and "Compression Ratio Over Time" (percent, concept `bitDistribution`, sharing the x of "Bitrate"): the coded size over the size of the audio as PCM, the bitrate over the stream's sample rate times channels times bits per sample. The bitrate series carry the concept `bitDistribution` and the quantity "Bitrate".
- **Graphs:** "Bitrate" (kbps over the frame offset: each frame's bits over the time its samples last) and "Bitrate Over Time" (kbps over seconds), the series TTA, FLAC and WavPack have, with the same names, units and axes. A frame's samples are the frame length of the stream info, or those the last frame states.
- Channel layout, in the words of TTA and Monkey's Audio: the speaker assignment of the stream info where it has one; else the nonzero channel mask of the stored WAV header; else one channel is center and two are left and right; more have no layout.
- Encoder "TAK 2.3.3" (high confidence) from the encoder info object. A stream of codec 0 with no encoder info object at all is "TAK 1.0" with medium confidence and the stream info as evidence: the SDK says codec 0 is TAK 1.0 and its decoder takes 1.0.0 for such a file, and the version is inferred from a missing object, not stated. An encoder info object whose CRC-24 fails still names its version, with medium confidence and the failing CRC-24 in the evidence; one whose size breaks the layout, or a stream of another codec without the object, names no encoder. Processor lossyWAV from a `fact` chunk in the wave data, high confidence, as for the other formats that keep a WAV header. Codec 3 is named and makes no claim of its own: nothing checkable says whether the beta picked it itself or because the user asked.
- Values takStream.pas does not define are Info findings (`tak.value-undefined`): a codec not listed, a data type other than 0, a bits per sample code above 16, a sample rate above 262,144 Hz, valid bits over the bits per sample, an evaluation of 3, a speaker code of 19 to 63. A reserved frame size type (10 to 15) is `tak.frame-size-type-reserved`, and a frame size that does not fit the rate (a time type of more than 16,384 samples, a size type of more than 250 ms) is `tak.frame-size-invalid`.
- The stored WAV header: `tak.wav-header-damaged`, `tak.wav-channels-differ`, `tak.channel-mask-mismatch`, `tak.header-fact-text-unrecognized`; the writer claims of tags: `tak.writer-claim-differs`. Data that does not begin with the stream ID, when the parser is forced: `tak.no-signature`.
- A CRC-24 that fails is `tak.object-crc-mismatch` (once, with a count); an object whose size breaks its layout is `tak.object-size-invalid`.
- **Frames,** each once per stream with a count, pointing at the first case:
  - `tak.frame-header-crc-mismatch`: a header's CRC-24 differs;
  - `tak.frame-crc-mismatch`: a frame's data CRC-24 differs;
  - `tak.frame-cut` (Warning): the data ends inside the last frame, where its data CRC-24 fails and it is not flagged as the last frame or the last frame info states a later end; the CRC-24 is not compared and the frame's samples are lost samples;
  - `tak.frames-missing`: the data ends after a whole frame that is not flagged as the last;
  - `tak.data-skipped`: bytes that hold no frame whose header CRC-24 holds, skipped up to the next;
  - `tak.frame-number-discontinuity`: a frame whose number is not the one after the frame before it (or, for the first frame, 0), which also means the frames after a loss;
  - `tak.total-samples-mismatch`: the frames hold other samples than the stream info states, where nothing was lost;
  - `tak.no-frames`: no frame whose header CRC-24 holds follows the metadata (not raised where `tak.audio-cut` already says the file is cut);
  - `tak.frames-limit`: more than 1,048,576 frames.

## Verification

Four checksums are declared. The three CRC-24s are compared by the parser; the MD5 is compared by the decoder, so it is checked where the front end decodes: with `--verify` (the decoder declares a checksum over decoded audio, so the host decodes without being asked for samples), `--measure` or an output sink.

| Checksum | Covers | Compared |
|---|---|---|
| metadata CRC-24 | a metadata object's data | yes, every object that has one |
| frame header CRC-24 | a frame's header | yes, every frame whose header the data holds |
| frame data CRC-24 | a frame's coded data | yes, every frame the data holds whole |
| MD5 | the raw audio, as a WAVE file's data chunk holds it | by the decoder, when every sample of the stream was decoded, in order and without a gap (`tak.md5-mismatch`, Warning, on the MD5 field of the MD5 object) |

- A stream is verified when every CRC-24 holds, the stream info's CRC-24 holds, the frames are all there in order up to the one flagged as the last, none is cut, nothing was skipped and their samples are the count the stream info states: the frame data CRC-24 then covers all the audio. One that stores an MD5 is verified when the decoder has compared it too.
- Takc writes the MD5 object only with `-md5`; of the encoder-written fixtures `external-md5.tak` has one, and the decoded audio matches it.
- The frame CRC-24 covers the coded data, not the decoded audio: it says the bytes are as the encoder wrote them, not what they decode to.

## Decoding

- **The walk:** the parser reads the coded data of every frame in every analysis, for the statistics below, with the same code the decoder reads with (`FrameSyntax`); a frame whose syntax breaks is `tak.frame-syntax-invalid` (Warning, with a count) and is not decoded, and whole bytes left after the last field are the info `tak.frame-bytes-after-data`. Streams of codecs 0, 1 and 3, of other depths or more channels are not walked.
- **Coding statistics and graphs:** "Stereo Coding" (the stereo mode of each frame of two channels: left/right, left/side, side/right, mid/side, scaled prediction, filter prediction), "Subframe Types" (residuals only or predictor), "Average Predictor Order", "Highest Predictor Order", "Coefficient Precision", "Highest Residual Mode" (the highest mode, 0 to 50, of the partitions of the residual blocks: the mode picks the code every value of the partition is read with, mode 0 meaning all zeros; a numeric series of its own, since FLAC states no Rice parameter series and 51 categories would outrun the eight colors of an ordered series) and "Channels With Wasted Bits", with the category series and the three numeric series per frame, under the names, units and axes of FLAC's, TTA's and WavPack's. The numeric series also have "Highest Predictor Order Over Time", "Coefficient Precision Over Time" and "Highest Residual Mode Over Time": the highest value of the frames that start in each second, the seconds counted in the samples coded so far as "Bitrate Over Time" and FLAC's do.
- **When:** the decoder declares a checksum (the MD5), so the host decodes every TAK stream in an analysis that asks for decoding (`--verify`, `--measure`, a sink), whether or not the stream stores an MD5. Without decoding the verification says so (`notRequested`) and the tree is the same.
- **What it reads:** codec 2 (mono and stereo) and codec 4 (up to six channels), data type 0, 8, 16 and 24 bits per sample. Anything else is refused when the decoder opens (`decode.not-possible`): codecs 0, 1 and 3 (their coding is not known; TAK 1.0 files are not decoded), other bit depths, more than two channels in codec 2, more than six in codec 4, and a frame size the stream info states that is not valid for the sample rate. The channel layout is the one the report shows.
- **The frame:** after the header's CRC-24 and before the data CRC-24, per channel the wasted bits, the first sample, a difference order and up to eight subframes; a subframe is stored as residuals or predicted by a filter of 4 to 256 taps whose coefficients come from up to 256 parameters, with a warm-up or a continuation of the subframe before; two channels (or the entries of codec 4's channel table) are then decorrelated by one of add, subtract, mid/side, scaled prediction or a filter of 8 or 16 taps. Frames shorter than 16 samples are stored. [The description](../design/tak-format.md) has the fields and the arithmetic, which is 32-bit with wraparound.
- **Output:** signed integers at the stream's depth, interleaved by the sink in channel order, one block per frame; 8-bit streams decode to values around 0.
- **Every frame decodes on its own:** no state passes from one frame to the next, so a frame that is damaged, lost or skipped costs only itself.
- **Checked against:** a test-side encoder that inverts the description (round trips over every residual mode, prediction order, difference order, wasted-bits value, stereo mode, the paired entries of the channel table, the sample rates and 8, 16 and 24 bits), the worked examples of the description, the manifests of the 33 encoder-written files (the samples of the signal Takc was given), and the TAK decoder's own output and FFmpeg's on those files, which `scripts/fixtures/crosscheck_tak_pcm.py` runs in CI ([development.md](../development.md#tak-decoding-clean-room)).
- **Damage:**
  - the syntax the description lists as damage, which the walk reports as `tak.frame-syntax-invalid` (a wasted-bits count of the sample width or more, subframes that rise out of the frame or have no sample, an order above the subframe, a warm-up difference order of 3, a precision below 3, a partition count outside 2 to 128, a residual mode above 50 or below 0, an escape of more than 29 bits, a filter mode on fewer than 257 samples, a channel table that lists a channel twice, not at all or beyond the count, or pairs it with a partner it may not have), coded data that ends inside a field or fields that run past it): the frame is not decoded, nothing of it is written, and the next frame decodes on its own; a sample outside the range of the depth is the decoder's `decode.frame-invalid`;
  - a frame whose header or data CRC-24 fails is decoded all the same and written, but the measurements leave its positions out as withheld samples ("checksum failed") and the next frame follows a gap ([the design](../design/damaged-units.md)); the MD5 is still compared, and fails where the audio changed (`tak.md5-mismatch`), as FLAC's does;
  - a frame cut by the end of the data is not decoded.
- **Not decoded:** the frames past the first 1,048,576 (`tak.frames-limit`).
- **Memory:** one frame (at most 4 MiB, `FrameWalker`), up to 16,384 samples of each channel for the samples and a few kilobytes of scratch.
- **Work:** `DecodeContext.AddWork` is charged before the work (the reading of the coded data is the parser's): the fixed work of `DecodeWork.ForSample` plus 4 for each sample of each channel, and for each predicted subframe one unit a tap for each predicted sample and N * N units for the N coefficients, and the filter mode's taps for each sample. The decoder states 16,384 units an input byte (`IDecoder.WorkPerInputByte`), as ALAC does; a frame of silence charges about 64,000 to 76,000 for each of its few bytes, so a stream of such frames is stopped by the budget past the floor of 2^35 units ([the measurement](../design/decoders.md#as-built-the-tak-decoder)).
- **What the description left unsure** and how the decoder treats it: [development.md](../development.md#tak-decoding-clean-room).

## Observations from Encspect's test files

All from Takc 2.3.3 (`tests/fixtures/tak`, [development.md](../development.md)); every file was encoded twice and the two files are equal byte for byte.

- **Objects:** the stream info, last frame info, encoder info, MD5 (with `-md5`), wave data, padding (pipe encoding) and End, in that order; no seek table. `-wm0` and `-ihs` leave out the wave data.
- **Frame size:** 4096 samples at -p0 and -p1, 125 ms at -p2, 8192 samples at -p3 and 250 ms at -p4; `-fsl` below the preset's size lowers it. Takc refuses `-fsl16384` at -p2. The article [tak-presets](../../src/Encspect.Host/Topics/tak-presets.md) lists them.
- **Channels:** six channels store codec 4, with the speaker assignment where the WAV header has a channel mask.
- **Frames:** in the 33 files every header and data CRC-24 holds, the first and the last frame are info frames, only the last is flagged as the last frame and its samples are the remainder of the stream info's count, no info frame states a previous distance, and the last frame info's end is the end of the file. The files last at most a second, so none shows an info frame between the first and the last. Tests hold the walk to `tak_model.py`'s frames and ffprobe's packets to the same offsets and sizes ([development.md](../development.md#tak-fixtures-from-takc)).
- **Real files** (not committed, `check_tak_samples.py`): both of the FFmpeg archive have 1,015 frames with every CRC-24 holding, the last frame flagged, and 65 info frames, one in 16 frames (about 2 seconds at 5512 samples and 44,100 Hz), which is takStream.pas's rule.

## Not covered

- TAK 1.0 (codec 0), the experimental and lossyWAV codecs (1 and 3), bit depths other than 8, 16 and 24, and more than six channels: the description does not cover them, and the decoder refuses them.
- The coded syntax of a frame as nodes (only the statistics and the series above), the paired types of codec 4's channel table as series: queued ([the survey](../design/coding-graphs.md#optimfrog-and-tak-new-in-the-re-survey)).
- Frames in a TAK stream inside another container (none is routed).
- The info frame's stream info against the metadata's (a disagreement is not a finding), and the previous distance against the position of the previous info frame.
- A file cut or damaged inside its last frame when no last frame info states the end: only the data CRC-24 fails (the walk cannot tell damage from a cut).
- The seek table's points, and a channel count over 16 (the 4-bit field cannot hold it).
- The meaning of the codecs 1 and 3 beyond their names.
