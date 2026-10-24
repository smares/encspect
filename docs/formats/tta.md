# TTA

True Audio files (`.tta`, TTA1), and the frames of a TTA track that a container stores without the header (the "tta" codec parser, for Matroska's `A_TTA1`; the Matroska route is a later change). There is no decoder: the coded samples are not read.

Sources: the TTA authors' format description (https://tta.sourceforge.net/en/tta-format-description/) and its "Compression Theory" page, Matroska's codec mapping for `A_TTA1` (https://www.matroska.org/technical/codec_specs.html), and FFmpeg 6.1.1 and MediaInfo as black-box readers and writers of the test files. The reference code (libtta, ttaenc) and FFmpeg's TTA sources are LGPL or GPL and were not read ([decoders.md](../design/decoders.md), decision 10); a field whose meaning no description or tool confirms is not claimed.

## What is read

- The header, 22 bytes:
  - the signature `TTA1`, the audio format, the channels, the bits per sample, the sample rate and the number of samples in each channel (little-endian);
  - the CRC-32 over the 18 bytes before it, as stored and as computed.
- The seek table: one 4-byte entry for each frame, then a CRC-32 over the entries.
  - The number of entries is the header's samples over the samples in a frame, rounded up. A frame holds the sample rate times 256/245 samples (the description's "frame time" of 1.0448979...), truncated; files written by FFmpeg 6.1.1 at 32,000 Hz show the truncation (33,436 samples, not 33,437).
  - An entry is the length of its frame in bytes, its CRC-32 included.
- Every frame: the coded bytes and the CRC-32 after them, compared with one computed over the coded bytes. The frames are walked by the seek table's lengths; the file is read once, in chunks, so a frame of any length costs no more than the chunk.
- Tags: an ID3v2 tag before the header, and APEv2, Lyrics3 and ID3v1 tags after the last frame are the host's. The stream declares its end (`DeclaredEnd`: the end of the last frame by the seek table, where the header and the whole table are there and the table's CRC-32 holds), so a tag-like run of bytes inside the frames is not a tag (`tag.near-miss`), and bytes after the frames that are no tag are `tta.data-after-frames`. A table that fails its CRC-32 declares no end, and a tag is then looked for as in a file with no declared size.
- The second version's signature, `TTA2`, is reported (`tta.version-2`) and nothing more is read: the description lists its header as in development, and no tool at hand writes it.

## Detection

- A header whose CRC-32 holds at the start of the data (after leading tags) is certain (class 100).
- The signature with a CRC-32 that fails and fields an encoder could write (audio format 1 or 2, channels, a sample rate), or `TTA2`, is likely (80); the signature with fields that cannot be a header, or cut short, is possible (70).
- A header further in is no match: a Matroska track's CodecPrivate holds one, among other places.

## Damage

- A header or seek table whose CRC-32 fails (`tta.header-crc-mismatch`, `tta.seek-table-crc-mismatch`); frames are read by the lengths all the same, and a damaged length shows as frames that fail their own CRC-32.
- A frame whose CRC-32 fails (`tta.frame-crc-mismatch`, once with a count).
- Data that ends in the header (`tta.header-truncated`), in the seek table (`tta.seek-table-truncated`; the entries present are read, and every frame is then past the end) or in a frame (`tta.frame-cut`), and frames the table lists past the end of the data (`tta.frames-missing`).
- A seek table entry shorter than a frame's CRC-32 (`tta.frame-too-small`).
- A header with a sample rate of 0 (`tta.sample-rate-zero`: the frame length, and with it the table's size, is undefined; the rest is shown as unread data), 0 channels (`tta.channels-zero`), 0 samples (`tta.no-samples`), a bit depth other than 8, 16 and 24 (`tta.bits-per-sample-unusual`, Info), and an audio format other than 1 and 2 (`tta.audio-format-unknown`).
- Audio format 2 is `tta.audio-format-encrypted` (Info). The description does not define the field; FFmpeg 6.1.1 writes 1 and reads 2 as an encrypted stream that needs a password ("Missing password for encrypted stream"), which is the only evidence. Without a file an encoder wrote, the CRC-32s of such a stream are compared as for any other, on the assumption that they cover the stored bytes.
- Data that does not begin with a signature, when the parser is forced (`tta.no-signature`).
- An ID3v2 tag found inside the stream through a SEEK frame ends the stream there, and the bytes after the tag are not read (`tta.data-after-tag`), as for the other formats; this applies only where the stream declares no end.

## Reported

- the sample rate, channels, bits per sample, samples (the samples of the frames read whole), duration and average bitrate (the bytes of the frames, CRC-32s included, over the time their samples last);
- the bitrate per frame and per second as series, with the smallest and largest frame, a distribution and the bitrate mode: variable where the frame bitrates differ (`IsVbr`), unknown for a single frame;
- the frame length, and the frames whose CRC-32 held and failed.

## Verification

Three checksums are declared and tallied; none needs decoding, so they are compared in every analysis, and the stream's decoding is `NoDecoder` (`--verify` adds the info `decode.no-decoder`).

| Checksum | Covers | Counts as covering all of the audio when |
|---|---|---|
| header CRC-32 | the header | never |
| seek table CRC-32 | the seek table | never |
| frame CRC-32 | the coded data | the header's CRC-32 holds, it counts samples, and every frame it calls for is there whole |

A stream is verified when every stored checksum was compared, none failed and the frame CRC-32s cover all of its audio. A file cut after a whole frame, or with frames missing, passes its frame CRC-32s and is unknown.

## Channel layout

The header states a channel count and no speaker positions, so the stream names none: the channel mode is Mono for one channel, Stereo for two and Multichannel for more. (The description lists a channel mask for TTA2 only.)

## Encoder detection

- The stream names no encoder. The audio format is 1 in every file at hand, and the frames carry no header to differ in.
- An APEv2 `Encoder` item, which FFmpeg's muxer writes as `Lavf<version>`, and an ID3v2 TSSE frame name the writer, with low confidence; two that name different software are `tta.writer-claim-differs`, and software known to write another format (LAME on a TTA stream) is `tta.writer-other-format`.
- Observed in Encspect's test files, and not used by the analyzer: FFmpeg 6.1.1's muxer appends the APEv2 tag with that item unless `-fflags +bitexact` is given (`external-16-stereo-bitexact.tta` has no tag), and its encoder writes audio format 1.

## TTA in Matroska

- The "tta" codec parser reads one frame to a packet (`A_TTA1`: each frame kept whole, CRC-32 included; the header and the seek table dropped). It compares each frame's CRC-32 and reports the stream as the file parser does, without the seek table.
- The header comes from the configuration (the CodecPrivate holds the 22-byte header, per the codec mapping), or, without it, the sample rate, channels and bit depth from the container's own figures (`CodecConfiguration.Format`). With the header the frame lengths, the sample count and the number of frames are known (`tta.frame-count-differs` where the container holds another number), and the stream can be verified. Without it the frames are compared, and the stream is unknown, since nothing says how many there should be.
- A configuration that is no TTA1 header is `tta.header-invalid`. A payload handed in one piece is `tta.payload-not-analyzed`. A packet shorter than a CRC-32 is `tta.frame-too-small`.
- The test file `matroska/external-ffmpeg-tta.mka` has no CodecPrivate: FFmpeg's muxer writes none.

## Not covered

- The coded samples: no decoding, no waveform, spectrogram or lowpass for TTA, and no check of the audio beyond the frames' CRC-32s.
- TTA2, and a decrypted view of audio format 2.
- Finding frames in a file whose seek table is damaged beyond its CRC-32 (the description says such a file can be decoded only in an "unseekable" mode; the analyzer follows the lengths).
- Compression ratio per frame, which [coding-graphs.md](../design/coding-graphs.md) plans for TTA after the parser.
