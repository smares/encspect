---
id: dts-transient-mode
title: DTS transient modes
related: dts-subframes, block-switching, filter-bank, dts-core-frames
references: ETSI TS 102 114 V1.6.1 (2019-08), 5.5.1 (TMODE, SCALES), 5.6, Annex D.5.2
---

**The problem.** A subband sample carries the quantization noise of the whole subframe, up to 32 samples of a subband, about 1,024 output samples. A sudden attack in a quiet passage then has noise in front of it, which the ear hears as pre-echo. See [block switching](block-switching) for the same problem in transform codecs.

**How it works.** DTS keeps its filter bank at 32 subbands and changes the scale factors instead. When a subframe has more than one subsubframe (SSC above 0), the encoder may send for each coded subband a transient mode TMODE, from 0 to 3, in one of four Huffman books chosen by THUFF. TMODE 0 means no transient. Above 0 it names the subsubframe in which the transient starts, and the subband has two scale factors: the first for the subsubframes before it and the second from it on. The quiet part before the attack then keeps a small scale factor and a small step.

Subbands with no bits allocated and the high-frequency subbands that are vector quantized never have a TMODE, and a subframe of a single subsubframe has none.

**What you see.** The analyzer counts the subframes in which any subband of any channel has TMODE above 0 as "Subframes With Block Switching", beside the subframes read, and graphs it for each subframe. The name follows the other formats; DTS does not switch the transform.
