---
id: tns
title: Temporal noise shaping (TNS)
related: aac-windows-and-blocks, block-switching, aac-raw-data-blocks
references: ISO/IEC 14496-3 4.4.2, 4.6.9; ffmpeg libavcodec/aacdec_template.c (decode_tns)
---

**The problem.** A long AAC window spans 2048 samples. When a sound changes sharply inside it, such as a struck note or speech, the quantization noise still spreads evenly over the whole window, so it can be heard before the attack and in the quieter parts around it: pre-echo. Switching to short windows avoids that, but costs frequency resolution and bits, and does not help a signal that changes within every window.

**How it works.** Temporal noise shaping applies a prediction filter across frequency, to the spectral values of a window rather than to the samples over time. A filter across frequency acts on the time envelope the way a filter over time acts on the spectrum, so the decoder's inverse filter shapes the quantization noise in time to follow the loudness of the signal within the window: loud where the signal is loud, quiet where it is quiet. The encoder sends the filters in tns_data: up to three per long window and one per short window, each with the bands it spans, its order, its direction (upward or downward in frequency) and its quantized coefficients. AAC LC allows an order of up to 12 in long windows and 7 in short ones; AAC Main up to 20.

**What you see.** A channel's HasTnsData flag and TNS Data with each window's filters, their order, length and direction. The statistics count the channel streams that use TNS per window sequence: encoders turn it on mostly for transients, so its share shows how often they found them.

**Where it is specified.** ISO/IEC 14496-3 4.6.9 describes the tool; its syntax is part of each individual channel stream.
