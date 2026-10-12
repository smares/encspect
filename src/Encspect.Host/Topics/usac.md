---
id: usac
title: USAC (xHE-AAC)
related: sbr, aac-object-types, audio-specific-config, parametric-stereo
references: ISO/IEC 23003-3 5.2, 6.1; exhale (vendored encoder source)
---

**What it is.** Unified speech and audio coding, ISO/IEC 23003-3, is the MPEG codec behind xHE-AAC, audio object type 42. Earlier codecs suit either music, like AAC, or speech, like the speech codecs of mobile phones; USAC switches between an AAC-like transform coder and linear-prediction tools frame by frame, so one stream can carry speech and music at low bitrates.

**Tools.** Beside the switching core, USAC brings an enhanced [SBR](sbr), with frame lengths and ratios of its own (a 2:1, 8:3 or 4:1 relation between output and core rate), and a stereo coding based on MPEG Surround, which works on a downmix and parameters much as [parametric stereo](parametric-stereo) does.

**Configuration.** The [AudioSpecificConfig](audio-specific-config) carries a UsacConfig: the output sample rate, an index for the core's frame length and the SBR ratio, the channel configuration, and the list of elements every frame holds, single channel, channel pair, LFE or extension elements, each with its settings. Configuration extensions add information such as loudness.

**Independent frames.** Each access unit starts with a flag that says whether it can be decoded without the frames before it. Where playback starts or a stream switches, an audio pre-roll extension can carry the configuration and the frames before it, so the decoder's output is complete from that frame on.

**What you see.** xHE-AAC (USAC) as the codec, the UsacConfig with its elements, each access unit with its IsIndependent flag, and the count of independent access units. The frames themselves are not read further yet. The fixtures come from exhale, an open-source USAC encoder; ffmpeg 6.1 lists no stream for them.
