---
id: asf-scripts-and-markers
title: ASF script commands and markers
related: asf-container, asf-index
references: Advanced Systems Format (ASF) Specification, revision 01.20.03, sections 3.6 and 3.7
---

**The problem.** A Windows Media presentation can ask its player to do things at set times, such as opening a web page beside a lecture's slides or starting another file, and can name places in the file a viewer may jump to, such as chapters.

**How it works.** The Script Command Object holds a list of command types, text such as URL or FILENAME that the file's author may choose freely, and a list of commands. Each command has a presentation time in milliseconds, the number of its type in the list and a parameter, whose meaning the type decides. What a player does with a type is up to the player. The Marker Object holds a name and a list of markers. Each marker has a byte offset into the Data Object, a presentation time in 100-nanosecond units, a send time in milliseconds, flags that are reserved and a description. Every text is UTF-16; the specification recommends a terminating NUL but does not require one.

**What you see.** Each command and marker is a node with its fields at their bytes. A command's value names its type and parameter, and a marker's value is its description. Findings appear where a command names a type past the list, where a count is larger than the data holds, where a marker's entry length does not match its fields, or where a name is not whole UTF-16 characters. Only the first 1,000 entries of a list are shown.
