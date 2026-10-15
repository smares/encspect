---
id: edit-lists-and-priming
title: Edit lists, priming and AAC encoder delay
related: gapless-playback, mp4-sample-tables, itunes-metadata, aac-raw-data-blocks
references: QuickTime File Format, Appendix G (audio priming); ISO/IEC 14496-12 8.6.6, 8.9, 10.1
---

**The problem.** AAC codes each frame of 1024 samples with a transform over 2048, so a decoder needs the frame before to rebuild any stretch of audio. Encoders therefore start with priming samples, silence that is not part of the source, and fill the last frame with padding after it. In Encspect's test files the amount depends on the encoder and its mode: Apple's encoder (afconvert, macOS 15.7.9) states 2112 for AAC-LC and HE-AAC in iTunSMPB, fdkaac 1.0.0 2048 in iTunSMPB (in an edit list only with `-G 2`), ffmpeg's AAC encoder 1024 in the edit list, and FAAC 2.2 1024 in both. Exceptions: AAC-LD, AAC-ELD, HE-AAC v2 and FAAC's HE-AAC state other amounts, FAAC 1.x and 2.0 state none, and so does ffmpeg's fragmented MP4. The analyzer does not use the amount to name an encoder. Played as decoded, a track starts late and ends with silence, which breaks [gapless playback](gapless-playback).

**The edit list.** An MP4 track's edit list (`elst`) says which part of the media to present. For AAC, its media time is the encoder delay, the first sample to play, and its segment duration is the source's length, so a player trims both ends. The segment duration counts in the movie's timescale and the media time in the media's, which for audio is usually the sample rate.

**Roll groups.** A sample group of type `roll` (`sgpd` with `sbgp`) gives a roll distance of -1: to decode any access unit correctly, a decoder must also decode the one before it. Apple uses its presence to mark that the edit list states the delay explicitly; without it, QuickTime assumes 2112. exhale's USAC files carry a pre-roll group (`prol`) instead.

**iTunSMPB.** Some writers, such as iTunes, fdkaac and FAAC 2.2, write a freeform metadata item, iTunSMPB, instead of or besides the edit list, with the delay, the padding and the source's length as hexadecimal words. Players that read it trim the same samples without the edit list.

**What you see.** A Gapless node under each audio track with the delay, samples and padding from the edit list and from iTunSMPB, the roll distance, and a warning where the two sources disagree.
