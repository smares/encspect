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
| ASF and WMA (versions 1 and 2) | [asf-wma.md](asf-wma.md) |

## Common to every format

- **Streams:** each format reports its audio streams with codec, sample rate, channels, duration, average bitrate, whether the bitrate varies, and the encoder with a confidence level. The summary and the CSV read the streams.
- **Series:** numbers for graphs, per frame or packet and per second of audio, such as the bitrate.
- **Channel layout:** every stream names the speaker each channel feeds, in the stream's own order, with a common name such as 5.1 where it has one. Channels without a speaker position, such as the two programs of MPEG dual channel, are discrete. Each format's page says where its layout comes from.
- **Findings:** damage and violations of a specification are findings that point to the node they concern. Damaged input never ends the analysis.
- **Descriptions:** every node says what the field is, and many link to a background article (`encspect --learn`).
