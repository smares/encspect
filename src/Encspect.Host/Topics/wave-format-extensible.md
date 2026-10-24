---
id: wave-format-extensible
title: WAVEFORMATEXTENSIBLE and channel masks
related: waveformatex, channel-layouts, pcm-samples
references: Microsoft, WAVEFORMATEX and WAVEFORMATEXTENSIBLE documentation; Microsoft, mmreg.h and ksmedia.h; EBU Tech 3306-2009, section 3 and annex A.2
---

**The problem.** The plain WAVEFORMATEX cannot say which speaker a channel feeds, beyond left and right for two channels, nor that a 24-bit sample sits in a 32-bit container. Both matter once files have more than two channels or more than 16 bits.

**How it works.** A format tag of 0xFFFE announces WAVEFORMATEXTENSIBLE, whose cbSize is at least 22. Three fields follow the usual ones. The valid bits per sample are the bits that hold audio, the rest of the container being zero. The channel mask has a bit for each speaker position: front left is bit 0, front right bit 1, front center bit 2, the low-frequency effects channel bit 3, then the back pair, the front pair of center, the back center, the side pair and the top positions up to bit 17. The channels in the data are in the order of the set bits. The sub-format is a GUID: AC-3, E-AC-3 and MPEG-2 audio have GUIDs of their own, and otherwise, when its last twelve bytes are Microsoft's base value, its first 32 bits are the format tag of the coding, so the format tag 1 becomes the GUID that starts with 00000001.

Bits 18 to 30 are reserved by Microsoft; the EBU assigns bits 23 to 30 to a stereo down mix, control data and non-PCM bitstreams, which the 18 positions do not cover. When the mask names more speakers than the file has channels, the extra, most significant bits are ignored; when it names fewer, the remaining channels feed no particular speaker.

**What you see.** The valid bits, the mask in hex with the speakers it names, the sub-format with the coding it stands for, and the stream's channel layout in the order of the channels. An information finding appears where the mask names a different number of speakers than the file has channels.
