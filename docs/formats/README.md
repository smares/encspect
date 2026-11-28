# Formats

What each format parser reads, checks and reports. Tags have their own pages under [tags](../tags/README.md).

| Format | Page |
|---|---|
| MPEG audio (MP1, MP2, MP3) | [mpeg-audio.md](mpeg-audio.md) |
| MP4, M4A, M4B, 3GP and QuickTime | [mp4.md](mp4.md) |
| AAC (in MP4, ADTS, ADIF) | [aac.md](aac.md) |
| Ogg | [ogg.md](ogg.md) |
| Speex | [speex.md](speex.md) |
| Vorbis | [vorbis.md](vorbis.md) |
| Opus | [opus.md](opus.md) |
| Musepack | [musepack.md](musepack.md) |
| AC-3 and E-AC-3 | [ac3.md](ac3.md) |
| DTS (core, XCh, X96, XXCH and DTS-HD extension substreams, raw and in MP4) | [dts.md](dts.md) |
| DTS-HD files (`.dtshd`: the chunks around a DTS stream) | [dtshd.md](dtshd.md) |
| WavPack (lossless, hybrid and DSD, with correction files, and with decoding for verification; the files of WavPack 1 to 3 at header level, with decoding) | [wavpack.md](wavpack.md) |
| FLAC (native, in Ogg, in MP4 and in Matroska, with decoding for verification) | [flac.md](flac.md) |
| Monkey's Audio (all versions parsed; versions 3.80 to 3.99 and 4.11 decoded for verification) | [monkeys-audio.md](monkeys-audio.md) |
| TTA (True Audio, verified by its CRC-32s and decoded for measurements; the frames of a Matroska track) | [tta.md](tta.md) |
| OptimFROG (`.ofr`: the blocks, the stored WAV header, the encoder's mode and the CRC-32 of every data block; no decoder) | [optimfrog.md](optimfrog.md) |
| TAK (`.tak`: the metadata objects with their CRC-24s, the stream info, the encoder's preset and the stored WAV header, and the frames with their header and data CRC-24s, decoded to compare the MD5 and for the measurements) | [tak.md](tak.md) |
| Apple Lossless (ALAC) frame by frame in MP4 and Matroska (the "alac" codec parser; CAF waits for a CAF parser) | [alac.md](alac.md) |
| Dolby TrueHD and MLP (raw `.thd` and `.mlp` files and the tracks of MP4 and Matroska, with decoding for verification) | [truehd.md](truehd.md) |
| ASF and WMA (versions 1 and 2, Professional, Lossless and Voice frame by frame) | [asf-wma.md](asf-wma.md) |
| WAV (RIFF, RIFX, RF64 and BW64; PCM, floating point and the compressed formats at header level; MPEG audio, AC-3, E-AC-3 and DTS routed to their parsers, and AC-3, E-AC-3, DTS and AAC also as IEC 61937 bursts in the samples; PCM, float, A-law and mu-law samples decoded for measurements) | [wav.md](wav.md) |
| Wave64 (Sony's 64-bit WAVE, `.w64`; the chunks of a WAVE file in GUID-named chunks, and the same decoding) | [wav.md](wav.md#wave64) |
| AIFF and AIFF-C (all compression types at header level; the uncompressed, float, A-law and mu-law types decoded for measurements) | [aiff.md](aiff.md) |
| Matroska and WebM (audio tracks routed to the codec parsers; video at container level) | [matroska.md](matroska.md) |

The formats not read yet, in the order they are planned, are on [planned.md](planned.md).

## Codecs in containers

Which container hands which codec to its codec parser, and which codecs are decoded. A container routes only the combinations that writers in the wild produce; a combination nobody writes stays at container level, with the track named and a `codec-not-analyzed` finding.

- **routed:** the container hands the codec's data to its parser, which reads it as in the codec's own file.
- **packets, not analyzed:** handed over, but the parser cannot place the packets as bytes of the file (compressed or stripped frames); `mpeg-audio.packets-not-analyzed`. No combination is in this state in the table below any more.
- **planned:** a combination a known writer produces (named where known), not routed yet.
- **later:** waits for a real file to commit as a fixture.
- **not planned:** a combination no known writer produces.

| Codec | MP4 | Matroska | Ogg | ASF | WAV, Wave64, BW64 | Own file | Decoded |
|---|---|---|---|---|---|---|---|
| AAC | routed | routed | not planned | not planned | routed, as the ADTS frames of IEC 61937 bursts | ADTS, ADIF | yes (measured: AAC LC) |
| MPEG audio | routed, as packets, whatever the number of stretches | routed (`A_MPEG/L1`, `L2`, `L3`, and `A_MS/ACM` with format tag 0x0050 or 0x0055) | not planned | routed (format tags 0x0050 and 0x0055) | routed, by the format tag or the sub-format GUID, and as IEC 61937 bursts | yes | yes (measured) |
| AC-3, E-AC-3 | routed | routed (also `A_MS/ACM`, by the format tag 0x2000 or the sub-format GUID) | not planned | routed (AC-3, format tag 0x2000; E-AC-3 not planned) | routed, by the format tag or the sub-format GUID, and as IEC 61937 bursts | yes | yes (measured) |
| DTS | routed | routed (also `A_MS/ACM`, by the format tag 0x0008 or 0x2001) | not planned | not routed (no ASF writer's file to check against) | routed, by the format tag, as a stream in 16-bit stereo PCM samples, and as IEC 61937 bursts | yes, and `.dtshd` | yes (measured: the core) |
| Vorbis | not planned | routed | routed | not planned | not planned | no | yes (measured) |
| Opus | routed | routed | routed | not planned | not planned | no | yes (measured) |
| Speex | not planned | not planned | routed | not planned | not planned | no | yes (measured) |
| FLAC | routed | routed | routed | not planned | not planned | yes | yes |
| ALAC | routed | routed | not planned | not planned | not planned | no (CAF is planned after 0.5) | yes |
| WavPack | not planned | routed | not planned | not planned | not planned | yes | yes |
| TTA | not planned | routed | not planned | not planned | not planned | yes | yes |
| OptimFROG | not planned | not planned | not planned | not planned | not planned | yes | no |
| TAK | not planned | not planned | not planned | not planned | not planned | yes | yes (measured) |
| Dolby TrueHD, MLP | routed (`mlpa`; an unprotected track) | routed | not planned | not planned | planned after 0.5 as IEC 61937 MAT bursts ([plan](../plan.md#after-05), item 9); the bursts are named and timed until then | yes | yes |
| WMA (versions 1 and 2, Professional, Lossless, Voice) | not planned | routed (`A_MS/ACM`, by the format tag) | not planned | routed | routed (by the format tag, blocks of the block alignment) | ASF | no |
| Monkey's Audio | not planned | not planned | not planned | not planned | not planned | yes | yes |
| Musepack | not planned | not planned | not planned | not planned | not planned | yes | yes (measured) |
| PCM | routed (`ipcm`, `fpcm` and QuickTime's `sowt`, `twos`, `in24`, `in32`, `fl32`, `fl64`, `raw `, `alaw`, `ulaw` and `lpcm`) | routed (`A_PCM/INT/LIT`, `A_PCM/INT/BIG` and `A_PCM/FLOAT/IEEE`, and `A_MS/ACM` with format tag 1, 3, 6 or 7) | not planned | routed (format tags 1, 3, 6 and 7, and the extensible format) | the container's own stream | AIFF and AIFF-C too | in WAV, Wave64, AIFF, MP4 and Matroska |

- **AIFF and AIFF-C** route no codec; their uncompressed, float, A-law and mu-law samples are decoded for measurements.
- **`A_MS/ACM`** in Matroska holds a WAVEFORMATEX in its CodecPrivate; its format tag (or the extensible form's sub-format GUID) routes the track to the codec parser the WAV and ASF routes use for that coding (`WaveFormatRoutes`). Real files come from FFmpeg's muxer, which writes it for Windows Media Audio, ADPCM, GSM 6.10, A-law and mu-law (the Windows Media Audio, A-law and mu-law tracks are analyzed, the others reported from the TrackEntry); the MPEG audio, AC-3, DTS and PCM routes of the ID are held by hand-built files, since the muxers that were run write those codings under their own IDs.
- **Decoded** means a decoder verifies the stream's checksums and feeds the measurements, wherever a route reaches the codec.

## Depth by format

How far into each coding Encspect sees today, on the depth levels of the [plan](../plan.md#breadth-and-depth); a milestone in parentheses is where the missing level is planned. Each format's page has the detail.

- **Container structure:** every container (MP4, Matroska and WebM, Ogg, ASF, WAV, Wave64, BW64, AIFF, `.dtshd`) is read at its own level, and each raw stream format by its frames, so the table leaves that level out. The exception is WavPack 1 to 3, whose files are read at header level (the stored WAV header, the WavPack header, the bitstream as one region, the trailer and the stream figures) and whose audio is decoded on request ([WavPack 1 to 3](wavpack.md#wavpack-1-to-3)).
- **Codec syntax:** the headers and frame-level fields of every coding are read, so the table leaves that level out too; WMA Voice is read down to the sizes that end a frame and the fields that name it.
- **Entropy-coded data:** whether the parser or the decoder walks the coded values (Huffman, Golomb, range or run-level codes).
- **Decoded audio:** verified means a decoder checks a checksum stored over the decoded audio; measured means `--measure` gets the spectrum, levels and loudness.
- **Interpretation:** every format reports its statistics and bitrate series, and names the encoder and writer where its evidence allows. The last column names, by series name, the graphs of coding choices it adds: category and flag series, and the numeric "Coded Bandwidth" and "Channel Bit Share".

| Coding | Entropy-coded data | Decoded audio | Graphs of coding choices |
|---|---|---|---|
| MPEG audio | Layer III scalefactors and Huffman data (the entropy pass, every analysis); Layers I and II up to the samples, and their samples where decoded | measured (Layers I to III) | Stereo Coding, Channel Modes, Joint Stereo Modes, Intensity Stereo Bounds, Intensity Stereo Bound, Block Usage, Block Sizes, Scalefactor Scaling, Global Gain, Coded Bandwidth, Channel Bit Share |
| AAC | Main, LC, SSR and LTP: every raw data block with its spectral data, stored where AAC LC is decoded; ER, ELD and USAC not read (0.5) | measured (AAC LC; HE-AAC, Main, LTP, ER AAC and USAC: 0.5) | Block Usage, Block Sizes, Window Shapes, TNS, Pulse Data, Noise Substitution, SBR, Parametric Stereo, Stereo Coding, Mid/Side Bands, Intensity Stereo Bound, Coded Bandwidth |
| AC-3, E-AC-3 | every audio block with its mantissa codes (the entropy pass, every analysis); the adaptive hybrid transform and enhanced coupling read too; the audio blocks at the reduced rates not read (not planned) | measured (the CRCs verify the coded data: "lossy stream") | Blocks With Block Switching, Blocks With Coupling, Blocks With Rematrixing, Blocks With Spectral Extension (E-AC-3), Coded Bandwidth ([ac3.md](ac3.md#what-is-graphed)) |
| DTS | the core's subframes walked in every analysis; the extensions' data not read (follow-ups after 0.5) | measured (the core; extensions: follow-ups after 0.5) | Stereo Coding, Coded Bandwidth, Subframes With Block Switching |
| Vorbis | floors and residues of every packet (the entropy pass, every analysis) | measured | Block Sizes, Modes, Coded Bandwidth |
| Opus | every SILK and CELT frame to its final range coder state (the entropy pass, every analysis) | measured | Modes, Frame Sizes, Channel Coding, Coded Bandwidth (from the TOC bytes), Block Sizes, Stereo Coding, Intensity Stereo Bound (CELT) |
| Speex | every frame's parameters (the entropy pass, every analysis) | measured (libspeex 1.2.1's fixed-point decoder, bit for bit) | Narrowband Submodes, Wideband Submodes, Ultra-Wideband Submodes |
| Musepack | every frame's resolutions, scalefactors and samples (the entropy pass, every analysis); the SV4 to SV6 streams libmpcdec 1.2.6 refuses not read | measured (SV7 and SV8; SV4 to SV6 as libmpcdec 1.2.6 decodes them) | Stereo Coding, Noise-Substituted Channels, Coded Bandwidth |
| WMA versions 1 and 2 | walked to the end of every frame, with the run-level coefficients | no (0.5) | Stereo Coding, Block Sizes, Noise-Substituted Blocks, Coded Bandwidth |
| WMA Professional | walked to the end of every frame | no (0.5) | Stereo Coding, Subframe Sizes |
| WMA Voice | the sizes that end a frame | no (0.5, if the lead wants it) | Frame Types |
| WMA Lossless | walked to the end of every frame | no (0.5) | Stereo Coding, Subframe Sizes |
| FLAC | walked by the parser | verified and measured | Stereo Coding, Subframe Types, Fixed Predictor Orders, Partition Orders, Rice With 5-Bit Parameters, Highest Predictor Order, Coefficient Precision, Block Size |
| WavPack | walked by the decoder | verified and measured | Stereo Coding, Decorrelation Passes |
| Monkey's Audio | walked by the decoder | verified and measured (versions 3.80 to 3.99 and 4.11) | Stereo Coding, Silent Frames |
| TTA | walked by the decoder; the frames' CRC-32s are checked over the coded bytes | measured (audio format 1) | none |
| OptimFROG | the blocks are walked and their CRC-32s checked over the compressed data; the data itself is not read | no decoder | none |
| TAK | the metadata objects are walked and their CRC-24s checked; the frames are walked by their sync words and their header and data CRC-24s checked over the coded data; the coded syntax is walked by the parser | verified (the MD5 where there is one) and measured (codec 2 and 4) | Stereo Coding, Subframe Types, Average Predictor Order, Highest Predictor Order, Coefficient Precision, Channels With Wasted Bits, Highest Residual Mode |
| ALAC | walked by the parser | measured (no checksum to verify) | Element Coding, Stereo Coding, Highest Predictor Order |
| Dolby TrueHD, MLP | walked by the parser | verified and measured | Stereo Coding, Highest Predictor Order |
| PCM, float, A-law, mu-law (WAV, Wave64, AIFF) | none to walk | measured | none |

- **Graphs of coding choices** are built for every format with a decoder or a walk of its coded data; the ones still queued are in [coding-graphs.md](../design/coding-graphs.md#the-survey).

## Common to every format

- **Streams:** each format reports its audio streams with codec, sample rate, channels, duration, average bitrate, whether the bitrate varies, and the encoder with a confidence level.
  - The summary and the CSV read the streams.
  - Streams that play at the same time share a playback group, and the summary counts the longest of a group once ([architecture.md](../architecture.md#streams-and-series)).
- **Series:** numbers for graphs, per frame or packet and per second of audio, such as the bitrate. The bitrate series of every format with them carry the concept `bitDistribution` and the quantity "Bitrate", so that front ends group them; each lossless format also has "Compression Ratio" in the same form. A **category series** holds the category of each value of each frame or packet (block types, stereo coding), with the counts over the stream that equal the tally node it is anchored to, and groups under the shared headings "Stereo Coding", "Block Switching", "Coded Bandwidth" and "Bit Distribution" in the GUI; the design is [coding-graphs.md](../design/coding-graphs.md).
- **Channel layout:** every stream names the speaker each channel feeds, in the stream's own order, with a common name such as 5.1 where it has one. Channels without a speaker position, such as the two programs of MPEG dual channel, are discrete. Each format's page says where its layout comes from.
- **Findings:** damage and violations of a specification are findings that point to the node they concern. Damaged input never ends the analysis.
- **Descriptions:** every node says what the field is, and many link to a background article (`encspect --learn`).
