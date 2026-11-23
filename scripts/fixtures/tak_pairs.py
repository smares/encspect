#!/usr/bin/env python3
"""Which frames of a TAK file (codec 4) pair channels in their channel table (docs/design/tak-format.md, section 6.2).

    python3 scripts/fixtures/tak_pairs.py FILE.tak ...

The channel table is the first field of a codec 4 frame's coded data (after the frame header, for frames of 16 samples or more), so the walk of tak_model.py, which
finds the frames the way the analyzer's walk does, is enough to read it: one bit (0: every channel alone), else E = u(4) + 1 entries, each a channel u(4), a bit
`paired` and, if set, a type u(2) (0 add, 1 mid/side, 2 scaled, 3 filter) and a partner u(4). The analyzer's decoder reads the same syntax and gives the samples of every committed
file back exactly (crosscheck_tak_pcm.py), which is what ties this reading to it. Prints one line per file; `summarize(path)` gives the counts that
import_windows_tak_fixtures.py uses to keep only the files that pair.
"""

import json
import pathlib
import sys

import tak_model

TYPES = ("add", "mid/side", "scaled", "filter")


def read_table(data, start, channels):
    """The entries [(channel, type or None, partner or None)] of the channel table at the byte start, or None where the table is a plain channel order."""
    bits = tak_model.Bits(data[start:start + 4 + 16 * 2])
    bits.overflow = False
    if not bits.get(1):
        return None
    entries = []
    for _ in range(bits.get(4) + 1):
        channel = bits.get(4)
        if bits.get(1):
            kind = bits.get(2)
            entries.append((channel, kind, bits.get(4)))
        else:
            entries.append((channel, None, None))
    return entries


def summarize(path):
    """{frames, codec, channels, tableFrames, pairedFrames, pairedEntries, types: {name: count}} of a file."""
    data = pathlib.Path(path).read_bytes()
    objects, audio = tak_model.walk(data)
    result = {"frames": 0, "codec": None, "channels": None, "tableFrames": 0, "pairedFrames": 0, "pairedEntries": 0, "types": {name: 0 for name in TYPES}}
    if audio is None:
        return result
    first = tak_model.frame_header(data, audio)
    info = first.get("stream_info") if first else None
    if info is None:
        return result
    result["codec"], result["channels"] = info["codec"], info["channels"]
    frames = tak_model.walk_frames(data, audio, len(data))
    result["frames"] = len(frames)
    if info["codec"] != 4:
        return result
    for frame in frames:
        samples = frame["samples"] if frame["last"] else info["frame_length"]
        if frame["cut"] or samples < 16:
            continue
        entries = read_table(data, frame["offset"] + frame["header_length"], info["channels"])
        if entries is None:
            continue
        result["tableFrames"] += 1
        paired = [e for e in entries if e[1] is not None]
        if paired:
            result["pairedFrames"] += 1
            result["pairedEntries"] += len(paired)
            for _, kind, _ in paired:
                result["types"][TYPES[kind]] += 1
    return result


def main(argv):
    if not argv:
        raise SystemExit(__doc__)
    for name in argv:
        print(name, json.dumps(summarize(name)))


if __name__ == "__main__":
    main(sys.argv[1:])
