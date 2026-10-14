# Musepack

Musepack SV8, SV7, and SV4 to SV6.

## What is read

- **SV8**, packet by packet:
  - the stream header, with its CRC-32;
  - ReplayGain and encoder info;
  - the seek table, checked against the audio packets;
  - chapters, with their APEv2 tags;
  - the stream end.
  - Once 4,096 packets are detailed, runs of further packets that are no audio and do not describe the stream (unknown keys, chapters) become one node each, with their count; the stream's own packets are still read.
  - After 4,096 audio packets in a row without a payload, which no encoder writes, the packets are not followed further.
  - A sample count of 0, which the specification uses for an unknown length, is a finding; libmpcdec then decodes nothing.
  - Packets past the header's sample count add no frames, since decoders stop there.
- **SV7**, frame by frame: the 20-bit frame lengths are followed through the 32-bit words. For each frame, the parser reads the used subbands, the mid/side subbands and the noise-filled ones.
  - With true gapless, the last frame's samples come from the 11 bits after it, as libmpcdec takes them, not from the header.
  - The extra frame that gapless may need is read only where it can be there: not where a tag starts at the next word, nor where its subband resolutions run past its length or out of range.
  - After 4,096 frames in a row with a length of 0, which no encoder writes, the frames are not followed further.
- **SV4 to SV6**: the header, then frame by frame as libmpcdec 1.2.6 reads them: the used subbands and the mid/side subbands. This covers streams with a variable bitrate, no intensity stereo and one frame per length.
- **SV4 to SV6 streams that libmpcdec 1.2.6 refuses** (a constant bitrate, intensity stereo, blocks of frames): the header only, with a finding that says why.

## Reported

- the samples decoders output, after the gapless trimming;
- bitrates per frame or packet, and per second;
- the subbands coded mid/side.

## SV4 and SV5 particulars

- The encoders write whole 32-bit words only, so the last frame can run up to 31 bits past the end of the data.
- Decoders (libmpcdec 1.2.6, mppdec) decode all frames but the last, which is reported and not read.
- A stream whose last frame ends inside its last word, followed by zero bits, is not recognized: the encoders never write one, and accepting it would weaken the check that stands in for a magic number.

## SV6 particulars

- No SV6 encoder was found: every Rarewares mppenc build from 0.90 to 1.15u writes SV7.
- The SV6 frame syntax comes from one decoder lineage: libmpcdec 1.2.6, which reads SV4, SV5 and SV6 frames with one function, and the Portable Musepack decoder, the C++ code it was ported to C from. Fixtures derived from SV5 encodes test that reading; no SV6 encoder output confirms it.
- Unlike SV4 and SV5, libmpcdec 1.2.6 decodes the last counted frame. It then reads 11 bits after it: how many of its samples belong to the audio, 0 meaning all 1,152.
- The parser reports these 11 bits as a node after the last frame, and computes the stream's samples from them as libmpcdec 1.2.6 outputs them.
- More than 1,152 samples in those bits is a finding; libmpcdec 1.2.6 still decodes the frame.
- mppdec (Buschmann, June 2000), the only separate decoder at hand, decodes every counted frame of the derived fixtures. It confirms that the last counted frame is decoded, not the 11 bits or the sample count.
- The stream must end in the word that holds those 11 bits, on the assumption that an SV6 encoder wrote them whole.
- Where they end exactly on a word boundary, one more word of zeros may follow and is shown as its own node, without a finding. mppenc 1.16, the SV7 descendant of the old encoders, writes such a word (`FinishBitstream` in encode_sv7.c).
- Samples assume every frame whole: a damaged frame stops the decoder, which then outputs fewer.
- The SV6 fixtures are SV5 encodes relabeled as version 6, not an encoder's output (see [development](../development.md)).

## Not yet covered

- the frames inside SV8 audio packets, which have no length of their own (planned: entropy pass).

## Encoder detection

- **SV8**: mpcenc 1.30.1, and mpc2sv8 conversions.
- **SV7**: the header's version byte and, where builds share it, the true gapless flag, Max Level and profile 1. Encodes with mppenc 1.16 and every Rarewares Win32 build confirm each name, with high confidence:

| Version byte | Other fields | Encoder |
|---|---|---|
| 116 | | mppenc 1.16 |
| 115 | profile 1 | mppenc 1.15f to 1.15r |
| 115 | other profile | mppenc 1.15t to 1.15u |
| 114 | | mppenc 1.14 |
| 113 | | mppenc 1.13a to 1.13g |
| 112 | | mppenc 1.12 |
| 111 | profile 1 | mppenc 1.11a |
| 111 | other profile | mppenc 1.11b |
| 110 | | mppenc 1.1 |
| 106 | | mppenc 1.06 |
| 0 | true gapless, profile 1 | mppenc 1.05z1 to 1.06 alpha |
| 0 | true gapless, other profile | mppenc 1.01j to 1.04 |
| 0 | no true gapless, Max Level set, ReplayGain 0 | MP+ 1.7.8 to 1.7.9c (Buschmann) |
| 0 | no true gapless, Max Level 0, ReplayGain 0 | mppenc 0.90f to 1.0 (Klemm) |
| 0 | no true gapless, ReplayGain set | both of the above, medium confidence |

- The builds that write profile 1 do so whatever profile is asked for.
- ReplayGain tools also fill Max Level, so it separates Buschmann's builds from Klemm's only where the ReplayGain fields are 0.
- A range admits the untested builds between its ends. Builds outside the tested ones that share the fields, such as Buschmann's 1.7.0 to 1.7.7, would be named as the tested ones.
- An SV7 version byte above 116 gets no name from libmpcdec's reading (it reads it as an SV8 version, 0.0.0); it is reported with low confidence.
- SV4 to SV6 headers have no encoder field.
