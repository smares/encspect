---
id: gapless-playback
title: Encoder delay, padding and gapless playback
related: lame-tag, granules-and-frames, filter-bank, musepack-sv7-stream
references: LAME tag specification (Gabriel Bouvigne); Fraunhofer Original File Length
---

**The problem.** An MP3 decoder does not output exactly the samples the encoder was given. The encoder adds samples before the audio, because its transforms and filterbank need context before the first real sample: the encoder delay. It also fills the last frame with samples after the audio, since frames hold a fixed number of samples: the padding. The decoder adds its own delay on top. The result is a short silence at the start and end of every track, which is audible where tracks of a live album or a continuous mix should run into each other.

**How it is solved.** If the encoder stores how many samples it added, a player can drop them and play exactly the original audio: gapless playback. LAME stores the encoder delay and padding in its [LAME tag](lame-tag); with LAME, the delay is typically 576 samples, and decoders add 529 of their own. Fraunhofer's encoders store an Original File Length block in the ancillary data of the first frame, with the delay and the original length in samples.

**Sequences.** LAME's --nogap option encodes a series of files so that each continues where the previous one ended, and marks the files as continuing and continued in the tag.

**What you see.** Encoder Delay and Encoder Padding in the LAME tag, and in the statistics the sample count and duration that remain once they are dropped, which is what ffmpeg and mpg123 output with gapless trimming.

**In Musepack.** Decoders drop the synthesis filter's 481 samples at the start. SV7's header gives the samples in the last frame (true gapless), SV8's stream header the total samples and any silence to skip at the start, so decoders output exactly the input ([SV7](musepack-sv7-stream)).
