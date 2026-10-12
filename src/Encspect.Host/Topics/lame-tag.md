---
id: lame-tag
title: The LAME tag
related: xing-header, gapless-playback, replaygain, encoder-detection, lowpass, ath, noise-shaping
references: LAME tag specification (Gabriel Bouvigne), Mp3 Info Tag revision 1; LAME libmp3lame/VbrTag.c
---

**What it is.** LAME extends the [Xing or Info header](xing-header) of the first frame with 36 bytes of its own, specified as the Mp3 Info Tag. It records how the file was made and what a player needs to play it exactly:

- the encoder's version string, nine bytes such as "LAME3.100";
- the bitrate mode ([VBR method](bitrate-modes)), the [lowpass](lowpass), the [ATH type](ath), the [noise shaping](noise-shaping) mode, the stereo mode, the preset and the source sample rate;
- the [encoder delay and padding](gapless-playback), for gapless playback;
- [ReplayGain](replaygain) values and the peak sample;
- an MP3 Gain field for later volume changes;
- the music length and a CRC of the audio frames, and a CRC of the tag itself.

**Two CRCs.** The tag CRC covers the frame up to the CRC field and shows whether the tag is intact; where it fails, readers such as ffmpeg ignore the tag's encoder string and ReplayGain. The music CRC covers the audio frames and shows whether the audio changed after encoding, as when MP3Gain edits the gains.

**Other writers.** ffmpeg writes a LAME tag into the MP3s it creates, with "Lavc" or "Lavf" as its encoder string, and the Helix encoder writes one starting "LAMEH".

**What you see.** All fields under LAME Tag, the tag's encoder string as evidence for the [encoder](encoder-detection), and findings where a CRC fails.
