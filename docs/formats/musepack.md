# Musepack

Musepack SV7 and SV8. Stream versions 4 to 6 are named from their header only.

## What is read

- **SV8**, packet by packet:
  - the stream header, with its CRC-32;
  - ReplayGain and encoder info;
  - the seek table, checked against the audio packets;
  - chapters, with their APEv2 tags;
  - the stream end.
- **SV7**, frame by frame: the 20-bit frame lengths are followed through the 32-bit words. For each frame, the parser reads the used subbands, the mid/side subbands and the noise-filled ones.

## Reported

- the samples decoders output, after the gapless trimming;
- bitrates per frame or packet, and per second;
- the subbands coded mid/side.

## Not yet covered

- the frames of stream versions 4 to 6;
- the frames inside SV8 audio packets, which have no length of their own.

## Encoder detection

From the version field: mppenc 1.16, mpcenc 1.30.1, and mpc2sv8 conversions.
