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
| Dolby TrueHD and MLP (raw `.thd` and `.mlp` files, with decoding for verification) | [truehd.md](truehd.md) |
| ASF and WMA (Professional and Lossless frame by frame, the other codecs at header level) | [asf-wma.md](asf-wma.md) |
| WAV (RIFF, RIFX, RF64 and BW64; PCM, floating point and the compressed formats at header level; MPEG audio, AC-3 and DTS routed to their parsers; PCM, float, A-law and mu-law samples decoded for measurements) | [wav.md](wav.md) |
| Wave64 (Sony's 64-bit WAVE, `.w64`; the chunks of a WAVE file in GUID-named chunks, and the same decoding) | [wav.md](wav.md#wave64) |
| AIFF and AIFF-C (all compression types at header level; the uncompressed, float, A-law and mu-law types decoded for measurements) | [aiff.md](aiff.md) |
| Matroska and WebM (audio tracks routed to the codec parsers; video at container level) | [matroska.md](matroska.md) |

## Common to every format

- **Streams:** each format reports its audio streams with codec, sample rate, channels, duration, average bitrate, whether the bitrate varies, and the encoder with a confidence level. The summary and the CSV read the streams. Streams that play at the same time (the tracks of an MP4 movie, of a Matroska Segment, the streams of one Ogg chain link or of one ASF file, the independent substreams of E-AC-3) share a playback group, and the summary counts the longest of a group once.
- **Series:** numbers for graphs, per frame or packet and per second of audio, such as the bitrate. A **category series** holds the category of each value of each frame or packet (block types, stereo coding), with the counts over the stream that equal the tally node it is anchored to, and groups under the shared headings "Stereo Coding", "Block Switching", "Coded Bandwidth" and "Bit Distribution" in the GUI; the design is [coding-graphs.md](../design/coding-graphs.md).
- **Channel layout:** every stream names the speaker each channel feeds, in the stream's own order, with a common name such as 5.1 where it has one. Channels without a speaker position, such as the two programs of MPEG dual channel, are discrete. Each format's page says where its layout comes from.
- **Findings:** damage and violations of a specification are findings that point to the node they concern. Damaged input never ends the analysis.
- **Descriptions:** every node says what the field is, and many link to a background article (`encspect --learn`).
