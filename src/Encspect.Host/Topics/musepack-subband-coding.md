---
id: musepack-subband-coding
title: Musepack: subband coding and quality profiles
related: filter-bank, subband-coding, mid-side-stereo, noise-substitution, musepack-sv7-stream, musepack-sv8-packets, bitrate-modes
references: libmpcdec and libmpcenc (musepack_src r495): synth_filter.c, mpc_decoder.c, requant.c, mpcenc.h; mppenc 1.16; mpcenc 1.30.1 --longhelp
---

**The filterbank.** Musepack splits the audio into 32 subbands of equal width with the polyphase filterbank of MPEG audio Layers I and II ([filterbank](filter-bank)): libmpcdec's synthesis filter cites ISO/IEC 11172-3 for its steps and uses a window of 32 × 16 coefficients. A frame holds 36 samples of every subband, 1,152 per channel, as a Layer II frame does. The filter's delay, 512 − 32 + 1 = 481 samples, is what decoders drop at the start of a stream.

**Resolutions instead of bit allocation.** Like Layers I and II ([subband coding](subband-coding)), Musepack codes each subband's samples directly, with scalefactors. Each frame states a resolution per subband and channel: 0 leaves the subband out, 1 to 7 quantize its samples coarsely and code them with Huffman tables, 8 to 17 store them in ever more bits, and -1 fills the subband with noise ([noise substitution](noise-substitution)). Where the stream allows it, each coded subband has a flag that codes it as [mid and side](mid-side-stereo) rather than left and right. Unlike MPEG, nothing ties a frame to a bitrate: each frame is as long as its data, so the bitrate always varies ([bitrate modes](bitrate-modes)).

**Quality profiles.** The encoder aims at a quality, not a bitrate. Its psychoacoustic model decides how much noise each subband may carry, and the quality setting scales that. Musepack names the settings as profiles: --telephone (quality 2), --thumb (3), --radio (4), --standard (5, the default), --extreme (6), --insane (7) and --braindead (8); mpcenc's help puts them at about 60 to 270 kbps. The stream stores the profile as the quality plus 5: 10 is Standard.

**What you see.** The header's max band and profile, and per SV7 frame the subbands used, the mid/side subbands and the noise-filled ones.
