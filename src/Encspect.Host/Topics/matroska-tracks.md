---
id: matroska-tracks
title: Tracks, codec IDs and content encodings in Matroska
related: matroska-container, matroska-timestamps, matroska-lacing, opus-pre-skip, audio-specific-config, channel-layouts
references: RFC 9559 sections 5.1.4.1.1 to 5.1.4.1.31; the Matroska codec mapping (draft-ietf-cellar-codec-20, section 3.4); FFmpeg libavformat/matroskadec.c
---

**The track.** Each TrackEntry has a TrackNumber, which blocks use to name it, a TrackType (audio is 2, video 1, subtitles 17), a CodecID and, for audio, an Audio element with SamplingFrequency, Channels, BitDepth and, for SBR, an OutputSamplingFrequency.

**Codec IDs and CodecPrivate.** The codec mapping names each coding and says what its frames and its CodecPrivate hold. A_AAC stores raw AAC data blocks and an AudioSpecificConfig in CodecPrivate. A_MPEG/L1 to L3, A_AC3, A_EAC3 and A_DTS store the frames as they are, with no CodecPrivate. A_VORBIS stores the three Vorbis headers, Xiph-laced, in CodecPrivate. A_OPUS stores the OpusHead there, and its CodecDelay is the pre-skip in nanoseconds ([pre-skip](opus-pre-skip)).

**Content encodings.** A track may alter its frames. Header stripping removes the same first bytes from every frame, which a reader puts back; zlib compresses each frame; encryption hides the frames. A reader applies the encodings from the highest ContentEncodingOrder down. The analyzer undoes header stripping and zlib, and reports encryption without decoding it.

**What you see.** The TrackEntry's fields at their bytes, a summary of the track, the stream the codec parser reports, and findings where the audio settings disagree with the frames, a required CodecPrivate is missing or invalid, or the codec is not read yet.
