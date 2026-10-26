---
id: replaygain
title: ReplayGain
related: lame-tag, global-gain, ape-tag, musepack-sv8-packets, measurements
references: ReplayGain specification (David Robinson, 2001); LAME tag specification (Gabriel Bouvigne)
---

**The problem.** Tracks from different albums and eras are mastered at very different loudness. Played one after another, some seem much louder than others, and listeners keep reaching for the volume.

**How it works.** ReplayGain measures how loud a track sounds, with a model of the ear's sensitivity, and stores the change in level that would play it at a common reference loudness. The original proposal used a reference of 89 dB. Two gains are usual: the track gain, which evens out every track, and the album gain, which applies one change to a whole album, so the loud and quiet tracks within it keep their difference. The peak, the largest sample value, lets a player avoid clipping when it raises the level. The file's audio stays unchanged; players apply the gain on playback.

**In MP3 files.** LAME computes ReplayGain while encoding and stores a track ("radio") gain and the peak in its [LAME tag](lame-tag); LAME before 3.95 used a reference of 83 dB, so its gains read 6 dB lower than later versions'. Taggers more often store ReplayGain in APE items (REPLAYGAIN_TRACK_GAIN and so on) or in ID3v2 TXXX frames. MP3Gain instead changes the frames' [global gain](global-gain), which changes the audio data itself, in steps of 1.5 dB.

**What you see.** Radio and Audiophile Replay Gain and Peak in the LAME tag, and ReplayGain items in tags. These are stated values, written by whoever tagged the file and not checked here; a loudness or peak the analyzer computed from the decoded audio would be a measured figure, another thing, see [measurements](measurements).

**In Musepack.** SV7 stores the track and album gain in its header as an adjustment in hundredths of a dB and the peaks as sample values; SV8 stores them in its ReplayGain packet as loudness and peak in dB, which mpcgain computes after encoding, and mpcdec prints 64.82 dB minus the loudness as the gain ([SV8 packets](musepack-sv8-packets)).
