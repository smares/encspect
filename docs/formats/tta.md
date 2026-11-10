# TTA

True Audio files (`.tta`, TTA1), and the frames of a TTA track that a container stores without the header (the "tta" codec parser, for Matroska's `A_TTA1`, which the Matroska parser routes to it). A decoder (`Encspect.Formats.Tta.Decoding`, ID `tta`) reads the coded samples of audio format 1 where a front end asks for the samples ([Decoding](#decoding)).

Sources: the TTA authors' format description (https://tta.sourceforge.net/en/tta-format-description/) and its "Compression Theory" page, Matroska's codec mapping for `A_TTA1` (https://www.matroska.org/technical/codec_specs.html), and FFmpeg 6.1.1, libtta-c 2.3 and MediaInfo as black-box readers and writers of the test files. libtta-c 2.3, the TTA authors' own encoder, is built from its pinned source (`libtta_tool.py`) and run on inputs the fixture scripts write; the reference code (libtta, ttaenc) and FFmpeg's TTA sources are LGPL or GPL and were not read for the parser ([decoders.md](../design/decoders.md), decision 10); for the decoder only the describing agent read libtta (below). A field whose meaning no description or tool confirms is not claimed.

The authors' pages describe the container only: the header, the seek table and the frame's CRC-32. The coded samples (the adaptive Rice codes, the filter, the predictor and the channel decorrelation) are described there only in outline, so the decoder follows a clean-room description of libtta-c 2.3 ([development.md](../development.md#tta-decoding-clean-room)): written by an agent that read libtta's source and contains no code, and implemented by an agent that did not read it. Neither agent read FFmpeg's TTA decoder or any other TTA implementation; only the describing agent read libtta. `tta -d` and FFmpeg are black boxes that the decoded samples are compared with ([Decoding](#decoding)).

The description does not say how the frame length is rounded, what each CRC-32 covers or what an entry holds. The readings below come from the 26 files in Encspect's tests that FFmpeg 6.1.1 and libtta-c 2.3 wrote ([development.md](../development.md)), each with its manifest, and the analyzer uses them.

## What is read

- The header, 22 bytes:
  - the signature `TTA1`, the audio format, the channels, the bits per sample, the sample rate and the number of samples in each channel (little-endian);
  - the CRC-32 over the 18 bytes before it, as stored and as computed.
- The seek table: one 4-byte entry for each frame, then a CRC-32 over the entries.
  - The number of entries is the header's samples over the samples in a frame, rounded up. A frame holds the sample rate times 256/245 samples (the description's "frame time" of 1.0448979...). The files of both encoders show it truncated, not rounded: 66,873 samples at 32,000 Hz (33,436.7 per frame) are three frames of 33,436, 33,436 and 1 samples, and 50,155 samples at 24,000 Hz (25,077.55) are three as well; with rounding both would be two. The rates of 44,100 and 22,050 Hz give whole frame lengths (46,080 and 23,040). As the description states, the product is computed in unsigned 32 bits, so a crafted rate of 2^24 Hz or more wraps (16,777,216 Hz leaves 0, the length the table's size is undefined for, as for a rate of 0).
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

- the sample rate, channels, bits per sample, samples (the samples the frames declare: the frame length from the header, the remainder for the last frame; a frame the data cuts short counts them and Lost Samples counts them too), duration and average bitrate (the bytes of the frames held whole, CRC-32s included, over the time their samples last; a cut frame counts in the samples only);
- the bitrate per frame and per second as series, with the smallest and largest frame, a distribution and the bitrate mode: variable where the frame bitrates differ (`IsVbr`), unknown for a single frame;
- the frame length, and the frames whose CRC-32 held and failed.

## Verification

Three checksums are declared and tallied; none needs decoding, so they are compared in every analysis. The stream stores no checksum over its decoded audio, so the decoder declares none: `--verify` alone opens no session and reads no sample (the verification says the decoding was not asked for), as for ALAC and WAV; `--measure` and output decode.

| Checksum | Covers | Counts as covering all of the audio when |
|---|---|---|
| header CRC-32 | the header | never |
| seek table CRC-32 | the seek table | never |
| frame CRC-32 | the coded data | the header's CRC-32 holds, it counts samples, and every frame it calls for is there whole |

A stream is verified when every stored checksum was compared, none failed and the frame CRC-32s cover all of its audio. A file cut after a whole frame, or with frames missing, passes its frame CRC-32s and is unknown.

## Channel layout

The header states a channel count and no speaker positions. The layout comes from the count, as for Monkey's Audio without a channel mask: one channel is the center, two are left and right, more are assigned none. The channel mode is Mono for one channel, Stereo for two and Multichannel for more, and the "Channel Layout" statistic and the stream's layout follow the same rule. (The description lists a channel mask for TTA2 only.)

## Encoder detection

- The stream names no encoder. The audio format is 1 in every file at hand but the password-protected one, and the frames carry no header to differ in.
- An APEv2 `Encoder` item, which FFmpeg's muxer writes as `Lavf<version>`, and an ID3v2 TSSE frame name the writer, with low confidence; two that name different software are `tta.writer-claim-differs`, and software known to write another format (LAME on a TTA stream) is `tta.writer-other-format`.
- Observed in Encspect's test files, and not used by the analyzer: FFmpeg 6.1.1's muxer appends the APEv2 tag with that item unless `-fflags +bitexact` is given (`external-16-stereo-bitexact.tta` has no tag), and its encoder writes audio format 1; libtta-c 2.3 writes no tag and audio format 1 (2 with a password).

## TTA in Matroska

- The "tta" codec parser reads one frame to a packet (`A_TTA1`: each frame kept whole, CRC-32 included; the header and the seek table dropped). It compares each frame's CRC-32 and reports the stream as the file parser does, without the seek table.
- The header comes from the configuration (the CodecPrivate holds the 22-byte header, per the codec mapping), or, without it, the sample rate, channels and bit depth from the container's own figures (`CodecConfiguration.Format`). With the header the frame lengths, the sample count and the number of frames are known (`tta.frame-count-differs` where the container holds another number), and the stream can be verified. Without it the frames are compared, and the stream is unknown, since nothing says how many there should be; its samples come from the frames (below).
- A configuration that is no TTA1 header is `tta.header-invalid`. A payload handed in one piece is `tta.payload-not-analyzed`. A packet shorter than a CRC-32 is `tta.frame-too-small`.
- A configuration longer than the 22-byte header (the mapping's "TTA Header Structure" may be read to include the seek table, which Matroska drops) shows the bytes after it as `Extra Data` and is `tta.configuration-extra` (Info); they are not read.
- A container that states a sample rate, channels or bits per sample other than the header's is `tta.configuration-differs`; the header's figures are used.
- A headerless track that states more than 65,535 channels (the Channels element accepts up to 2^31 - 1) gets `tta.channels-beyond-limit` (Warning): the last frame's samples are not counted, the parser allocates nothing for the channels, and the decoder refuses the track. A last frame with more channels than coded bits holds no whole sample time and is not counted either.
- The Matroska route hands the parser the track's SamplingFrequency, Channels and BitDepth (the mapping uses them) together with the CodecPrivate, which it accepts for `A_TTA1` as the mapping defines it.
- The test files `matroska/external-ffmpeg-tta.mka`, `external-mkvmerge-tta.mka` (mkvmerge 82.0), `external-mkvmerge-tta-mono.mka` (a mono track, whose Channels element mkvmerge leaves out; the default is 1) and `external-ffmpeg-tta-last-frame-one-sample.mka` and `external-mkvmerge-tta-last-frame-one-sample.mka` (the libtta file of 66,873 samples at 32,000 Hz, copied) have no CodecPrivate: neither muxer writes one (ffprobe nevertheless reports 22 bytes of extradata for the FFmpeg file). The header path is covered by `TtaCodecParserTests`.
- **Samples without the header** ([samples-definition.md](../design/samples-definition.md): what the decoding process assigns): every frame but the last holds the frame length for the track's rate, and the last what its codes hold. The codec parser reads the last frame's Rice codes (the parser's code in `Codes.cs`, which the decoder also reads with) and counts the sample times after which they end in the frame's last byte, the rest of it padding. The stream's Samples, Duration and bitrate figures follow, decoded or not, and the frames show their samples.
- **When the padding allows several counts:** a code takes a bit or more, and once a channel's Rice parameters fall to 0 (silence) a code takes one bit, so up to 8 / channels − 1 further counts can end in the same last byte. The analyzer then takes the count nearest what the Segment Duration leaves, and without a Duration the fewest; either way the info finding `tta.last-frame-samples-ambiguous` names the range. The Duration does not always settle it: FFmpeg writes it in milliseconds (44 samples at 44,100 Hz) and counts the last frame as whole (3.135 s for 2.09 s of audio in `external-ffmpeg-tta-last-frame-one-sample.mka`), and mkvmerge writes one near the samples' length (2.089808 s for 2.0897813 s in `external-mkvmerge-tta-last-frame-one-sample.mka`). Where it cannot, the count can be off by up to those few samples, all of them silence. `external-ffmpeg-tta.mka`, whose sine ends near silence, has 44,099 or 44,100, and the Duration picks 44,100.
- **Decoding without the header:** the decoder takes the sample rate, channels and bit depth from the track (`DecodeSetup.Format`) and each frame's samples from the parser.
- Observed and not used by the analyzer: FFmpeg 6.1.1 decodes such a track's last frame as a whole frame and fails on it ("Invalid data found when processing input"), so it outputs the frames before it only; `tta -d` of the TTA file the muxer was given is then the reference.

## Reference for sample counts

- **The TTA authors' decoder is the reference** for the stream's `samples`: libtta-c 2.3's `tta -d` (`tta -dp secret` for `external-libtta-16-stereo-password.tta`, written with that test password), run on a Linux runner by `.github/workflows/official-decode.yml` (see "TTA decoding by libtta-c's tta -d" in [development.md](../development.md), which lists the differences found), in a child process of its own for each file. It is used as a black box ([decoders.md](../design/decoders.md), decisions 6 and 10): its source is not read, and there is no direct path, so the one path is the reader.
- **Counting.** Samples are counted from the PCM bytes after the header of the WAVE file `tta` wrote, with the channel count and bits per sample of that header; the data size the header declares is never used. TTA has no priming or padding, so there is one stage.
- **When `tta -d` gives no reference.** It exits 255 and leaves no output file with the messages "can't read from input file", "file is corrupted" (a header it cannot use), "not compatible file format" (8-bit files, 8 channels) and "password protected file"; it is killed by SIGFPE on `built-sample-rate-zero.tta`. It also exits 0 and says nothing after writing fewer data bytes than its own WAVE header declares (10 variants); that is a stop too. A header of 0 channels gives no sample count (`built-channels-zero.tta`: exit 0, no data). A stop is recorded with its reason and gives no reference, and what a stopped run left is never a figure. FFmpeg 8.0.1 is then the reference, labeled `ffmpeg (fallback)`, and only when it exits 0; else the file has none. The SIGFPE is recorded as a crash and not reported upstream (the project lead's answer, 2026-11-01).
- **What it does not give.** `tta -d` prints no per-frame information, so only the stream's sample count is compared; frame verdicts (`tta.frame-crc-mismatch`, `tta.frame-cut`) are not. On the 48 variants of encoder-written files with a changed coded byte or a changed CRC-32 it decodes 24 of each, writing the whole sample count with exit 0 and no message: its exit status and messages give no verdict on the frame CRC-32s (the PCM is not compared). The two hand-built files with failing frame CRC-32s are stopped, as their frames hold bytes where coded samples would be.
- **FFmpeg is the second column** for files `tta -d` decodes. The analyzer equals the reference for all 24 encoder-written files `tta -d` decodes; FFmpeg equals it in all 24, and for the 8-bit and 8-channel files, which `tta -d` refuses, the analyzer equals FFmpeg's count.
- The reference figures, the run, and every difference with its status are in `tests/fixtures/tta/official-decoders.json`, which `TtaOfficialDecodersTests` holds the analyzer to. The 41 hand-built files hold bytes in place of coded samples: `tta -d` decodes two of them (counted from what it wrote), writes no data for a header of 0 channels, stops on 37, and crashes on one; FFmpeg has a count for one of the 37.
- Derived damage variants (`scripts/fixtures/derive_variants.py tta`: cuts, a changed byte or CRC-32, a frame removed or repeated, the header's samples one more or fewer) are decoded the same way and matched by SHA-256; they are not committed.
- The analyzer has not been changed to the reference. `built-channels-zero.tta`, a header with 0 channels and 100,000 samples, has no reference: `tta -d` exits 0 and writes no data, which gives no count (`noted`). No `pending` item is left in the committed file. The project lead decided on 2026-11-01 that fewer data bytes than the header declares is a stop with no reference, and, in the first TTA check, that the analyzer keeps counting whole frames for a file cut in its last frame (`keep`). The samples definition supersedes the `keep`: a cut frame counts the samples its header declares and is lost, so `samples` equals the header's count that `tta -d` writes.
- The run is 41340327636 (commit fec9cf3); the first version of the run (41334418322, repeated as 41336001984, and 41338214809) gave the same figures except for the stops added after review.

## lossyWAV output

- **What was checked:** TTA's syntax has no count of low bits that are zero. Encspect's test files of the two 24-bit stereo noise-floor signals (`external-lossyflac-b512.flac` and `external-lossyflac-floor-44k.flac`, whose samples are lossyWAV 1.4.2's output at the standard preset), coded with libtta-c 2.3's `tta` (`scripts/fixtures/check_lossywav_coders.py`):
  - the signal before lossyWAV: 148,475 and 104,736 bytes; lossyWAV's output: 148,563 and 104,894 bytes. The zeroed bits bought nothing.
- **Fixture:** `external-libtta-24-stereo-lossywav.tta` (104,894 bytes, libtta-c 2.3), made by `scripts/fixtures/make_external_tta_fixtures.py` from the samples of the committed lossyWAV fixture `flac/external-lossyflac-floor-44k.flac` (the input kind `lossywav-floor-44k` of `flac_signal.py`, decoded by `flac_model.py`); its manifest and golden are committed.
- **What Encspect reports:** the same as for any TTA file. With measuring on, the decoded samples give the levels and the low bits per span, which read as the WAV file's do (`external-libtta-24-stereo-lossywav.measure.json`).
- **No claim:** the file holds no marker and no coded count, so nothing in it names lossyWAV.

## Decoding

- **When:** the decoder runs where the front end asks for the samples (`--measure`, the GUI's audio panels, an `Output` sink of the library). The tree is the same either way. It declares no checksum over the decoded audio, because the stream stores none: the frames' CRC-32s cover the coded bytes only and say nothing about the reconstruction.
- **What it reads:** audio format 1, 1 to 24 bits per sample (decoded at the container width of 1, 2 or 3 bytes; a depth that is not a multiple of 8 is output as the container's values, which for a left-justified source have their low bits zero; no encoder-written file of such a depth is at hand) and any channel count the header states. Each frame is decoded on its own from the same starting state, so a damaged or lost frame costs only itself.
- **The stages** (each channel in turn for every sample time):
  - the adaptive Rice code: the unary part is a run of 1 bits ended by a 0, and the binary part has the width of one of two running parameters; both adapt to a running sum of the values;
  - an adaptive filter of 8 taps, whose coefficients follow the signs of the history (sign-sign least mean squares), with a shift of 10 bits for 8 and 24 bit samples and 9 for 16 bit samples;
  - a fixed first-order predictor of 31/32 (15/16 for 8 bit samples);
  - the undoing of the channel decorrelation: the coded values are differences of neighboring channels, and the last channel is a mid-like one (mono is not decorrelated).
  - All of it in 32-bit wraparound arithmetic; the division by two in the decorrelation rounds toward zero, the shifts toward minus infinity.
- **Output:** signed integers at the container width, interleaved in channel order by the sink (planar blocks of up to 4,096 sample times). 8-bit streams decode to values around 0 (silence is 0), not unsigned bytes.
- **Checked against:** a test-side encoder that inverts the description (round trips for 8, 16 and 24 bits, 1 to 6 channels, silence, noise, the ends of the range, long runs of ones after silence), the worked examples of the description, and `tta -d` and FFmpeg sample for sample on the 26 encoder-written files it can (the 27th is the password file), which `scripts/fixtures/crosscheck_tta_pcm.py` runs in CI ([development.md](../development.md#tta-decoding-clean-room)). `tta -d` refuses the 8-bit and 8-channel files, so FFmpeg is the only reference for them; the 8-bit coding (a predictor of 15/16 and a filter shift of 10) rests on FFmpeg's decode of its own file.
- **Damage:**
  - a sample outside the range of the depth, a Rice code of 31 bits or more, a Rice parameter beyond 32 bits, or coded data that ends inside a sample: the frame is not decoded (`decode.frame-invalid`), and the blocks it had written stay; the next frame decodes on its own;
  - a frame the parser calls damaged (its CRC-32 fails) is decoded all the same;
  - a frame cut by the end of the data or shorter than its CRC-32 is not decoded;
  - bytes left after the last code of a frame are ignored, as the reference ignores them (the decoder reports them as the info `tta.frame-bytes-after-codes`);
  - a frame of more than 64 MiB is not read into memory and not decoded (`tta.frame-not-decoded`, Info).
- **Memory:** a frame is held whole while it is decoded (at most 64 MiB); the output is written in blocks of at most 65,536 samples over all channels; the state is a few hundred bytes for each channel.
- **Work:** `DecodeContext.AddWork` is charged before the work: 16 units a byte of the frame's coded data, and for each sample of each channel the fixed work of `DecodeWork.ForSample` plus 34 (the filter: 8 taps to predict and 8 to update and step, at 1.5 units a tap; the predictor and the decorrelation, 10).
- **Frames decoded:** the first 2,097,152 frames of a stream (`Records.MaxEntries`); a file with more is not decoded past them.

## Not covered

- Audio format 2 (a stream encoded with a password): the decoder refuses it (`decode.not-possible`, "audio format 2"). The password only changes the filter's starting coefficients, but the analyzer takes no password.
- A track in a container that stores no header is decoded as audio format 1, since nothing states the format; a stream encoded with a password would decode to other samples. Its last frame's count can be off by a few samples of silence (TTA in Matroska, above).
- Rice parameters above 24 bits. libtta's bit cache is 32 bits wide, so for a stream whose parameter grows past that the encoders may not round-trip; the decoder reads the bits exactly. `external-24-stereo-loud-noise.tta` (FFmpeg, 24-bit full-scale noise, parameters near 24 bits) decodes to the same samples as `tta -d` and FFmpeg; no committed file goes past 24 bits. A parameter beyond 32 bits is damage.
- 8-bit streams of encoders other than FFmpeg's: the reference decoder refuses 8 bits, and only FFmpeg's file tells how they decode.
- TTA2, and a decrypted view of audio format 2.
- Finding frames in a file whose seek table is damaged beyond its CRC-32 (the description says such a file can be decoded only in an "unseekable" mode; the analyzer follows the lengths).
- No check of the audio beyond the frames' CRC-32s: the stream stores none over the decoded samples.
- Compression ratio per frame, which [coding-graphs.md](../design/coding-graphs.md) plans for TTA after the parser.

## Articles

- **Articles:** `tta-stream`, `tta-seek-table`, `tta-coding`, `tta-in-matroska`.
- **Shared with other formats:** `crc`, `verification`, `bitrate-modes`.
