# Musepack

Musepack SV8, SV7, and SV4 to SV6.

## What is read

- **SV8**, packet by packet:
  - the stream header, with its CRC-32;
  - ReplayGain and encoder info;
  - the seek table, checked against the audio packets;
  - chapters, with their APEv2 tags;
  - the stream end.
- **SV7**, frame by frame: the 20-bit frame lengths are followed through the 32-bit words. For each frame, the parser reads the used subbands, the mid/side subbands and the noise-filled ones.
- **SV4 and SV5**: the header, then frame by frame as libmpcdec 1.2.6 reads them: the used subbands and the mid/side subbands. This covers streams with a variable bitrate, no intensity stereo and one frame per length.
- **SV4 and SV5 streams that libmpcdec 1.2.6 refuses** (a constant bitrate, intensity stereo, blocks of frames): the header only, with a finding that says why.
- **SV6**: the header only. No SV6 encoder was found to confirm the frame syntax.

## Reported

- the samples decoders output, after the gapless trimming;
- bitrates per frame or packet, and per second;
- the subbands coded mid/side.

## SV4 and SV5 particulars

- The encoders write whole 32-bit words only, so the last frame can run up to 31 bits past the end of the data.
- Decoders (libmpcdec 1.2.6, mppdec) decode all frames but the last, which is reported and not read.

## Not yet covered

- the frames inside SV8 audio packets, which have no length of their own;
- the frames of SV6.

## Encoder detection

- **SV8**: mpcenc 1.30.1, and mpc2sv8 conversions.
- **SV7**: the header's version byte and, where builds share it, the true gapless flag, Max Level and profile 1. Encodes with mppenc 1.16 and every Rarewares Win32 build confirm each name, with high confidence:

| Version byte | Other fields | Encoder |
|---|---|---|
| 116 | | mppenc 1.16 |
| 115 | profile 1 | mppenc 1.15f, 1.15i or 1.15r |
| 115 | other profile | mppenc 1.15t or 1.15u |
| 114 | | mppenc 1.14 |
| 113 | | mppenc 1.13a or 1.13g |
| 112 | | mppenc 1.12 |
| 111 | profile 1 | mppenc 1.11a |
| 111 | other profile | mppenc 1.11b |
| 110 | | mppenc 1.1 |
| 106 | | mppenc 1.06 |
| 0 | true gapless, profile 1 | mppenc 1.05z1, 1.05z2 or 1.06 alpha |
| 0 | true gapless, other profile | mppenc 1.01j to 1.04 |
| 0 | no true gapless, Max Level set | MP+ 1.7.8 to 1.7.9c (Buschmann) |
| 0 | no true gapless, Max Level 0 | mppenc 0.90f to 1.0 (Klemm) |

- The builds that write profile 1 do so whatever profile is asked for.
- SV4 to SV6 headers have no encoder field.
