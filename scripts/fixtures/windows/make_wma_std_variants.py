#!/usr/bin/env python3
"""Variants of standard Windows Media Audio files (versions 1 and 2), to ask Microsoft's decoder one question at a time.

    python3 make_wma_std_variants.py <analyzer> <output folder> <folder>[:<glob>[,<glob>...]]...

Each folder is searched for files with a standard stream (format tag 0x0160 or 0x0161, not encrypted, one stream); the globs
(default *.wma and *.asf) pick files. The analyzer is the command line program, used only to find where the frames and the
superframes of a file lie; what a decoder outputs is read from the decoder under test, a black box. The kinds:

  base                 the file as it is
  cut-<k>              the stream ends after frame k (counted from 0). With the bit reservoir, the frame count in the
                       header of the superframe holding frame k is lowered to end the decoded frames there, and the later
                       superframes are removed; what follows frame k in that superframe stays, as the start of a frame
                       no media object finishes. Without the bit reservoir (one frame in each media object) the later
                       media objects are removed, where they lie in packets of their own.
  cut-<k>-play-<ms>, cut-<k>-preroll-<ms>
                       the same, with the ASF File Properties Object's play duration or preroll set, in milliseconds

The cuts are the last frame of every superframe, two frames for each block size that the blocks after a frame announce
(the "next block size" of the frame's last block), and the first three frames. The headers of the Data Object and the File
Properties Object that count or measure the file are corrected; the play duration stays the base file's unless a kind sets
it. Writes <output folder>/<name>.wma and manifest.json with, for each file, what was done. Nothing is guessed: a file whose
structure does not read as expected is reported in manifest.json and skipped.
"""

import fnmatch
import json
import pathlib
import struct
import subprocess
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
sys.path.insert(0, str(HERE))
import asf_model  # noqa: E402
import make_wma_pro_variants as pro  # noqa: E402

WMA_NAMES = ("Windows Media Audio 1", "Windows Media Audio 2")
HEADER_VARIANTS = (("play", 0), ("play", 12000), ("preroll", 0))
HEADER_BASES = 2


class Unreadable(Exception):
    pass


def walk(node, out):
    out.append(node)
    for child in node.get("children") or []:
        walk(child, out)


def value_of(node, name):
    return next((c["value"] for c in node.get("children") or [] if c["name"] == name), None)


def analyze(analyzer, path):
    result = subprocess.run([analyzer, "--json", str(path)], capture_output=True, text=True)
    try:
        return json.loads(result.stdout)[0]
    except (ValueError, IndexError):
        raise Unreadable("the analyzer gave no report")


def structure(report):
    """The frame length, whether the stream has a bit reservoir, and for each superframe the frames it decodes, the count its
    header states and whether a frame was in progress when it began; for each frame the block size its last block announces."""
    nodes = []
    walk(report["root"], nodes)
    streams = [n for n in nodes if n["name"] in WMA_NAMES]
    if len(streams) != 1:
        raise Unreadable("not one standard stream")
    stream = streams[0]
    inner = []
    walk(stream, inner)
    stats = next((n for n in inner if n["name"] == "Statistics" and n.get("children")), None)
    if stats is None:
        raise Unreadable("no statistics")
    values = {c["name"]: c["value"] for c in stats["children"]}
    f = int(values["Frame Length"])
    reservoir = values["Uses Bit Reservoir"] is True
    if "Invalid Frames" in values:
        raise Unreadable("invalid frames")
    superframes = []
    in_progress = False
    holder = next((n for n in inner if n["name"] == "Superframes" and n.get("children")), None)
    if holder is None:
        raise Unreadable("no superframes listed")
    for sf in holder["children"]:
        kids = sf.get("children") or []
        first = str(sf["value"]).split()[0]
        d = int(first) if first.isdigit() else 0
        count = value_of(sf, "Frames in Superframe")
        left = value_of(sf, "Bit Reservoir")
        superframes.append({"offset": sf["offset"], "length": sf["length"], "decoded": d, "count": count, "inProgress": in_progress})
        in_progress = isinstance(left, (int, float)) and left > 0
    frames_node = next((n for n in inner if n["name"] == "Frames" and n.get("children")), None)
    nexts = []
    for frame in frames_node["children"] if frames_node else []:
        blocks = []
        walk(frame, blocks)
        sizes = [b["value"] for b in blocks if b["name"] == "Next Block Size"]
        nexts.append(int(sizes[-1]) if sizes else f)
    if sum(s["decoded"] for s in superframes) != len(nexts):
        raise Unreadable("the superframes decode a different number of frames than the frames list")
    return {"frameLength": f, "reservoir": reservoir, "superframes": superframes, "nexts": nexts}


class StdFile:
    """A standard file as bytes, with the header positions fix_counts of make_wma_pro_variants.py needs."""

    def __init__(self, data):
        parsed = asf_model.parse(bytes(data))
        streams = parsed["streams"]
        if len(streams) != 1 or streams[0].get("tag") not in (0x160, 0x161) or streams[0].get("encrypted"):
            raise Unreadable("not one unencrypted standard stream")
        self.data = bytearray(data)
        self.stream = streams[0]
        self.rate = self.stream["rate"]
        self.objects = [m for m in parsed["media_objects"] if m[0] == self.stream["number"]]
        self.packets = parsed["packets"]
        header_size = struct.unpack_from("<Q", self.data, 16)[0]
        self.data_offset = header_size
        if bytes(self.data[header_size:header_size + 16]) != pro.DATA_OBJECT:
            raise Unreadable("no Data Object after the header")
        self.file_properties = self.find_object(pro.FILE_PROPERTIES, 30, header_size)
        align = self.stream["block_align"]
        self.object_packet = []
        for obj in self.objects:
            if len(obj[4]) != 1 or obj[4][0][1] != align:
                raise Unreadable("a media object that is not one contiguous block alignment")
            at = obj[4][0][0]
            index = next((i for i, p in enumerate(self.packets) if p[0] <= at < p[0] + p[1]), None)
            if index is None:
                raise Unreadable("a media object outside the packets")
            self.object_packet.append(index)

    find_object = pro.ProFile.find_object


def frame_position(structure_, k):
    """(superframe index, frames of it kept) for frame k."""
    before = 0
    for j, sf in enumerate(structure_["superframes"]):
        if k < before + sf["decoded"]:
            return j, k - before + 1
        before += sf["decoded"]
    raise Unreadable("no such frame")


def cut(raw, structure_, k):
    std = StdFile(raw)
    j, kept = frame_position(structure_, k)
    sf = structure_["superframes"][j]
    if len(std.objects) != len(structure_["superframes"]):
        raise Unreadable("the superframes are not the media objects")
    if j + 1 < len(std.objects) and std.object_packet[j + 1] == std.object_packet[j]:
        raise Unreadable("the superframe shares its packet with the next one")
    if structure_["reservoir"] and kept < sf["decoded"]:
        new_count = kept if sf["inProgress"] else kept + 1
        at = sf["offset"]
        if std.data[at] & 15 != sf["count"]:
            raise Unreadable("the frame count is not where expected")
        std.data[at] = (std.data[at] & 0xF0) | new_count
    elif not structure_["reservoir"] and kept != sf["decoded"]:
        raise Unreadable("a superframe of several frames without a bit reservoir")
    packet = std.packets[std.object_packet[j]]
    end = packet[0] + packet[1]
    del std.data[end:]
    pro.fix_counts(std, std.object_packet[j] + 1, end)
    return std


def set_header(std, field, ms):
    offset = std.file_properties + (64 if field == "play" else 80)
    struct.pack_into("<Q", std.data, offset, ms * 10000 if field == "play" else ms)


def choose_cuts(structure_):
    frames = len(structure_["nexts"])
    ks = set(range(min(3, frames)))
    before = 0
    for sf in structure_["superframes"]:
        before += sf["decoded"]
        if sf["decoded"]:
            ks.add(before - 1)
    by_size = {}
    for k in range(3, frames - 1):
        by_size.setdefault(structure_["nexts"][k], []).append(k)
    for size, found in sorted(by_size.items()):
        for pick in (found[len(found) // 3], found[(2 * len(found)) // 3]):
            ks.add(pick)
    return sorted(ks)


def header_ks(structure_):
    """The cuts that also get header variants: the last frame of the fourth superframe (or the last one there is) and frame 2."""
    ends, before = [], 0
    for sf in structure_["superframes"]:
        before += sf["decoded"]
        ends.append(before - 1)
    return {ends[min(3, len(ends) - 1)], 2}


def candidates(spec):
    folder, _, pattern = spec.partition(":")
    folder = pathlib.Path(folder)
    if not folder.is_dir():
        return []
    patterns = pattern.split(",") if pattern else ["*.wma", "*.asf"]
    seen = []
    for path in sorted(folder.iterdir()):
        if path.is_file() and any(fnmatch.fnmatch(path.name, p) for p in patterns):
            seen.append(path)
    return seen


def main():
    if len(sys.argv) < 4:
        print(__doc__)
        return 2
    analyzer, output = sys.argv[1], pathlib.Path(sys.argv[2])
    output.mkdir(parents=True, exist_ok=True)
    manifest, skipped = {}, {}
    bases = 0
    header_bases = 0
    for spec in sys.argv[3:]:
        for path in candidates(spec):
            raw = path.read_bytes()
            try:
                StdFile(raw)
                structure_ = structure(analyze(analyzer, path))
            except (Unreadable, KeyError, StopIteration, struct.error) as error:
                skipped[path.name] = f"base: {error}"
                continue
            stem = path.stem
            name = f"std-{stem}-base.wma"
            (output / name).write_bytes(raw)
            manifest[name] = {"base": path.name, "kind": "base", "frames": len(structure_["nexts"]), "frameLength": structure_["frameLength"],
                              "reservoir": structure_["reservoir"], "superframes": len(structure_["superframes"]), "next": structure_["nexts"][-1] if structure_["nexts"] else None}
            made = 0
            headered = False
            for k in choose_cuts(structure_):
                try:
                    std = cut(raw, structure_, k)
                except Unreadable as error:
                    skipped[stem] = f"{skipped.get(stem, '')}{'' if stem not in skipped else '; '}cut-{k}: {error}"[:300]
                    continue
                name = f"std-{stem}-cut-{k}.wma"
                (output / name).write_bytes(bytes(std.data))
                j, kept = frame_position(structure_, k)
                manifest[name] = {"base": path.name, "kind": f"cut-{k}", "frames": k + 1, "frameLength": structure_["frameLength"],
                                  "superframe": j, "kept": kept, "next": structure_["nexts"][k]}
                made += 1
                if header_bases < HEADER_BASES and structure_["reservoir"] and k in header_ks(structure_):
                    headered = True
                    for field, ms in HEADER_VARIANTS:
                        copy = StdFile(bytes(std.data))
                        set_header(copy, field, ms)
                        header_name = f"std-{stem}-cut-{k}-{field}-{ms}.wma"
                        (output / header_name).write_bytes(bytes(copy.data))
                        manifest[header_name] = dict(manifest[name], kind=f"cut-{k}-{field}-{ms}")
            bases += 1 if made else 0
            header_bases += 1 if headered else 0
    (output / "manifest.json").write_text(json.dumps({"files": manifest, "skipped": skipped}, indent=1) + "\n", encoding="utf-8")
    print(f"{len(manifest)} files from {bases} bases, skipped: {json.dumps(skipped)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
