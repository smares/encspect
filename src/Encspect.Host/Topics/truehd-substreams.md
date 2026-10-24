---
id: truehd-substreams
title: TrueHD substreams and presentations
related: truehd-access-units, truehd-rematrixing, truehd-lossless-check, channel-layouts
references: Encspect's TrueHD and MLP format description (docs/design/truehd-format.md), sections "Presentations and substreams" and "Dolby Atmos"
---

**The problem.** A listener may have stereo speakers, a 5.1 system or a larger one, and one stream can serve them without storing every mix in full.

**How it works.** An access unit holds one to four substreams (MLP at most two). In the files Encspect was tested on, substream 0 codes the first channels and outputs the smallest presentation, the two channels of a stereo mix, and the next substream codes further channels and outputs a larger presentation, 5.1, built from its own channels and the ones the lower substreams coded. So a decoder that wants a presentation reads the substreams up to its own, and only the matrices, shifts and channel assignment of that one apply. The channels are coded once.

**Signals.** The major sync gives the channel layouts of the presentations as flags (TrueHD) or an arrangement (MLP) and a byte of substream information. A stream with four substreams and bit 7 of that byte set is signaled as Dolby Atmos. Encspect reads the first three substreams and checks the fourth against the directory only; what it holds is not described, so the analyzer reports that the stream signals Atmos and nothing more.

**What you see.** Each substream's bytes with its blocks, its parity byte and its CRC-8, the presentation each substream outputs in the statistics, and the lossless check that each presentation stores ([lossless check](truehd-lossless-check)). A finding names the substream that cannot be read.
