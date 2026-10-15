---
id: asf-metadata
title: ASF metadata objects
related: asf-container, id3v2
references: Advanced Systems Format (ASF) Specification, revision 01.20.03, sections 3.10, 3.11, 3.13, 4.7, 4.8 and 7.4; FFmpeg libavformat/asf.c, asfdec_f.c
---

**The problem.** A file needs a title and an artist, but also an album, a track number, cover art and the name of the program that wrote it. A fixed set of fields would soon be too small.

**How it works.** ASF has five objects for it, which the specification says to choose from in this order. The Content Description Object holds the title, author, copyright, description and rating as UTF-16 text. The Content Branding Object holds a banner image and two URLs. The Extended Content Description Object holds attributes of any name with a typed value: text, bytes, a boolean, or a 16-, 32- or 64-bit number. The Metadata Object does the same and adds a stream number. The Metadata Library Object adds a language, allows an attribute more than once, allows values of more than 65,535 bytes, and has a GUID type. Windows Media's own attribute names begin with "WM/", such as WM/AlbumTitle, WM/TrackNumber and WM/Picture.

**What you see.** Each of these objects is a tag in the tree, and every attribute a node named after it, with its typed value. An attached picture is taken apart into its type, MIME type, description and data. Attributes such as WM/EncodingSettings, WMFSDKVersion or WM/ToolName name the software that wrote the file; the report treats them as claims of low confidence in [writer detection](writer-detection), apart from the encoder.
