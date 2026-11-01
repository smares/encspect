# TTA

True Audio files (`.tta`, TTA1), and the frames of a TTA track that a container stores without the header (the "tta" codec parser, for Matroska's `A_TTA1`, which the Matroska parser routes to it). There is no decoder: the coded samples are not read.

Sources: the TTA authors' format description (https://tta.sourceforge.net/en/tta-format-description/) and its "Compression Theory" page, Matroska's codec mapping for `A_TTA1` (https://www.matroska.org/technical/codec_specs.html), and FFmpeg 6.1.1, libtta-c 2.3 and MediaInfo as black-box readers and writers of the test files. libtta-c 2.3, the TTA authors' own encoder, is built from its pinned source (`libtta_tool.py`) and run on inputs the fixture scripts write; the reference code (libtta, ttaenc) and FFmpeg's TTA sources are LGPL or GPL and were not read ([decoders.md](../design/decoders.md), decision 10). A field whose meaning no description or tool confirms is not claimed.

The description does not say how the frame length is rounded, what each CRC-32 covers or what an entry holds. The readings below come from the 26 files in Encspect's tests that FFmpeg 6.1.1 and libtta-c 2.3 wrote ([development.md](../development.md)), each with its manifest, and the analyzer uses them.

## What is read

- The header, 22 bytes:
  - the signature `TTA1`, the audio format, the channels, the bits per sample, the sample rate and the number of samples in each channel (little-endian);
  - the CRC-32 over the 18 bytes before it, as stored and as computed.
- The seek table: one 4-byte entry for each frame, then a CRC-32 over the entries.
  - The number of entries is the header's samples over the samples in a frame, rounded up. A frame holds the sample rate times 256/245 samples (the description's "frame time" of 1.0448979...). The files of both encoders show it truncated, not rounded: 66,873 samples at 32,000 Hz (33,436.7 per frame) are three frames of 33,436, 33,436 and 1 samples, and 50,155 samples at 24,000 Hz (25,077.55) are three as well; with rounding both would be two. The rates of 44,100 and 22,050 Hz give whole frame lengths (46,080 and 23,040).
  - An entry is the length of its frame in bytes (the description: "the current frame data length"). In the files of both encoders it includes the frame's CRC-32, and the entries add up to the length of the frames.
- Every frame: the coded bytes and the CRC-32 after them, compared with one computed over the coded bytes (zlib's CRC-32 of them, in the files of both encoders). The frames are walked by the seek table's lengths; the frames are read once, in chunks, so a frame of any length costs no more than the chunk. (The stream's declared end reads the seek table once more.)
- Limits: the report keeps at most 2,097,152 entries (`Records.MaxEntries`): their lengths for the seek point nodes, and the records of the frames the data holds for the frame nodes, the bitrate graphs and the figures. A seek table can list billions, so an entry past the end of the data keeps its 4-byte length and nothing else, and entries past the limit are counted and their frames' CRC-32s compared but have no node or graph point (`tta.entries-limit`).
- Tags: an ID3v2 tag before the header, and APEv2, Lyrics3 and ID3v1 tags after the last frame are the host's. The stream declares its end (`DeclaredEnd`: the end of the last frame by the seek table, where the header and the whole table are there and the table's CRC-32 holds), so a tag-like run of bytes inside the frames is not a tag (`tag.near-miss`), and bytes after the frames that are no tag are `tta.data-after-frames`. A table that fails its CRC-32 declares no end, and a tag is then looked for as in a file with no declared size.
- The second version's signature, `TTA2`, is reported (`tta.version-2`) and nothing more is read: the description lists its header as in development, and no tool at hand writes it. The 36-byte header it lists has a CRC-32 ("CRC32 block footer") that the analyzer does not compute, since no file shows what it covers.

## Detection

- A header whose CRC-32 holds at the start of the data (after leading tags) is certain (class 100).
- The signature with a CRC-32 that fails and fields an encoder could write (audio format 1 or 2, channels, a sample rate) is likely (80); the signature with fields that cannot be a header, or cut short, is possible (70).
- `TTA2` is likely (80) only with the 36 bytes of its header there and 1 to 64 channels, 1 to 64 bits per sample and a sample rate of 1 to 1,000,000 Hz; four bytes of ASCII alone, as in a text file that begins with them, are possible (70).
- A header further in is no match: a Matroska track's CodecPrivate holds one, among other places.

## Damage

- A header or seek table whose CRC-32 fails (`tta.header-crc-mismatch`, `tta.seek-table-crc-mismatch`); frames are read by the lengths all the same, and a damaged length shows as frames that fail their own CRC-32.
- A frame whose CRC-32 fails (`tta.frame-crc-mismatch`, once with a count).
- Data that ends in the header (`tta.header-truncated`), in the seek table (`tta.seek-table-truncated`; the entries present are read, and every frame is then past the end) or in a frame (`tta.frame-cut`), and frames the table lists past the end of the data (`tta.frames-missing`).
- A seek table entry shorter than a frame's CRC-32 (`tta.frame-too-small`).
- A header with a sample rate of 0 (`tta.sample-rate-zero`: the frame length, and with it the table's size, is undefined; the rest is shown as unread data), 0 channels (`tta.channels-zero`), 0 samples (`tta.no-samples`), a bit depth other than 8, 16 and 24 (`tta.bits-per-sample-unusual`, Info), and an audio format other than 1 and 2 (`tta.audio-format-unknown`).
- Audio format 2 is `tta.audio-format-encrypted` (Info). The description does not define the field. libtta-c 2.3 writes 2 for a stream encoded with a password (`external-libtta-16-stereo-password.tta`, password "secret"), and FFmpeg 6.1.1 refuses such a file without one ("Missing password for encrypted stream"). In that file the frame CRC-32s cover the stored bytes: the CRC-32 of each frame's bytes before its last four equals the stored value, as in the files without a password, and FFmpeg decodes the file with its `-password` option and no CRC error. The analyzer therefore compares them as for any other stream, and the stream can be verified. This rests on one file and one password; a stream made another way is not covered.
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

- The stream names no encoder. The audio format is 1 in every file at hand but the password-protected one, and the frames carry no header to differ in.
- An APEv2 `Encoder` item, which FFmpeg's muxer writes as `Lavf<version>`, and an ID3v2 TSSE frame name the writer, with low confidence; two that name different software are `tta.writer-claim-differs`, and software known to write another format (LAME on a TTA stream) is `tta.writer-other-format`.
- Observed in Encspect's test files, and not used by the analyzer: FFmpeg 6.1.1's muxer appends the APEv2 tag with that item unless `-fflags +bitexact` is given (`external-16-stereo-bitexact.tta` has no tag), and its encoder writes audio format 1; libtta-c 2.3 writes no tag and audio format 1 (2 with a password).

## TTA in Matroska

- The "tta" codec parser reads one frame to a packet (`A_TTA1`: each frame kept whole, CRC-32 included; the header and the seek table dropped). It compares each frame's CRC-32 and reports the stream as the file parser does, without the seek table.
- The header comes from the configuration (the CodecPrivate holds the 22-byte header, per the codec mapping), or, without it, the sample rate, channels and bit depth from the container's own figures (`CodecConfiguration.Format`). With the header the frame lengths, the sample count and the number of frames are known (`tta.frame-count-differs` where the container holds another number), and the stream can be verified. Without it the frames are compared, and the stream is unknown, since nothing says how many there should be.
- A configuration that is no TTA1 header is `tta.header-invalid`. A payload handed in one piece is `tta.payload-not-analyzed`. A packet shorter than a CRC-32 is `tta.frame-too-small`.
- A configuration longer than the 22-byte header (the mapping's "TTA Header Structure" may be read to include the seek table, which Matroska drops) shows the bytes after it as `Extra Data` and is `tta.configuration-extra` (Info); they are not read.
- A container that states a sample rate, channels or bits per sample other than the header's is `tta.configuration-differs`; the header's figures are used.
- The Matroska route hands the parser the track's SamplingFrequency, Channels and BitDepth (the mapping uses them) together with the CodecPrivate, which it accepts for `A_TTA1` as the mapping defines it.
- The test files `matroska/external-ffmpeg-tta.mka` and `matroska/external-mkvmerge-tta.mka` (mkvmerge 82.0) have no CodecPrivate: neither muxer writes one (ffprobe nevertheless reports 22 bytes of extradata for the FFmpeg file). The header path is covered by `TtaCodecParserTests`.

## Not covered

- The coded samples: no decoding, no waveform, spectrogram or lowpass for TTA, and no check of the audio beyond the frames' CRC-32s.
- TTA2, and a decrypted view of audio format 2.
- Finding frames in a file whose seek table is damaged beyond its CRC-32 (the description says such a file can be decoded only in an "unseekable" mode; the analyzer follows the lengths).
- Compression ratio per frame, which [coding-graphs.md](../design/coding-graphs.md) plans for TTA after the parser.
