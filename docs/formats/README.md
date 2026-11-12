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
| WavPack (lossless, hybrid and DSD, with correction files, and with decoding for verification) | [wavpack.md](wavpack.md) |
| FLAC (native and in Ogg, with decoding for verification) | [flac.md](flac.md) |
| Monkey's Audio (all versions parsed; versions 3.80 to 3.99 and 4.11 decoded for verification) | [monkeys-audio.md](monkeys-audio.md) |
| TTA (True Audio, verified by its CRC-32s and decoded for measurements; the frames of a Matroska track) | [tta.md](tta.md) |
| OptimFROG (`.ofr`: the blocks, the stored WAV header, the encoder's mode and the CRC-32 of every data block; no decoder) | [optimfrog.md](optimfrog.md) |
| TAK (`.tak`: the metadata objects with their CRC-24s, the stream info, the encoder's preset and the stored WAV header; no frame walk, no decoder) | [tak.md](tak.md) |
| Apple Lossless (ALAC) frame by frame in MP4 and Matroska (the "alac" codec parser; CAF waits for a CAF parser) | [alac.md](alac.md) |
| Dolby TrueHD and MLP (raw `.thd` and `.mlp` files, with decoding for verification) | [truehd.md](truehd.md) |
| ASF and WMA (versions 1 and 2, Professional, Lossless and Voice frame by frame) | [asf-wma.md](asf-wma.md) |
| WAV (RIFF, RIFX, RF64 and BW64; PCM, floating point and the compressed formats at header level; MPEG audio, AC-3, E-AC-3 and DTS routed to their parsers, and AC-3, E-AC-3, DTS and AAC also as IEC 61937 bursts in the samples; PCM, float, A-law and mu-law samples decoded for measurements) | [wav.md](wav.md) |
| Wave64 (Sony's 64-bit WAVE, `.w64`; the chunks of a WAVE file in GUID-named chunks, and the same decoding) | [wav.md](wav.md#wave64) |
| AIFF and AIFF-C (all compression types at header level; the uncompressed, float, A-law and mu-law types decoded for measurements) | [aiff.md](aiff.md) |
| Matroska and WebM (audio tracks routed to the codec parsers; video at container level) | [matroska.md](matroska.md) |

The formats not read yet, in the order they are planned, are on [planned.md](planned.md).

## Codecs in containers

Which container hands which codec to its codec parser, and which codecs are decoded. A container routes only the combinations that writers in the wild produce; a combination nobody writes stays at container level, with the track named and a `codec-not-analyzed` finding.

- **routed:** the container hands the codec's data to its parser, which reads it as in the codec's own file.
- **packets, not analyzed:** handed over, but the MPEG audio parser reads only one contiguous payload (`mpeg.packets-not-analyzed`).
- **planned:** a combination a known writer produces (named where known), not routed yet.
- **later:** waits for a real file to commit as a fixture.
- **not planned:** a combination no known writer produces.

| Codec | MP4 | Matroska | Ogg | ASF | WAV, Wave64, BW64 | Own file | Decoded |
|---|---|---|---|---|---|---|---|
| AAC | routed | routed | not planned | not planned | routed, as the ADTS frames of IEC 61937 bursts | ADTS, ADIF | no |
| MPEG audio | routed where its samples lie in one stretch; else packets, not analyzed (planned: FFmpeg) | packets, not analyzed (planned: FFmpeg, mkvmerge) | not planned | planned | routed, by the format tag or the sub-format GUID; IEC 61937 bursts planned (FFmpeg's spdif muxer) | yes | no |
| AC-3, E-AC-3 | routed | routed | not planned | planned (AC-3) | routed, by the format tag or the sub-format GUID, and as IEC 61937 bursts | yes | no |
| DTS | routed | routed | not planned | not planned | routed, by the format tag, as a stream in 16-bit stereo PCM samples, and as IEC 61937 bursts | yes, and `.dtshd` | no |
| Vorbis | not planned | routed | routed | not planned | not planned | no | yes (measured) |
| Opus | planned (FFmpeg) | routed | routed | not planned | not planned | no | yes (measured) |
| Speex | not planned | not planned | routed | not planned | not planned | no | no |
| FLAC | planned (FFmpeg) | routed | routed | not planned | not planned | yes | yes |
| ALAC | routed | routed | not planned | not planned | not planned | no (CAF is planned after 0.5) | yes |
| WavPack | not planned | routed | not planned | not planned | not planned | yes | yes |
| TTA | not planned | routed | not planned | not planned | not planned | yes | yes |
| OptimFROG | not planned | not planned | not planned | not planned | not planned | yes | no |
| TAK | not planned | not planned | not planned | not planned | not planned | yes | no |
| Dolby TrueHD, MLP | planned (FFmpeg, `mlpa`) | routed | not planned | not planned | not planned, the lead's decision of 2026-11-07 (IEC 61937 MAT bursts are named and timed) | yes | yes |
| WMA (versions 1 and 2, Professional, Lossless, Voice) | not planned | later, through `A_MS/ACM` | not planned | routed | not planned | ASF | no |
| Monkey's Audio | not planned | not planned | not planned | not planned | not planned | yes | yes |
| Musepack | not planned | not planned | not planned | not planned | not planned | yes | no |
| PCM | planned (FFmpeg) | planned (FFmpeg, mkvmerge) | not planned | named, not analyzed (`asf.codec-not-analyzed`) | the container's own stream | AIFF and AIFF-C too | in WAV, Wave64 and AIFF |

- **AIFF and AIFF-C** route no codec; their uncompressed, float, A-law and mu-law samples are decoded for measurements.
- **`A_MS/ACM`** in older Matroska files holds a WAVEFORMATEX; routing it by its format tag, which reaches every codec parser the format tag names, comes once a real file from a muxer that writes it is committed as a fixture.
- **Decoded** means a decoder verifies the stream's checksums and feeds the measurements, wherever a route reaches the codec.

## Depth by format

How far into each coding Encspect sees today, on the depth levels of the [plan](../plan.md#breadth-and-depth); a milestone in parentheses is where the missing level is planned. Each format's page has the detail.

- **Container structure:** every container (MP4, Matroska and WebM, Ogg, ASF, WAV, Wave64, BW64, AIFF, `.dtshd`) is read at its own level, and each raw stream format by its frames, so the table leaves that level out. The exceptions are the contents of ASF script commands and markers, and WavPack 1 to 3, which is not read at all (both 0.4).
- **Codec syntax:** the headers and frame-level fields of every coding are read, so the table leaves that level out too; WMA Voice is read down to the sizes that end a frame and the fields that name it.
- **Entropy-coded data:** whether the parser or the decoder walks the coded values (Huffman, Golomb, range or run-level codes).
- **Decoded audio:** verified means a decoder checks a checksum stored over the decoded audio; measured means `--measure` gets the spectrum, levels and loudness.
- **Interpretation:** every format reports its statistics and bitrate series, and names the encoder and writer where its evidence allows. The last column names, by series name, the graphs of coding choices it adds: category and flag series, and the numeric "Coded Bandwidth" and "Channel Bit Share".

| Coding | Entropy-coded data | Decoded audio | Graphs of coding choices |
|---|---|---|---|
| MPEG audio | Layer III scalefactors and Huffman data (the entropy pass, every analysis); Layers I and II up to the samples, and their samples where decoded | measured (Layers I to III) | Stereo Coding, Channel Modes, Joint Stereo Modes, Intensity Stereo Bounds, Block Usage, Block Sizes, Coded Bandwidth, Channel Bit Share |
| AAC | Main, LC, SSR and LTP: every raw data block with its spectral data; ER, ELD and USAC not read (0.5) | no (0.5) | Block Usage, Block Sizes, Stereo Coding, Coded Bandwidth |
| AC-3, E-AC-3 | every audio block up to its mantissas; the adaptive hybrid transform and enhanced coupling not read (0.3); the audio blocks at the reduced rates not read (not planned) | no (0.3) | Blocks With Block Switching, Blocks With Coupling, Blocks With Rematrixing, Blocks With Spectral Extension (E-AC-3), Coded Bandwidth ([ac3.md](ac3.md#what-is-graphed)) |
| DTS | core subframes not read (0.4); the extensions' data not read (follow-ups after 0.5) | no (core: 0.4; extensions: follow-ups after 0.5) | Stereo Coding of the core |
| Vorbis | floors and residues of every packet (the entropy pass, every analysis) | measured | Block Sizes, Coded Bandwidth |
| Opus | every SILK and CELT frame to its final range coder state (the entropy pass, every analysis) | measured | Coded Bandwidth (from the TOC bytes), Block Sizes, Stereo Coding, Intensity Stereo Bound (CELT) |
| Speex | the coded frame data not read (0.4) | no (0.4) | none |
| Musepack | SV4 to SV7 subband layout per frame; the frames inside SV8 packets not read (0.4) | no (0.4) | Stereo Coding (SV4 to SV7) |
| WMA versions 1 and 2 | walked to the end of every frame, with the run-level coefficients | no (0.5) | Stereo Coding |
| WMA Professional | walked to the end of every frame | no (0.5) | Stereo Coding |
| WMA Voice | the sizes that end a frame | no (0.5, if the lead wants it) | none |
| WMA Lossless | walked to the end of every frame | no (0.5) | Stereo Coding |
| FLAC | walked by the parser | verified and measured | Stereo Coding |
| WavPack | walked by the decoder | verified and measured | Stereo Coding |
| Monkey's Audio | walked by the decoder | verified and measured (versions 3.80 to 3.99 and 4.11) | none |
| TTA | walked by the decoder; the frames' CRC-32s are checked over the coded bytes | measured (audio format 1) | none |
| OptimFROG | the blocks are walked and their CRC-32s checked over the compressed data; the data itself is not read | no decoder | none |
| TAK | the metadata objects are walked and their CRC-24s checked; the frames are not walked | no decoder | none |
| ALAC | walked by the parser | measured (no checksum to verify) | Element Coding, Stereo Coding |
| Dolby TrueHD, MLP | walked by the parser | verified and measured | none (Stereo Coding: 0.4) |
| PCM, float, A-law, mu-law (WAV, Wave64, AIFF) | none to walk | measured | none |

- **Graphs of coding choices** for the formats whose data the parsers already read come in 0.4; those that need the entropy pass come with each format's decoder ([coding-graphs.md](../design/coding-graphs.md#the-survey)).

## Common to every format

- **Streams:** each format reports its audio streams with codec, sample rate, channels, duration, average bitrate, whether the bitrate varies, and the encoder with a confidence level.
  - The summary and the CSV read the streams.
  - Streams that play at the same time share a playback group, and the summary counts the longest of a group once ([architecture.md](../architecture.md#streams-and-series)).
- **Series:** numbers for graphs, per frame or packet and per second of audio, such as the bitrate. A **category series** holds the category of each value of each frame or packet (block types, stereo coding), with the counts over the stream that equal the tally node it is anchored to, and groups under the shared headings "Stereo Coding", "Block Switching", "Coded Bandwidth" and "Bit Distribution" in the GUI; the design is [coding-graphs.md](../design/coding-graphs.md).
- **Channel layout:** every stream names the speaker each channel feeds, in the stream's own order, with a common name such as 5.1 where it has one. Channels without a speaker position, such as the two programs of MPEG dual channel, are discrete. Each format's page says where its layout comes from.
- **Findings:** damage and violations of a specification are findings that point to the node they concern. Damaged input never ends the analysis.
- **Descriptions:** every node says what the field is, and many link to a background article (`encspect --learn`).
