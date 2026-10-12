---
id: opus-channel-mapping
title: Opus channel mapping and multistream
related: opus-toc, opus-modes, mid-side-stereo, channel-layouts
references: RFC 7845, sections 3 and 5.1.1; RFC 8486, sections 3 and 5
---

**The problem.** An Opus stream codes one or two channels. Surround and ambisonic audio need more, so Ogg Opus packs several Opus streams into each packet and says how their decoded channels become output channels.

**Streams and coupling.** The identification header gives the stream count N and the coupled count M: the first M streams decode as stereo, two channels each, the rest as mono. A channel mapping table then gives, for each output channel, the decoded channel it takes, or 255 for silence.

**Families.** The mapping family says what the output channels mean:

- Family 0: one stream, mono or stereo, with no table.
- Family 1: one to eight channels in the Vorbis order, from mono and stereo to 5.1 (front left, front center, front right, rear left, rear right, LFE) and 7.1.
- Family 2 (RFC 8486): ambisonics, (1 + n)² channels for an order n of 0 to 14, each an ambisonic component in ambisonic channel number (ACN) order, optionally followed by a non-diegetic stereo pair (left, right). Mixed-order ambisonics marks the channels it leaves out as silent.
- Family 3 (RFC 8486): the same channel counts, but a demixing matrix of 16-bit coefficients takes the place of the table and mixes the decoded channels into the output ones.
- Family 255: any number of channels without defined meaning, which general-purpose players should not play.

The other families are reserved, 240 to 254 for experiments, and decoders that do not know one should not decode its packets.

**What you see.** The header's family, stream and coupled counts and the table, each output channel with the stream it comes from and its speaker or ACN, the Channel Layout of families 0 and 1, and for family 3 the matrix's size.
