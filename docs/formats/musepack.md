# Musepack

Musepack SV8, SV7, and SV4 to SV6.

## What is read

- **SV8**, packet by packet:
  - the stream header, with its CRC-32;
  - ReplayGain and encoder info;
  - the seek table, checked against the audio packets;
  - chapters, with their APEv2 tags;
  - the stream end.
  - Once 4,096 packets that are no audio are detailed, further packets that are no audio become runs, one node each with their count, whatever their key. A packet in a run is not read, so its fields are not checked; what needs no fields still counts: a key outside A to Z or one the specification does not reserve, a ReplayGain or seek table offset packet after the first audio packet, a repeated stream header, and that a ReplayGain packet exists.
  - Three kinds are read past the 4,096 all the same: the stream end, and the first 8 each of the stream header, ReplayGain and encoder info packets that come after them, since the stream info needs them.
  - The findings of the packets that are read are counted once per kind, with the first case named, as with unknown keys.
  - A seek table offset that points into a run is not reported, since the table it names may be there. The entries of all seek tables together are kept up to 4,194,304 for the check against the audio packets; later tables are counted and not compared.
  - After 4,096 audio packets in a row too short for one bit per frame (empty ones among them), which no encoder writes, the packets are not followed further. libmpcdec reads at least one bit for each frame (its used bands, in `mpc_decoder_read_bitstream_sv8`), and the stream header gives the frames in a packet.
  - A sample count of 0, which the specification uses for an unknown length, is a finding; libmpcdec then outputs no samples, though it decodes every frame.
  - Packets past the header's sample count add no frames, since decoders stop there.
  - A stream header that ends before its two bytes with the sample rate and the channels reports sample rate 0 and no channels (libmpcdec refuses it), and counts no samples.
- **SV7**, frame by frame: the 20-bit frame lengths are followed through the 32-bit words. For each frame, the parser reads the used subbands, the mid/side subbands and the noise-filled ones.
  - With true gapless, the last frame's samples come from the 11 bits after it, as libmpcdec takes them, not from the header.
  - The extra frame that gapless may need is read only where it can be there: not where a tag starts at the next word, nor where its subband resolutions run past its length or out of range.
  - After 4,096 frames in a row whose subband resolutions run past their length (a length of 0 among them), which no encoder writes, the frames are not followed further. The same holds for SV4 to SV6.
- **SV4 to SV6**: the header, then frame by frame as libmpcdec 1.2.6 reads them: the used subbands and the mid/side subbands. This covers streams with a variable bitrate, no intensity stereo and one frame per length.
- **SV4 to SV6 streams that libmpcdec 1.2.6 refuses** (a constant bitrate, intensity stereo, blocks of frames): the header only, with a finding that says why.

## Memory and time

- Every frame or audio packet that is followed costs one record (24 bytes for SV4 to SV7, 32 for SV8) and a point in the bitrate series, for at least 20 bits of the file (a frame's length field) or 3 bytes (a packet's key and size). The memory tests hold SV5 and SV7 to 90 bytes kept and 120 at the peak per frame, and the same per audio packet.
- Units too short to hold what they must (the runs above) stop the walk after 4,096. Interleaving one plausible unit in 4,096 does not escape the bound above: the others still cost one record each, for their 20 bits or 3 bytes.
- Packets that are no audio cost nothing each past the detailed ones: the memory tests hold unknown, ReplayGain, encoder info, seek table offset, seek table and stream header packets to about 1 byte per packet.
- The check of the seek table offsets against the tables is linear in their number (a hash set of the tables' offsets); the loops that read no input check for cancellation every 4,096 iterations.

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

## Encoder and writer detection

The encoder comes from the stream's version fields (below) and an ID3v2 TSSE frame (low); an APEv2 Encoder item, which SV8 files and trailing tags carry, names the writer, with low confidence, and so does ffmpeg's muxer version (`musepack.writer-claim-differs` for two that name different software, `musepack.writer-other-format` for software known to write another format).

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

- The builds that write profile 1 are the ones in the rows that name it, and they wrote 1 at every setting tried: the default, `--radio` and `--insane`, where the other builds that take these switches wrote 10, 9 and 12 (mppenc 0.90f to 0.90o and Buschmann's MP+ take others, and were encoded at the default only). `sv7-encoders.json` records every encode, and a test holds the table to them. Other settings were not tried, so a profile 1 build that wrote another profile at one of them would be named as the row for the other profile.
- ReplayGain tools also fill Max Level, so it separates Buschmann's builds from Klemm's only where the ReplayGain fields are 0.
- A range admits the untested builds between its ends. Builds outside the tested ones that share the fields, such as Buschmann's 1.7.0 to 1.7.7, would be named as the tested ones.
- An SV7 version byte above 116 is named as libmpcdec names it, "stable 0.0.0" (its branch for the later versions, with the bits above the byte, all 0); no encoder writes such a byte, so the encoder is reported with low confidence.
- A tag's claim agrees with the stream's own evidence where both name Musepack software (mpcenc, mppenc, mpc2sv8, mpcgain, "MP+" or Musepack); it does not tell versions apart, so "mppenc 1.15u" on an mppenc 1.16 stream counts as agreeing.
- SV4 to SV6 headers have no encoder field.
