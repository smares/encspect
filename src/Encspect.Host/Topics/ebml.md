---
id: ebml
title: EBML, the format under Matroska and WebM
related: matroska-container, matroska-lacing, crc
references: RFC 8794 sections 3 to 7, 8 and 11.3; RFC 9559 section 4
---

**The problem.** A binary format needs a way to nest data, to skip what a reader does not know, and to grow without breaking old readers. EBML, the Extensible Binary Meta Language, is a binary relative of XML that gives Matroska and WebM exactly that.

**Elements.** Everything is an element: an Element ID, an Element Data Size and the data. Both numbers are variable-size integers: the leading zero bits of the first byte give the length in bytes (a first byte of 1xxxxxxx is one byte, 01xxxxxx two bytes, and so on up to eight), and the first 1 bit is a marker that the value does not use. IDs keep the marker, so 0x1A45DFA3 marks an EBML Header. A size with all value bits set means the size is unknown, which only the Segment and the Cluster may use: a reader then finds the end of the element at the next element that cannot belong inside it.

**Types and the header.** An element holds an unsigned or signed integer, a float, text, a date (nanoseconds since 2001-01-01), binary data or other elements. A document starts with an EBML Header whose DocType names the schema: `matroska` or `webm`. EBMLMaxIDLength and EBMLMaxSizeLength limit the lengths of IDs and sizes. Two elements may appear anywhere: a Void element, whose content is discarded and which reserves space for later edits, and a CRC-32 element, which checks the data of its parent that follows it.

**What you see.** Every element as a node with its ID's name, its value and its bytes, a computed CRC-32 beside each stored one, and findings where a size runs past its parent, an ID or size is longer than allowed, or a CRC does not match.
