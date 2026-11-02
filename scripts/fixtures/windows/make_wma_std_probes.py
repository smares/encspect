#!/usr/bin/env python3
"""Probes of standard Windows Media Audio files (versions 1 and 2) for Microsoft's decoder, one question at a time.

    python3 make_wma_std_probes.py <analyzer> <output folder> <folder>[:<glob>[,<glob>...]]...

Companion of make_wma_std_variants.py (same arguments, same skipping of files that do not read as expected). That script cuts a
file after a frame; this one keeps a media object whole and moves the bits inside it, so that what the decoder is given after
the last frame it can read is varied on purpose. Every probe ends the stream after one media object (the later ones are removed
and the counts of the headers fixed). The bit positions come from the analyzer; what the decoder outputs is read from the
decoder under test, a black box. The kinds, for a media object j:

  la<t>    the last frame that ends in j (the last frame the object's frame count lets it finish) is moved so that t bits of
           the original bits that follow it are left up to the end of j; the "Last Frame Bit Offset" is raised by the shift
           and the gap after the frame that began in the object before is zero bits
  tf<t>    the same for the frame whose first bits are the last ones of j when that frame is wholly inside j (its bits come
           from the analyzer's frame list); a negative t cuts the end of the frame off (the bits fall out of the object)
  hf<t>    the same for the last media object of the file and the frame that begins in its last bits (found by reading the file
           with one more media object appended, which the probe does not keep)
  sh<s>    the frames that begin after the frame the object before began moved later by s bits (the same shift as la, but the
           bits that fall out of the end of the object are the last bits of the original tail, not the ones after a chosen frame)
  tz<t>    nothing moved; the tail (the bits after the last frame that ends in j) keeps its first t bits and is zero from there
  tr<t>    the same with pseudo-random bits instead of zero bits
  mz<t>, mo<t>
           for objects whose last frame ends at most 80 bits before the end: the first t bits of the tail kept, the rest of
           the tail zero bits (mz) or one bits (mo); nothing else moved
  from<j>  the first j media objects removed (j chosen so that the object j starts with a frame and its frame count includes
           one frame whose bits lie in the removed object, which the count loses); the stream is not cut at the end
  id       the object as it is (a control: the stream ends after j)
  cnt<d>   the frame count in the header of j raised or lowered by d, nothing moved
  eos-...  the end of the stream marked differently (see end_markers): data packet count of the headers raised or lowered by one,
           the end of the Data Object before the last packet, an empty packet after the last, the play duration or the preroll
           of the File Properties Object set

Writes <output folder>/<name>.wma and manifest.json with, for each file, what was done: kind, the media object, the frame
indices and bit counts involved, and t. Nothing is guessed.
"""

import fnmatch
import json
import pathlib
import re
import struct
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE))
import make_wma_pro_variants as pro  # noqa: E402
import make_wma_std_variants as std_variants  # noqa: E402
from make_wma_std_variants import StdFile, Unreadable, walk, analyze  # noqa: E402

LOOKAHEADS = (0, 6, 10, 12, 13, 14, 17, 20, 26, 32, 48, 64)
TAIL_FRAME_AFTER = (-1, 0, 8, 16, 24, 32, 64)
HIDDEN_AFTER = (-1, 0, 8, 16, 32, 64)
COUNT_DELTAS = (-1, 1)
SHIFTS = (1, 8, 64, 512)
TAIL_KEEPS = (0, 16)
MARGIN_KEEPS = (0, 4, 8, 12, 16, 20, 24, 32)
MARGIN_MAX = 80
OBJECTS_PER_BASE = 2


def layout(report):
    """Per media object: its bytes, header, the bit offset field, the frames that end in it with their object-relative bits and
    where the bits no frame ends in begin; and the bits of every frame in file order."""
    nodes = []
    walk(report["root"], nodes)
    stream = next(n for n in nodes if n["name"] in std_variants.WMA_NAMES)
    kids = {c["name"]: c for c in stream["children"]}
    objects = []
    for sf in kids["Superframes"]["children"]:
        parts = {c["name"]: c for c in sf["children"]}
        offset_field = parts.get("Last Frame Bit Offset")
        origin = sf["offset"] * 8
        entry = {"offset": sf["offset"], "length": sf["length"], "bits": sf["length"] * 8, "offsetBits": offset_field["bitLength"] if offset_field else None,
                 "lfbo": offset_field["value"] if offset_field else None, "count": parts["Frames in Superframe"]["value"], "ends": [], "tail": None}
        cursor = None
        for child in sf["children"]:
            if child["name"] in ("Frame End", "Frame Data"):
                start = child["offset"] * 8 + child.get("bitOffset", 0) - origin
                end = start + child.get("bitLength", child["length"] * 8)
                entry["ends"].append((child["name"], start, end))
                cursor = end
        entry["tail"] = cursor
        objects.append(entry)
    frame_bits = []
    for frame in kids["Frames"]["children"]:
        match = re.match(r"\d+ blocks?, (\d+) bits", str(frame["value"]))
        frame_bits.append(int(match.group(1)) if match else None)
    done = []
    total = 0
    for sf in kids["Superframes"]["children"]:
        first = str(sf["value"]).split()[0]
        total += int(first) if first.isdigit() else 0
        done.append(total)
    return objects, frame_bits, done


def to_bits(data):
    return "".join(f"{b:08b}" for b in data)


def from_bits(text):
    return bytes(int(text[i:i + 8], 2) for i in range(0, len(text), 8))


def edit_object(raw, j, build):
    """The file with media object j rebuilt by build(bit string) -> bit string of the same length, the later media objects
    removed and the counts of the headers fixed."""
    file = StdFile(raw)
    if j + 1 < len(file.objects) and file.object_packet[j + 1] == file.object_packet[j]:
        raise Unreadable("the media object shares its packet with the next one")
    at, size = file.objects[j][4][0]
    new = build(to_bits(bytes(file.data[at:at + size])))
    if len(new) != size * 8:
        raise Unreadable("the rebuilt object changed its length")
    file.data[at:at + size] = from_bits(new)
    packet = file.packets[file.object_packet[j]]
    end = packet[0] + packet[1]
    del file.data[end:]
    pro.fix_counts(file, file.object_packet[j] + 1, end)
    return bytes(file.data)


def shifted(bits, offset_bits, lfbo, frames_end, t):
    """bits with everything from the first new frame on moved later so that the frame ending at frames_end has t bits after it.
    The moved part is cut off at the end of the object; the gap is zero bits and the bit offset field is raised by it."""
    total = len(bits)
    shift = total - t - frames_end
    if shift < 0:
        raise Unreadable("no room to shift")
    start = 8 + offset_bits + lfbo
    if lfbo + shift >= 1 << offset_bits:
        raise Unreadable("the shift does not fit the bit offset field")
    header = bits[:8] + format(lfbo + shift, f"0{offset_bits}b") + bits[8 + offset_bits:start]
    return header + "0" * shift + bits[start:total - shift]


def tail_replaced(bits, tail, keep, mode):
    """bits with everything after the first keep bits of the tail replaced by zero bits or by pseudo-random bits."""
    total = len(bits)
    if tail + keep > total:
        raise Unreadable("no room for the kept bits")
    if mode == "z":
        filler = "0" * (total - tail - keep)
    else:
        state = 0x2545F491 ^ tail
        out = []
        for _ in range(total - tail - keep):
            state = (state * 1103515245 + 12345) & 0x7FFFFFFF
            out.append("1" if state & 0x10000 else "0")
        filler = "".join(out)
    return bits[:tail + keep] + filler


def tail_set(bits, tail, keep, bit):
    total = len(bits)
    if tail + keep > total:
        raise Unreadable("no room for the kept bits")
    return bits[:tail + keep] + bit * (total - tail - keep)


def drop_first(raw, j, new_count):
    file = StdFile(raw)
    first = file.object_packet[j]
    if file.object_packet[j - 1] == first:
        raise Unreadable("the media object shares its packet with the one before")
    start = file.packets[0][0]
    at = file.packets[first][0]
    del file.data[start:at]
    pro.fix_counts(file, len(file.packets) - first, len(file.data))
    moved = StdFile(bytes(file.data))
    obj_at, size = moved.objects[0][4][0]
    bits = to_bits(bytes(moved.data[obj_at:obj_at + size]))
    moved.data[obj_at:obj_at + size] = from_bits(with_count(bits, new_count))
    return bytes(moved.data)


def with_count(bits, count):
    return bits[:4] + format(count, "04b") + bits[8:]


def appended_copy(raw, width):
    """raw with one more media object after the last: a copy of the last packet whose media object has the next index, the frame
    count 1 and the bit offset field 0, so that a reader finishes the frame that began in the last object's last bits."""
    file = StdFile(raw)
    last = file.packets[-1]
    packet = bytearray(file.data[last[0]:last[0] + last[1]])
    obj = file.objects[-1]
    number, size = obj[1], obj[2]
    pattern = bytes([file.stream["number"], number, 0, 0, 0, 0, 8]) + struct.pack("<I", size)
    at = bytes(packet).find(pattern)
    if at < 0:
        raise Unreadable("the last packet's payload header is not as expected")
    packet[at + 1] = (number + 1) & 255
    place = obj[4][0][0] - last[0]
    head = to_bits(bytes(packet[place:place + size]))
    index = (int(head[:4], 2) + 1) & 15
    head = format(index, "04b") + format(1, "04b") + "0" * width + head[8 + width:]
    packet[place:place + size] = from_bits(head)
    end = last[0] + last[1]
    file.data[end:end] = packet
    pro.fix_counts(file, len(file.packets) + 1, end + len(packet))
    return bytes(file.data)


def hidden_frame_bits(analyzer, raw, width, scratch):
    """(bits of the frame that begins in the last bits of the last media object, frames found) from reading the file with one
    more media object appended."""
    path = pathlib.Path(str(scratch) + ".appended")
    path.write_bytes(appended_copy(raw, width))
    try:
        _, frame_bits, _ = layout(analyze(analyzer, path))
    finally:
        path.unlink(missing_ok=True)
    return (frame_bits[-1] if frame_bits else None), len(frame_bits)


def end_markers(raw):
    """(name, bytes) of the end-of-stream variants of the file as it is."""
    file = StdFile(raw)
    out = []
    count = len(file.packets)
    for delta in (1, -1):
        copy = StdFile(raw)
        pro.fix_counts(copy, count + delta, len(copy.data))
        out.append((f"eos-packets{'+' if delta > 0 else '-'}1", bytes(copy.data)))
    copy = StdFile(raw)
    struct.pack_into("<Q", copy.data, copy.data_offset + 16, len(copy.data) - copy.data_offset - copy.packets[-1][1])
    out.append(("eos-dataend-before-last-packet", bytes(copy.data)))
    last = file.packets[-1]
    blank = bytearray(file.data[last[0]:last[0] + last[1]])
    header_len = blank.find(bytes([file.stream["number"]]))
    for i in range(header_len, len(blank)):
        blank[i] = 0
    copy = StdFile(raw)
    copy.data += blank
    pro.fix_counts(copy, count + 1, len(copy.data))
    out.append(("eos-empty-packet", bytes(copy.data)))
    for field, ms in (("play", 0), ("play", 12000), ("preroll", 0)):
        copy = StdFile(raw)
        std_variants.set_header(copy, field, ms)
        out.append((f"eos-{field}-{ms}", bytes(copy.data)))
    return out


def spread(items, n):
    if len(items) <= n:
        return list(items)
    return [items[(len(items) * (2 * i + 1)) // (2 * n)] for i in range(n)]


def probes_for(analyzer, path, raw, stem):
    report = analyze(analyzer, path)
    objects, frame_bits, done = layout(report)
    structure = std_variants.structure(report)
    if not structure["reservoir"]:
        raise Unreadable("no bit reservoir")
    out = {}
    last = len(objects) - 1
    in_progress = [False] + [o["tail"] is not None and (o["bits"] - o["tail"]) >= 8 for o in objects[:-1]]
    la, tf = [], []
    for j, o in enumerate(objects[:-1]):
        if o["offsetBits"] is None or o["tail"] is None or not in_progress[j]:
            continue
        room = o["bits"] - o["tail"]
        if room >= 70 and any(e[0] == "Frame Data" for e in o["ends"]):
            la.append(j)
        k = done[j]
        if k < len(frame_bits) and frame_bits[k] is not None and frame_bits[k] <= room:
            tf.append(j)
    for j in spread(la, OBJECTS_PER_BASE):
        o = objects[j]
        for t in LOOKAHEADS:
            if o["bits"] - o["tail"] < t:
                continue
            name = f"probe-{stem}-j{j}-la{t}"
            try:
                data = edit_object(raw, j, lambda b, o=o, t=t: shifted(b, o["offsetBits"], o["lfbo"], o["tail"], t))
            except Unreadable:
                continue
            out[name] = (data, {"kind": f"la{t}", "object": j, "frame": done[j] - 1, "tailBits": o["bits"] - o["tail"], "t": t})
    for j in spread(la, OBJECTS_PER_BASE):
        o = objects[j]
        room = o["bits"] - o["tail"]
        for shift in SHIFTS:
            if shift >= room:
                continue
            name = f"probe-{stem}-j{j}-sh{shift}"
            try:
                data = edit_object(raw, j, lambda b, o=o, shift=shift: shifted(b, o["offsetBits"], o["lfbo"], o["tail"], room - shift))
            except Unreadable:
                continue
            out[name] = (data, {"kind": f"sh{shift}", "object": j, "frame": done[j] - 1, "tailBits": room, "shift": shift})
        for keep in TAIL_KEEPS:
            for mode in ("z", "r"):
                name = f"probe-{stem}-j{j}-t{mode}{keep}"
                try:
                    data = edit_object(raw, j, lambda b, o=o, keep=keep, mode=mode: tail_replaced(b, o["tail"], keep, mode))
                except Unreadable:
                    continue
                out[name] = (data, {"kind": f"t{mode}{keep}", "object": j, "frame": done[j] - 1, "tailBits": room, "keep": keep})
        out[f"probe-{stem}-j{j}-id"] = (edit_object(raw, j, lambda b: b), {"kind": "id", "object": j, "frame": done[j] - 1})
    small = [j for j, o in enumerate(objects[:-1]) if o["tail"] is not None and 0 < o["bits"] - o["tail"] <= MARGIN_MAX and o["offsetBits"] is not None]
    for j in spread(small, 6):
        o = objects[j]
        room = o["bits"] - o["tail"]
        for keep in MARGIN_KEEPS:
            if keep > room:
                continue
            for letter, bit in (("z", "0"), ("o", "1")):
                name = f"probe-{stem}-j{j}-m{letter}{keep}"
                try:
                    data = edit_object(raw, j, lambda b, o=o, keep=keep, bit=bit: tail_set(b, o["tail"], keep, bit))
                except Unreadable:
                    continue
                out[name] = (data, {"kind": f"m{letter}{keep}", "object": j, "frame": done[j] - 1, "tailBits": room, "keep": keep})
        out[f"probe-{stem}-j{j}-mid"] = (edit_object(raw, j, lambda b: b), {"kind": "mid", "object": j, "frame": done[j] - 1, "tailBits": room})
    starts = [j for j in range(2, len(objects) - 1) if objects[j]["lfbo"] == 0 and in_progress[j] and objects[j]["count"] >= 2]
    for j in spread(starts, 2):
        try:
            data = drop_first(raw, j, objects[j]["count"] - 1)
        except Unreadable:
            continue
        out[f"probe-{stem}-from{j}"] = (data, {"kind": f"from{j}", "object": j, "frame": done[j - 1], "count": objects[j]["count"]})
    for j in tf:
        o = objects[j]
        k = done[j]
        end = o["tail"] + frame_bits[k]
        for t in TAIL_FRAME_AFTER:
            name = f"probe-{stem}-j{j}-tf{'m' if t < 0 else ''}{abs(t)}"
            try:
                data = edit_object(raw, j, lambda b, o=o, t=t, end=end: shifted(b, o["offsetBits"], o["lfbo"], end, t))
            except Unreadable:
                continue
            out[name] = (data, {"kind": f"tf{t}", "object": j, "frame": k, "frameBits": frame_bits[k], "t": t})
    final = objects[last]
    if final["offsetBits"] is not None and in_progress[last]:
        hidden, frames_with_hidden = hidden_frame_bits(analyzer, raw, final["offsetBits"], path)
        if hidden is not None and frames_with_hidden == len(frame_bits) + 1:
            end = final["tail"] + hidden
            for t in HIDDEN_AFTER:
                name = f"probe-{stem}-final-hf{'m' if t < 0 else ''}{abs(t)}"
                try:
                    data = edit_object(raw, last, lambda b, t=t, end=end: shifted(b, final["offsetBits"], final["lfbo"], end, t))
                except Unreadable:
                    continue
                out[name] = (data, {"kind": f"hf{t}", "object": last, "frame": len(frame_bits), "frameBits": hidden, "t": t})
    for j in spread([j for j, o in enumerate(objects) if o["count"] is not None], OBJECTS_PER_BASE + 1) + [last]:
        for d in COUNT_DELTAS:
            count = objects[j]["count"] + d
            if not 1 <= count <= 15:
                continue
            name = f"probe-{stem}-j{j}-cnt{'m' if d < 0 else 'p'}{abs(d)}"
            try:
                data = edit_object(raw, j, lambda b, count=count: with_count(b, count))
            except Unreadable:
                continue
            out[name] = (data, {"kind": f"cnt{d}", "object": j, "count": objects[j]["count"], "newCount": count})
    return out, {"frames": len(frame_bits), "objects": len(objects), "frameLength": structure["frameLength"]}


def main():
    if len(sys.argv) < 4:
        print(__doc__)
        return 2
    analyzer, output = sys.argv[1], pathlib.Path(sys.argv[2])
    output.mkdir(parents=True, exist_ok=True)
    manifest, skipped, eos_done = {}, {}, 0
    for spec in sys.argv[3:]:
        for path in std_variants.candidates(spec):
            raw = path.read_bytes()
            stem = path.stem
            try:
                StdFile(raw)
                found, info = probes_for(analyzer, path, raw, stem)
            except (Unreadable, KeyError, StopIteration, struct.error, ValueError, AssertionError) as error:
                skipped[path.name] = f"{type(error).__name__}: {error}"[:200]
                continue
            for name, (data, meta) in found.items():
                (output / f"{name}.wma").write_bytes(data)
                manifest[f"{name}.wma"] = dict(meta, base=path.name, **info)
            if eos_done < 2:
                for name, data in end_markers(raw):
                    full = f"probe-{stem}-{name}.wma"
                    (output / full).write_bytes(data)
                    manifest[full] = {"kind": name, "base": path.name, **info}
                eos_done += 1
    (output / "manifest.json").write_text(json.dumps({"files": manifest, "skipped": skipped}, indent=1) + "\n", encoding="utf-8")
    print(f"{len(manifest)} probes, skipped: {json.dumps(skipped)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
