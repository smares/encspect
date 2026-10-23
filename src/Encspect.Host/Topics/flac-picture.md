---
id: flac-picture
title: FLAC pictures
related: flac-metadata-blocks, vorbis-comments
references: RFC 9639 (FLAC), section 8
---

**The problem.** Cover art and other images belong with the audio they illustrate, in whatever image format the tagging program chose.

**How it works.** A picture block holds one image. Its fields, each a 32-bit big-endian number or a string with its length in front, are the picture type, the MIME type, a description in UTF-8, the width and height in pixels, the color depth in bits per pixel, the number of colors of an indexed image (0 for one that is not), and the image data with its length. The picture types are those of the attached picture frame of ID3v2: 3 is the front cover, 4 the back cover, 0 any other picture, 1 a 32 by 32 pixel file icon, and so on up to 20. A MIME type of "-->" says the data is a URL, not an image.

**Size.** The data of a picture makes its block large, sometimes the largest thing in front of the audio, and the 24-bit length of a metadata block limits it to just under 16 MiB. A stream may hold several pictures.

**What you see.** Each picture with its type, MIME type, size in pixels and length. The analyzer reads the fields and does not decode the image. Findings name fields that run past the block, and data whose length does not match what the block has left.
