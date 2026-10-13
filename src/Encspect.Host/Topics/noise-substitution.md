---
id: noise-substitution
title: Noise substitution
related: musepack-subband-coding, intensity-stereo, wma-frames-and-blocks
references: FFmpeg 6.1.1 libavcodec/wmadec.c; libmpcdec r495 mpc_decoder.c (resolution -1, requantization); mppenc 1.16 mppenc.c (profile table, --pns); mpcenc 1.30.1 (--pns); Musepack SV8 specification (Encoder Info Packet, "PNS tool")
---

**The problem.** Noisy sounds, such as cymbals, breath or rain, cost many bits to code exactly, yet the ear cannot tell one stretch of noise from another of the same loudness and spectrum.

**How it works.** Perceptual noise substitution codes such a band only by its level: the decoder fills it with noise of that level instead of decoding coded samples. The bits saved go to the parts of the signal where details are audible. The trade is irreversible, like [intensity stereo](intensity-stereo): the noise in the decoded band is not the noise that was there.

**In Musepack.** A subband with the resolution -1 is filled with noise: libmpcdec draws random values for its 36 samples and scales them by the subband's scalefactors. mppenc and mpcenc enable it with --pns, and mppenc 1.16's table of profiles gives the low ones a PNS value of their own, so its --telephone uses it unasked. SV7 marks such streams in the high bits of the header's version byte (0x17), SV8 in the encoder info packet's PNS flag.

**In Windows Media Audio.** A block may mark each band above a cutoff frequency as noise: one bit per band of each coded channel says so, and a level is sent for the band instead of coefficients ([Windows Media Audio frames](wma-frames-and-blocks)). Whether a stream uses the tool depends on its sample rate and bitrate.

**What you see.** The PNS flag of the header or encoder info packet, and per SV7 frame and in the statistics the subband channels filled with noise.
