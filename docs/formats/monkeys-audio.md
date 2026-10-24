# Monkey's Audio

Monkey's Audio files (`.ape`): the lossless format of Matthew T. Ashland's Monkey's Audio. The parser reads every version of the format; the decoder, which verifies the frame CRCs, reads the version that every recent encoder writes (see "Decoding").

## What is read

- Files of version 3.98 and later start with a **descriptor** (52 bytes) and a **header** (24 bytes):
  - the descriptor: the signature (`MAC ` for integer audio, `MACF` for floating point), the file version, the version of the program that wrote the file, the sizes of the descriptor, the header, the seek table, the WAV header data, the frame data and the terminating data, and the MD5;
  - the header: the compression level, the format flags (each one named), the samples in a frame, the samples in the final frame, the number of frames, the bits per sample, the channels and the sample rate.
- Files of earlier versions start with one header of 32 bytes (a peak level and a count of seek elements may follow it) and have no descriptor and no MD5. The parser reads them the same way, down to version 3.80, and reports them with `monkeys-audio.old-format`.
- The **seek table**: one 32-bit offset for each frame. Entries are made 64 bits wide where a value is lower than the one before it, as the SDK does for files over 4 GiB.
- The **WAV header data** (the header of the compressed WAV file, kept so that decompressing gives it back) and the **terminating data** (what followed the samples in the WAV file, such as a LIST chunk). The terminating data is a region of its own in the tree.
- Every **frame**, found by the seek table:
  - the samples it holds (the header's count, the final frame's own);
  - the stored CRC of the decoded audio (31 bits) and, where the top bit of the CRC says so, the special frame codes: silent left channel, silent right channel, pseudo stereo;
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

- One channel is front center; two are front left and front right. The channel mode of a pair is reported as mid/side stereo, since the encoder always codes a pair as a mid and a side channel.
- A file of more channels has no layout. The header counts the channels and says nothing of their positions; the WAV header data may hold a channel mask, which the analyzer does not read. The channels are shown in the order of the file.

## Encoder detection

- The descriptor states the version of the program that wrote the file (13.42 for mac 13.42, shown as "Monkey's Audio 13.42"). The confidence is high when it is there.
- A file before version 3.98 states no program version; the encoder is "Monkey's Audio (file version 3.97)" with medium confidence, the evidence being the format of the file.
- Tags in or around the file that name another program as its writer are reported once as `monkeys-audio.writer-claim-differs`.

## Decoding

- The decoder (`Encspect.Formats.MonkeysAudio.Decoding`, ID `monkeys-audio`) runs when the front end asks for it: `--verify` on the command line, "Verify Audio" in the detail window. The tree is the same either way.
- It decodes files of version 3.99 and 4.11 (the format of every file the SDK has written since 3.99) at all five levels, with 8, 16, 24 and 32 bits per sample, floating point (`MACF`) and 1 to 32 channels. The parser's code reads the descriptor, header, seek table and the start of each frame; the decoder adds the range decoder, the predictors with their neural network filters, the undoing of the mid/side coding of channel pairs, the silent and pseudo-stereo frames and the CRC over the decoded audio.
- Two checksums are declared and tallied:

| Checksum | Covers | Counts as covering all of the audio when |
|---|---|---|
| descriptor MD5 | the coded data | it was compared and every frame the header counts was read in full |
| frame CRC-32 | the decoded audio | every frame the header counts was read in full and every frame stores a CRC |

- The frame CRC is the CRC-32 of the decoded samples in WAV layout, inverted and shifted right by one bit. A frame whose CRC differs is reported with `monkeys-audio.frame-crc-mismatch`; the samples are kept as decoded.
- A file is verified when the MD5 holds and every frame CRC holds. The MD5 alone, which needs no decoding, leaves a file unverified until the CRCs are compared.
- A frame the decoder cannot read (its coded data ends before the samples do, or holds a value the model cannot code) is reported with `decode.frame-invalid` and counts as not decoded; the decoding goes on with the next frame, since every frame starts afresh.
- A frame of more than 64 MiB is not decoded (`monkeys-audio.frame-not-decoded`): the analyzer reads a frame into memory to decode it.
- The decoder is cooperative: it charges its work to the host's budget by the chunk of 4,096 samples (see below) and checks the cancellation token between chunks. It holds one frame's bytes, the samples of one chunk and the filter state of the frame, nothing for each frame.
- **Files it does not decode** (`decode.not-possible`, with the reason): files of version 3.98 and before, and files whose header states a compression level other than 1000 to 5000, other than 1 to 32 channels, other than 8, 16, 24 or 32 bits per sample, or no samples in a frame or more than the SDK accepts. The frames of version 3.93 to 3.98 are coded with another range coder, an older predictor and a different delta filter, and files before 3.93 with the SDK's older decoder. The SDK 13.42 encoder writes none of them and no tool in reach writes a reference file to hold the decoder to, so these versions are parsed and not decoded.
- **The 24-bit retry.** The SDK decodes a frame of 24-bit audio once more in a second mode when its CRC fails, for files that 2019 to 2022 versions wrote (`APEDecompressCore.cpp`). The decoder does the same. No fixture exercises it: the SDK 13.42 encoder never writes such a file, so this path is written from the SDK's code and verified by nothing.
- **Seven channels.** The SDK's own decoder skips the seventh channel of a seven-channel file (`Prepare.cpp`), so its tool fails to verify such a file (error 1009). The decoder follows the SDK, and the frame CRC of the file differs.
- **The work a frame is charged:**
  - for each sample of each channel the frame outputs, the fixed work of the sample for its width (`DecodeWork.ForSample`);
  - for each channel that is coded, 192 units a sample for the range decoder, 48 for the predictor, and one unit for every two taps of the neural network filters of the level (none at fast, 16 taps at normal, 64 at high, 288 at extra high and 1,552 at insane);
  - the charge is made for each chunk of 4,096 samples, so a frame is never charged more than the chunk before it is decoded.
  - A file may be charged 8,192 units for each of its bytes (`WorkPerInputByte`) before `decode.limit` ends the decoding. The weights, the measured time for a unit and how they were chosen are in "As built: Monkey's Audio" of [the decoder design](../design/decoders.md).

## Tags

- APEv2 and ID3v1 tags after the last byte the descriptor counts are the host's, as are ID3v2 tags in front. The descriptor states the end of the stream, so a tag candidate that starts before it belongs to the stream.
- A file before version 3.98 states no end. An ID3v2.4 SEEK frame that points inside the frames ends the stream there, and the bytes after the tag are reported as data (`monkeys-audio.data-after-tag`).

## Findings

- A code that stands for several problems is reported once for each kind, with its count.
- Container: `monkeys-audio.no-descriptor`, `monkeys-audio.header-cut`, `monkeys-audio.junk-before-descriptor`, `monkeys-audio.old-format`, `monkeys-audio.version`, `monkeys-audio.descriptor-size`, `monkeys-audio.header-data-size`, `monkeys-audio.terminating-data-size`, `monkeys-audio.data-cut`, `monkeys-audio.trailing-data`, `monkeys-audio.data-after-tag`.
- Header fields: `monkeys-audio.compression-level`, `monkeys-audio.channels`, `monkeys-audio.bits-per-sample`, `monkeys-audio.sample-rate`, `monkeys-audio.blocks-per-frame`, `monkeys-audio.final-frame-blocks`, `monkeys-audio.no-frames`.
- Seek table and frames: `monkeys-audio.seek-table-size`, `monkeys-audio.seek-table-cut`, `monkeys-audio.seek-table-start`, `monkeys-audio.frame-misplaced`, `monkeys-audio.frame-cut`, `monkeys-audio.frame-too-short`, `monkeys-audio.frames-not-listed`.
- Checksums: `monkeys-audio.md5-mismatch`, `monkeys-audio.md5-not-compared`, `monkeys-audio.frame-crc-mismatch` (decoding), `monkeys-audio.frame-not-decoded` (decoding).
- Encoder: `monkeys-audio.writer-claim-differs`.

## Not covered

- Playback and any output other than the checksum comparison (the PCM sink is for tests and a later export).
- Decoding of files before version 3.99 (see "Decoding"); they are parsed in full.
- The channel positions of files with more than two channels (the WAV header data's channel mask).
- The APEv2 tag's own content beyond what the tag parser reads; the Monkey's Audio link files (`.apl`).
- Monkey's Audio inside other containers (Matroska, MP4): none of the container parsers routes to it.
