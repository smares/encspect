---
id: mp3-surround
title: MP3 Surround
related: ancillary-data, mp3pro, mpeg-multichannel, encoder-detection
references: mp3guessenc (MP3 Surround detection); no public specification
---

**The problem.** Surround sound needs five or six channels, but players, portable devices and existing files expect stereo.

**How it works.** Fraunhofer's MP3 Surround, introduced around 2004, mixes the 5.1 channels down to stereo and codes that downmix as an ordinary MPEG-1 Layer III stream at 44.1 or 48 kHz. Alongside, it extracts a compact description of the spatial image, how the channels relate in level and correlation band by band, and stores it in the [ancillary data](ancillary-data) of every frame. An MP3 Surround decoder uses that description to spread the downmix back over 5.1 channels; any other decoder plays the stereo downmix. The same approach, spatial audio coding, was later standardized as MPEG Surround.

**What you see.** No specification of the extension data is public. The analyzer recognizes MP3 Surround as mp3guessenc does, by a signature in the first bytes of every frame's ancillary data, shows an Enhancement node with the bytes and their data rate, and names Fraunhofer's MP3 Surround encoder with medium confidence. The [MPEG-2 multichannel extension](mpeg-multichannel) of Layer II follows the same idea of a playable stereo base, in a standardized way.
