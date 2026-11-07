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
| TTA (True Audio, verified by its CRC-32s; the frames of a Matroska track) | [tta.md](tta.md) |
| Apple Lossless (ALAC) frame by frame in MP4 and Matroska (the "alac" codec parser; CAF waits for a CAF parser) | [alac.md](alac.md) |
| Dolby TrueHD and MLP (raw `.thd` and `.mlp` files, with decoding for verification) | [truehd.md](truehd.md) |
| ASF and WMA (versions 1 and 2, Professional, Lossless and Voice frame by frame) | [asf-wma.md](asf-wma.md) |
| WAV (RIFF, RIFX, RF64 and BW64; PCM, floating point and the compressed formats at header level; MPEG audio, AC-3, E-AC-3 and DTS routed to their parsers, and AC-3, E-AC-3, DTS and AAC also as IEC 61937 bursts in the samples; PCM, float, A-law and mu-law samples decoded for measurements) | [wav.md](wav.md) |
| Wave64 (Sony's 64-bit WAVE, `.w64`; the chunks of a WAVE file in GUID-named chunks, and the same decoding) | [wav.md](wav.md#wave64) |
| AIFF and AIFF-C (all compression types at header level; the uncompressed, float, A-law and mu-law types decoded for measurements) | [aiff.md](aiff.md) |
| Matroska and WebM (audio tracks routed to the codec parsers; video at container level) | [matroska.md](matroska.md) |

## Codecs in containers

Which container hands which codec to its codec parser, and which codecs are decoded. A container routes only the combinations that writers in the wild produce; a combination nobody writes stays at container level, with the track named and a `codec-not-analyzed` finding.

- **routed:** the container hands the codec's data to its parser, which reads it as in the codec's own file.
- **packets, not analyzed:** handed over, but the MPEG audio parser reads only one contiguous payload (`mpeg.packets-not-analyzed`).
- **planned:** a combination a known writer produces (named), not routed yet.
- **not planned:** a combination no known writer produces.

| Codec | MP4 | Matroska | Ogg | ASF | WAV, Wave64, BW64 | Own file | Decoded |
|---|---|---|---|---|---|---|---|
| AAC | routed | routed | not planned | not planned | routed, as the ADTS frames of IEC 61937 bursts | ADTS, ADIF | no |
| MPEG audio | routed where its samples lie in one stretch; else packets, not analyzed (planned: FFmpeg) | packets, not analyzed (planned: FFmpeg, mkvmerge) | not planned | not planned | routed, by the format tag or the sub-format GUID; IEC 61937 bursts planned (FFmpeg's spdif muxer) | yes | no |
| AC-3, E-AC-3 | routed | routed | not planned | not planned | routed, by the format tag or the sub-format GUID, and as IEC 61937 bursts | yes | no |
| DTS | routed | routed | not planned | not planned | routed, by the format tag, as a stream in 16-bit stereo PCM samples, and as IEC 61937 bursts | yes, and `.dtshd` | no |
| Vorbis | not planned | routed | routed | not planned | not planned | no | no |
| Opus | planned (FFmpeg) | routed | routed | not planned | not planned | no | no |
| Speex | not planned | not planned | routed | not planned | not planned | no | no |
| FLAC | planned (FFmpeg) | routed | routed | not planned | not planned | yes | yes |
| ALAC | routed | routed | not planned | not planned | not planned | no (CAF is phase 5) | no |
| WavPack | not planned | routed | not planned | not planned | not planned | yes | yes |
| TTA | not planned | routed | not planned | not planned | not planned | yes | no |
| Dolby TrueHD, MLP | planned (FFmpeg, `mlpa`) | routed | not planned | not planned | not planned (IEC 61937 MAT bursts are named and timed) | yes | yes |
| WMA (versions 1 and 2, Professional, Lossless, Voice) | not planned | planned, through `A_MS/ACM` | not planned | routed | not planned | ASF | no |
| Monkey's Audio | not planned | not planned | not planned | not planned | not planned | yes | yes |
| Musepack | not planned | not planned | not planned | not planned | not planned | yes | no |
| PCM | planned (FFmpeg) | planned (FFmpeg, mkvmerge) | not planned | the container's own stream | the container's own stream | AIFF and AIFF-C too | in WAV, Wave64 and AIFF |

- **AIFF and AIFF-C** route no codec; their uncompressed, float, A-law and mu-law samples are decoded for measurements.
- **`A_MS/ACM`** in older Matroska files holds a WAVEFORMATEX; routing it by its format tag is planned, which reaches every codec parser the format tag names.
- **Decoded** means a decoder verifies the stream's checksums and feeds the measurements, wherever a route reaches the codec.

## Common to every format

- **Streams:** each format reports its audio streams with codec, sample rate, channels, duration, average bitrate, whether the bitrate varies, and the encoder with a confidence level. The summary and the CSV read the streams. Streams that play at the same time (the tracks of an MP4 movie, of a Matroska Segment, the streams of one Ogg chain link or of one ASF file, the independent substreams of E-AC-3) share a playback group, and the summary counts the longest of a group once.
- **Series:** numbers for graphs, per frame or packet and per second of audio, such as the bitrate. A **category series** holds the category of each value of each frame or packet (block types, stereo coding), with the counts over the stream that equal the tally node it is anchored to, and groups under the shared headings "Stereo Coding", "Block Switching", "Coded Bandwidth" and "Bit Distribution" in the GUI; the design is [coding-graphs.md](../design/coding-graphs.md).
- **Channel layout:** every stream names the speaker each channel feeds, in the stream's own order, with a common name such as 5.1 where it has one. Channels without a speaker position, such as the two programs of MPEG dual channel, are discrete. Each format's page says where its layout comes from.
- **Findings:** damage and violations of a specification are findings that point to the node they concern. Damaged input never ends the analysis.
- **Descriptions:** every node says what the field is, and many link to a background article (`encspect --learn`).
