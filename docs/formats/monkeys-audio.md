# Monkey's Audio

Monkey's Audio files (`.ape`): the lossless format of Matthew T. Ashland's Monkey's Audio. The parser reads every version of the format; the decoder, which verifies the frame CRCs and checksums, reads versions 3.80 to 3.99 and 4.11 (see "Decoding").

## What is read

- Files of version 3.98 and later start with a **descriptor** (52 bytes) and a **header** (24 bytes):
  - the descriptor: the signature (`MAC ` for integer audio, `MACF` for floating point), the file version, the version of the program that wrote the file, the sizes of the descriptor, the header, the seek table, the WAV header data, the frame data and the terminating data, and the MD5;
  - the header: the compression level, the format flags (each one named), the samples in a frame, the samples in the final frame, the number of frames, the bits per sample, the channels and the sample rate.
- Files of earlier versions start with one header of 32 bytes (a peak level and a count of seek elements may follow it) and have no descriptor and no MD5. The parser reads them the same way, down to version 3.80, and reports them with `monkeys-audio.old-format`.
- In a file of version 3.80 the seek table is followed by a table of one byte for each frame: the bit of the word the seek entry names at which the frame starts, since the frames of that version follow each other bit by bit (`Old/UnMACOld.cpp`, `SeekToFrame`). The parser keeps the first 1,048,576 of them. A frame whose successor starts inside the word it ends in has its last bits in that word, so the decoder is handed the four bytes after each frame as well; a field of a frame that starts inside a byte (its CRC and special codes) is a node over the bytes its bits touch.
- Where a frame starts with a CRC word: in every file of version 3.93 and later whatever the format flags say, and in an older file only when its CRC flag is set (`Old/UnMACOld.cpp`). All 32 bits of the word are the CRC up to version 3.82, and bit 31 flags the special frame codes from 3.83. An older file without the flag starts each frame with a checksum of 30 bits, Rice-coded, that is no CRC: the parser reads no CRC and no special codes there, and declares the checksum as "frame checksum", which the decoder compares (see "Decoding"). In a file of 3.90 to 3.92 the SDK reads no checksum from such a frame, so the checksum is declared as not stored there.
- The **seek table**: one 32-bit offset for each frame. Entries are made 64 bits wide where a value is lower than the one before it, as the SDK does for files over 4 GiB.
- The **WAV header data** (the header of the compressed WAV file, kept so that decompressing gives it back) and the **terminating data** (what followed the samples in the WAV file, such as a LIST chunk). The terminating data is a region of its own in the tree.
- From the WAV header data the parser reads only the `fmt ` chunk, for the channel layout (see "Channel layout"):
  - it applies to a RIFF or RF64 WAVE header; the chunks before `fmt ` are skipped by their sizes, in at most 64 steps, and no read goes past the header data;
  - the nodes under "WAV Header Data" are Format Tag, Channels and, for WAVE_FORMAT_EXTENSIBLE (0xFFFE) with a cbSize of at least 22, Channel Mask with its Speakers, named as in the WAV parser;
  - an AIFF or CAF header (`FORM`, `caff`) is named in the node's value and its content is not read, so such a file keeps the default layout.
- Every **frame**, found by the seek table:
  - the samples it holds (the header's count, the final frame's own);
  - the stored CRC of the decoded audio (31 bits) and, where the top bit of the CRC says so, the special frame codes: silent left channel (the only channel of a mono file, where this is the one code that counts), silent right channel, pseudo-stereo;
  - the bytes it occupies. The file stores the audio in 32-bit words with the bytes of each word reversed, so a frame that starts or ends inside a word covers its bytes in up to three ranges (`Node.Ranges`), and its node covers whole words.
- The descriptor **MD5** is computed in every analysis, over the WAV header data, the frame data and the terminating data, the header and the seek table, and compared with the stored one.
- The tree lists the first 1,048,576 frames in detail (`monkeys-audio.frames-not-listed`); later frames are read, checked and shown as one stretch. A file of any length costs a bounded amount of memory.

## Reported

- the codec "Monkey's Audio", the sample rate, channels, bits per sample, total samples and duration;
- the compression level by its name (fast 1000, normal 2000, high 3000, extra high 4000, insane 5000);
- the bitrate per frame and per second as series, with the smallest and largest frame and a distribution; the bitrate mode: variable where frames differ in bitrate, constant where they do not and unknown for a single frame (`IsVbr` is false for the last two);
- the counts of silent frames and of pseudo-stereo frames, and the number of frame CRCs stored;
- whether the descriptor MD5 matches.

## Channel layout

- The Monkey's Audio header counts the channels and states no positions. The channel mask of the WAV header data does, and the parser takes the layout from it:
  - a nonzero Channel Mask sets the stream's layout and the "Channel Layout" statistic for any number of channels, mono and stereo included, from the header's channel count (`ChannelLayouts.FromWaveMask`, the mapping the WAV and WavPack parsers use): the channels follow the order of the mask's bits, extra high bits are ignored, extra channels are assigned to no speaker, and bits from 18 up stand for channels without a position;
  - the decoder declares the same layout for the audio it writes, so the measurements of a multichannel file know which channel is the LFE.
- The header data is whatever header the compressed file began with. The SDK stores the header of its source as it is (`WAVInputSource.cpp`: RIFF, RF64, BW64 and Wave64 sources; AIFF, CAF and Sun/NeXT sources keep their own headers). The parser reads the fmt chunk of these:
  - a RIFF, RF64 or BW64 header (the SDK takes the three alike): the chunks before the fmt chunk are skipped by their 32-bit sizes and a pad byte after an odd size; the ds64 chunk of RF64 and BW64 is one of them, and the sizes it gives are not needed to find the fmt chunk;
  - a Wave64 header (Sony; the `APE_FORMAT_FLAG_W64` source `CW64InputSource`): the riff and wave GUIDs (40 bytes), then chunks whose ID is a GUID (four characters and a fixed tail for fmt and data), whose 64-bit size counts the 24 bytes of the chunk's own header, and that start at multiples of 8 bytes. The fmt chunk holds the same structure as in a WAVE file. The layout is the one the WAV parser's Wave64 support reads (`docs/formats/wav.md`).
- The container is named in the "WAV Header Data" node: a RIFF, RF64 or BW64 WAVE header, a Wave64 header, an AIFF header or a CAF header. Of the last two, only the name is read: their channel information (the `chan` chunk, the `CHAN` chunk) is not.
- Without a nonzero mask the layout is the default one: one channel is front center, two are front left and front right, and a file of more channels has no layout. This is the case for mask 0, a header of format tag 1 with more than two channels, an extensible format with a cbSize under 22 (which has no mask), an AIFF or CAF header, a header the walk could not read, a file that keeps no header data (the flag for a WAV header created on decompression), and a file whose descriptor has that flag and still stores header data: the SDK ignores the stored bytes then (`APEHeader.cpp`, `nWAVHeaderBytes`), so the parser does too and reports `monkeys-audio.wav-header-unused`.
- The channel mode of a pair is reported as mid/side stereo, since the encoder always codes a pair as a mid and a side channel; more channels are "Multichannel". The channels are shown in the order of the file.
- In Encspect's test files, written by `mac` 13.44, the stored mask is the one of the WAV the encoder was given: 0x33 for quadraphonic, 0x3F for 5.1 at the back, 0x60F for 5.1 at the sides, 0x63F for 7.1, the same masks from a BW64 source and a Wave64 source. A WAV of six channels with format tag 1, or an extensible one with mask 0, is stored as it is and gives no layout.
- Findings:
  - `monkeys-audio.wav-channels-differ` (Warning): the fmt chunk states other channels than the header;
  - `monkeys-audio.channel-mask-mismatch` (Info, as the WAV parser's `wav.channel-mask-mismatch`): the mask names more or fewer speakers than the header states channels; a mask with bit 31 (`SPEAKER_ALL`) is no count of speakers and is not compared;
  - `monkeys-audio.wav-extensible-incomplete` (Warning, as the WAV parser's `wav.extensible-incomplete`): the format tag is 0xFFFE and the cbSize, cut to the bytes the fmt chunk holds after it, is under the 22 bytes of valid bits, mask and sub-format, or the fmt chunk (16 or 17 bytes) ends before the cbSize, so there is no mask to read;
  - `monkeys-audio.wav-header-damaged` (Warning): the header data ends inside a chunk or inside the fmt chunk, holds no fmt chunk before its data chunk or its end, states a fmt chunk of less than 16 bytes, holds a Wave64 chunk of a size under 24, or holds more than 64 chunks before the fmt chunk;
  - `monkeys-audio.wav-header-unused` (Info): the flag for a header created on decompression is set, and the descriptor still counts header data.
- A damaged header data never stops decoding: the frames do not depend on it.

## Encoder detection

- The descriptor states the version of the program that wrote the file (13.44 for mac 13.44, shown as "Monkey's Audio 13.44"; Encspect's test files were written by 13.44, and one seven-channel file by 13.42). The confidence is high when it is there.
- A file before version 3.98 states no program version; the encoder is "Monkey's Audio (file version 3.97)" with medium confidence, the evidence being the format of the file.
- Tags in or around the file that name another program as its writer are reported once as `monkeys-audio.writer-claim-differs`.

## Decoding

- The decoder (`Encspect.Formats.MonkeysAudio.Decoding`, ID `monkeys-audio`) runs when the front end asks for it: `--verify` on the command line, "Decode Audio" in the detail window (which also measures the audio). The tree is the same either way.
- It decodes files of versions 3.80 to 3.99 and 4.11. From 3.93 on, at all five levels (the predictor of 3.93 and 3.94 has no insane level), with 8, 16, 24 and 32 bits per sample (the old header's flags name 8, 16 and 24 only), floating point (`MACF`) and 1 to 32 channels. Before 3.93, at the four levels fast to extra high, with 8, 16 and 24 bits and one or two channels. The parser's code reads the descriptor or header, the seek table and the start of each frame; the decoder adds the range decoder, the predictors with their neural network filters, the undoing of the mid/side coding of channel pairs, the silent and pseudo-stereo frames and the CRC over the decoded audio.
- What each version changes, from the SDK 13.44 (the decoder follows its code, version by version). From 3.93 on (`APEDecompressCore.cpp`):

| File version | Header | Range coder | Predictor | Neural network filters |
|---|---|---|---|---|
| 3.99, 4.11 | descriptor | current | current | new delta |
| 3.98 | descriptor | 3.90 to 3.98 | current | new delta |
| 3.95 to 3.97 | one header | 3.90 to 3.98 | current | old delta |
| 3.93, 3.94 | one header | 3.90 to 3.98 | one filter of four taps | old delta |

  From 3.95 on the two channels of a pair are predicted with each other's values, the second one's value coded first; in 3.93 and 3.94 each channel is predicted on its own, the first one's value coded first (`APEDecompressCore.cpp`).

- Before version 3.93 the SDK uses another decoder (`Old/UnMACOld.cpp`, `Old/APEDecompressCoreOld.cpp`) and so does Encspect (`OldFrameDecoder`). It decodes a frame one channel at a time: the whole array of the first channel (the mid channel, X), then the whole array of the second (the side channel, Y), each with a bit coding of its own and then a predictor that undoes the prediction of the whole array; the pair is made into left and right as in the later versions. The versions differ in these ways:

| File version | A frame starts | Word in front of the frame | Bit coding | 8-bit samples |
|---|---|---|---|---|
| 3.90 to 3.92 | on a byte | CRC in 31 bits and special codes | the range coder of the later versions, an array at a time, each starting on a byte boundary (3.90 codes a value's bits in one piece, 3.91 and later in two above 16 bits) | signed |
| 3.89 (above 3880) | on a byte | the same | Rice code with one parameter that follows a running sum, an overflow of 16 or more adding 4 to the parameter for each 16 | signed |
| 3.86 to 3.88 (up to 3880) | on a byte | the same | the same without the cap | signed |
| 3.84, 3.85 | on a byte | the same | Rice code: the first five values with parameter 10, the next 59 with one that follows their mean, the rest with one that follows the sum of the last 64 | signed |
| 3.83 | on a byte | the same | the same | unsigned |
| 3.81, 3.82 | on a byte | CRC in 32 bits, no special codes | the same | unsigned |
| 3.80 | on a bit, from the table of bits | CRC in 32 bits, no special codes | the same | unsigned |

  - The Rice coding caps the overflow for a version above 3880 (`GenerateArrayRice`), so a header version from 3881 to 3890 is read as 3.89 is.
  - Without the CRC flag the word in front is the frame checksum below; a value of 0 flags a silent frame. The SDK's bit array of 3.90 to 3.92 answers only the 32-bit reads (`UnBitArrayBase.cpp`, `DecodeValue`), so it reads no checksum from a file of those versions without the flag and gives silence for every frame; the decoder does not decode such a file.
  - The predictors by level, all from 3.80 on except the fast one (`Old/AntiPredictor*Old.cpp`): fast, a stage of order 2 and then one of order 1 over the whole array (`AntiPredictorFastOld`, from 3.32); normal, three adaptive stages, over the last values, then over the output of the first, then a share of the output before (`CAntiPredictorNormal3800To3929`); high, a filter of 16 taps over the array in front of those; extra high, a filter of 128 taps up to 3.82 and 256 taps from 3.83 in front of those, and from 3.83 a filter of 8 taps in front of that. The first elements of an array (4, 16, 128 or 256) are summed and not predicted, and an array shorter than 8, 20, 134 or 262 (normal, high, extra high up to 3.82, extra high from 3.83) is not predicted at all. There is no insane level.
  - Special codes (from 3.83, with the CRC flag): silent left and right channels, pseudo-stereo. A pair with both channels silent codes no value, a pair flagged pseudo-stereo codes the first channel only, the second being zero, and one silent channel alone changes nothing.
  - **Where a frame starts.** The SDK seeks only to the first frame it decodes and after a gap (`Old/UnMACOld.cpp`, `SeekToFrame`); every later frame continues where the one before ended, on the next byte boundary from 3.81. The analyzer takes the position of every frame from the seek table (and, in 3.80, the table of start bits), so that one damaged frame cannot move the frames after it. The two agree on every file whose tables are right. They part where a table is wrong: a start bit of 32 or more, which no encoder writes, is reported with `monkeys-audio.frame-start-bit` and the frame is read from there, and `built-old-3800-start-bit-outside-word` is a file that `mac -V` passes (it never looks at the table) and the analyzer does not verify.
  - **The end of the data.** The range coder of 3.90 to 3.92 reads two bytes past the end of an array. After the last frame the SDK's bit array reads on into the terminating data and the tag and gives zeros past the end of the file (`UnBitArrayBase.cpp`, `FillBitArray`); the decoder gives zeros past the end of an undamaged last frame, so a file that ends where its coded data does (the `*-unpadded` fixtures) is decoded as `mac -V` decodes it. A frame that the file cuts gets none and is invalid.
  - 8-bit samples are the unsigned bytes of the WAV data up to 3.83; from 3.84 the predictors see them as signed numbers, 128 below.

- References. The versions from 3.98 on have no public file this repository could hold; the checks are these:
  - `legacy-*` fixtures: files of 3.80 to 3.98 written by `scripts/fixtures/monkeys_audio_old.py` (3.80 to 3.92) and `scripts/fixtures/monkeys_audio_legacy.py` (3.93 to 3.98), encoders written as the inverse of the SDK's decoders. The SDK 13.44 decodes those versions and does not write them, so the files are held to its decoders: `mac -V` decodes every frame with the decoder of the version and compares the CRC or the checksum, and `mac -d` gives the samples. They cover every level (insane from 3.95 on; extra high from 3.80 to 3.92, 3.86 to 3.88 included), 8, 16 and 24 bits, mono and stereo, silent and pseudo-stereo frames, loud noise and spikes (the escapes of the codings), several frames, the old header's peak level, files without the CRC flag and, for 3.80, frames that start inside a word. Three files of 3.90 to 3.92 end where the coded data of the last frame does (`*-unpadded`), and `legacy-3890-normal-16-mono-overflow-exactly-16` is hand-made: some values are coded with an overflow of exactly 16 or 32 (a raised parameter and a value that would have fit the old one), which the encoder never writes and the SDK reads, so that a decoder that caps only above 16 fails it.
  - FFmpeg 8.0.1 decodes all the files of 3.93 to 3.98 to the same samples. Of the 39 files before 3.93 it decodes 16 to the SDK's samples, all at the normal and high levels with the CRC flag. In the others it decodes other samples, or fewer, without an error (the fast and extra high levels, files without the CRC flag, a silent frame of 3.83 at 8 bits, the spikes of 3.89) or fails (pseudo-stereo of 3.84, 3.90 without the flag); it compares the CRC only from 3.90 and not without the flag. `crosscheck_monkeys_audio_fixtures.py` lists each, and the SDK's own decoder is the reference for them.
  - A real file of 3.97, `sh3.ape` of the FFmpeg sample archive (level normal, with CRCs), downloaded and pinned by SHA-256 (`tests/conformance/monkeys-audio-samples.json`, `check_monkeys_audio_samples.py`): `mac -V` verifies it, its samples from `mac -d` equal the decoder's, and the analyzer verifies all three frame CRCs. The archive has no other file of these versions that is small enough to download in every run.
- Two checksums are declared and tallied (a file of the old header without the CRC flag has "frame checksum" in place of the second, and no MD5):

| Checksum | Covers | Counts as covering all of the audio when |
|---|---|---|
| descriptor MD5 | the coded data | it was compared and every frame the header counts was read in full |
| frame CRC-32 | the decoded audio | every frame the header counts was read in full and every frame stores a CRC |
| frame checksum | the decoded audio | every frame the header counts was read in full |

- The frame CRC is the CRC-32 of the decoded samples in WAV layout, inverted and shifted right by one bit; up to version 3.82 it is not shifted and all 32 bits of the stored word are compared. A frame whose CRC differs is reported with `monkeys-audio.frame-crc-mismatch`; the samples are kept as decoded.
- The frame checksum of a file without the CRC flag is the sum, over the samples of the frame, of the absolute values of the left and the right sample (of the sample for one channel), in 32 bits (`Old/UnMACOld.cpp`, `CalculateOldChecksum`), stored Rice-coded with parameter 30. It does not tell the order of the samples or a change that keeps the sum; a frame whose checksum differs is reported with `monkeys-audio.frame-checksum-mismatch`, and the samples are kept as decoded.
- A file is verified when the MD5 holds and every frame CRC holds. The MD5 alone, which needs no decoding, leaves a file unverified until the CRCs are compared.
- A frame the decoder cannot read (its coded data ends before the samples do, or holds a value the model cannot code) is reported with `decode.frame-invalid` and counts as not decoded; the decoding goes on with the next frame, since every frame starts afresh.
- A frame of more than 64 MiB is not decoded (`monkeys-audio.frame-not-decoded`): the analyzer reads a frame into memory to decode it. A frame the file holds only in part is measured by the bytes it holds, not by what the seek table or the descriptor states.
- The decoder is cooperative: it charges its work to the host's budget by the chunk of 4,096 samples (see below) and checks the cancellation token between chunks. It holds one frame's bytes, the samples of one chunk and the filter state of the frame, nothing for each frame. Before 3.93 it also holds the arrays of a whole frame (at most 73,728 samples of each of two channels, with a third array for the values before the predictor) until the frame is done, since those versions predict an array at a time.
- **Files it does not decode** (`decode.not-possible`, with the reason):
  - files before version 3.80, whose predictors the decoder does not have;
  - files of 3.90 to 3.92 without the CRC flag, which the SDK reads as silence (see above);
  - files before 3.93 whose header states a level other than 1000 to 4000, which the SDK's old decoder refuses;
  - files before 3.93 whose header states other than one or two channels: the SDK does not refuse them, but its old decoder decodes no samples for them, so the frame fails its CRC (or passes on a stored checksum of 0) and nothing is left to compare;
  - files whose version is above 3890 and below 3900, from which the SDK's bit array reads no values (`Old/UnBitArrayOld.cpp`, `GenerateArray`), and above 3920 and below 3930, which `CAPEDecompressOld` refuses (`Old/APEDecompressOld.cpp`);
  - files of version 3.80 whose table of start bits is not in the file or lists more than 1,048,576 frames;
  - files of 3.93 and 3.94 at the level insane (the SDK's predictor of those versions has none);
  - files whose header states a compression level other than 1000 to 5000, other than 1 to 32 channels, other than 8, 16, 24 or 32 bits per sample, or no samples in a frame or more than the SDK accepts.
  - A frame of a file before 3.93 that states more samples than a frame of its version holds (9,216 or 73,728, the last frame included) is reported with `decode.frame-invalid`, as the SDK refuses such a file.
- **The 24-bit retry.** Monkey's Audio 5.01 to 8.51 (November 2019 to August 2022) wrote 24-bit files with the first stage of the predictor summed in 64 bits, which the SDK's decoder undoes in an "interim mode" it enters when a frame of a 24-bit file fails its CRC (`APEDecompressCore.cpp`, `Interim.cpp`). The decoder does the same: it holds the samples of such a frame until the CRC has passed, decodes it again in the interim mode when it does not, and keeps the mode for the next frame only if the CRC passes in it. This differs from the SDK, which keeps the mode for the rest of the file: its tools stop at the first bad frame, but the analyzer goes on, and one damaged frame must not make the frames after it fail. A frame that fails in both modes gives the samples of the normal mode. `external-interim-24-stereo` is a file of that period, written by `mac` built with the one line of its encoder changed back (`make_interim_monkeys_audio_fixture.py`); the unchanged `mac` 13.44 verifies it through the same retry. `built-24-bit-frame-crc-changed` is a normal file of four loud 24-bit frames with a damaged CRC in the second: one mismatch, and the frames after it verify.
- **Seven channels.**
  - SDK 13.42 and earlier did not code the seventh channel of a seven-channel file (`Prepare.cpp`: `nStartChannel` was 7), so the frame CRC of such a file does not hold and `mac -V` fails it (error 1009).
  - 13.44 fixed it without a new file version (`APE_FILE_VERSION_NUMBER` stays 3990): the seventh channel is coded, and a file does not state which behavior wrote it.
  - The decoder follows 13.44, so it reports `monkeys-audio.frame-crc-mismatch` for a seven-channel file written by 13.42 or earlier, as 13.44's `mac -V` does.
  - `external-normal-16-7ch` is written by 13.44 and verifies; `external-normal-16-7ch-sdk-1342` is the same signal written by 13.42 and does not.
- **Samples of a pair and of the center.** A file of 16 bits and six channels or more has its channels 2 and 3 checked for 16 bits like the samples of a pair, as the SDK does; a value that does not fit makes the frame invalid.
- **The work a frame is charged:**
  - for each sample of each channel the frame outputs, the fixed work of the sample for its width (`DecodeWork.ForSample`);
  - for each channel that is coded, 192 units a sample for the range decoder, 48 for the predictor, and one unit for every two taps (three for every four at 32 bits) of the neural network filters of the level (none at fast, 16 taps at normal, 64 at high, 288 at extra high and 1,552 at insane);
  - for each channel of a frame flagged silent, which codes nothing, 40 units a sample more: the zeros still go through the channel pairing, the byte conversion, the CRC and the sink;
  - the charge is made for each chunk of 4,096 samples, so a frame is never charged more than the chunk before it is decoded.
  - Before version 3.93 the fixed work of the sample is the same, and for each value of each coded channel 32 units for the Rice codes or 192 for the range coder of 3.90 to 3.92, and for the predictor 8 units at fast, 40 at normal, 64 at high and, at extra high, 120 up to 3.82 (a filter of 128 taps, counted as 64 units, and 16 for its history) and 208 from 3.83 (256 taps counted as 128, 16 and 24 for the filter of 8 taps); a frame flagged silent is charged 10 units a sample more for each channel. A channel's array is decoded and then predicted whole, each in chunks of 4,096 values charged before they are done.
  - A file may be charged 8,192 units for each of its bytes (`WorkPerInputByte`) before `decode.limit` ends the decoding. The weights, the measured time for a unit and how they were chosen are in "As built: Monkey's Audio" of [the decoder design](../design/decoders.md).

## Tags

- APEv2 and ID3v1 tags after the last byte the descriptor counts are the host's, as are ID3v2 tags in front. The descriptor states the end of the stream, so a tag candidate that starts before it belongs to the stream.
- A file before version 3.98 states no end. An ID3v2.4 SEEK frame that points inside the frames ends the stream there, and the bytes after the tag are reported as data (`monkeys-audio.data-after-tag`).

## Findings

- A code that stands for several problems is reported once for each kind, with its count.
- Container: `monkeys-audio.no-descriptor`, `monkeys-audio.header-cut`, `monkeys-audio.junk-before-descriptor`, `monkeys-audio.old-format`, `monkeys-audio.version`, `monkeys-audio.descriptor-size`, `monkeys-audio.header-data-size`, `monkeys-audio.terminating-data-size`, `monkeys-audio.data-cut`, `monkeys-audio.trailing-data`, `monkeys-audio.data-after-tag`.
- WAV header data: `monkeys-audio.wav-header-damaged`, `monkeys-audio.wav-header-unused`, `monkeys-audio.wav-extensible-incomplete`, `monkeys-audio.wav-channels-differ`, `monkeys-audio.channel-mask-mismatch`.
- Header fields: `monkeys-audio.compression-level`, `monkeys-audio.channels`, `monkeys-audio.bits-per-sample`, `monkeys-audio.sample-rate`, `monkeys-audio.blocks-per-frame`, `monkeys-audio.final-frame-blocks`, `monkeys-audio.no-frames`.
- Seek table and frames: `monkeys-audio.seek-table-size`, `monkeys-audio.seek-table-cut`, `monkeys-audio.seek-table-start`, `monkeys-audio.frame-misplaced`, `monkeys-audio.frame-cut`, `monkeys-audio.frame-too-short`, `monkeys-audio.frame-start-bit` (3.80), `monkeys-audio.frames-not-listed`.
- Checksums: `monkeys-audio.md5-mismatch`, `monkeys-audio.md5-not-compared`, `monkeys-audio.frame-crc-mismatch` (decoding), `monkeys-audio.frame-checksum-mismatch` (decoding), `monkeys-audio.frame-not-decoded` (decoding).
- Encoder: `monkeys-audio.writer-claim-differs`.

The descriptor MD5 is worked out over the layout the descriptor states, as `VerifyFileW2` reads it, with the header found at the end of a descriptor of at least 52 bytes. For a descriptor that states fewer than 52 bytes for itself, the SDK is not consistent: its quick verify reads the header from the stated offset (and reports error 1009 on `built-descriptor-bytes-smaller.ape`), while its decoder reads the header after 52 bytes. The analyzer follows the decoder's layout, the one the frames are found from, and reports `monkeys-audio.descriptor-size`.

## Not covered

- Playback and any output other than the checksum comparison (the PCM sink is for tests and a later export).
- Decoding of files before version 3.80 and of the files named under "Files it does not decode"; they are parsed in full.
- The rest of the WAV header data: only the fmt chunk's format tag, channels and channel mask are read, from a RIFF, RF64, BW64 or Wave64 header. The sizes of the ds64 chunk, the fact chunk and every other chunk are not used. An AIFF or CAF header's channel information (the `chan` chunk, the `CHAN` chunk) is not read.
- The APEv2 tag's own content beyond what the tag parser reads; the Monkey's Audio link files (`.apl`).
- Monkey's Audio inside other containers (Matroska, MP4): none of the container parsers routes to it.
