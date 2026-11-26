#!/usr/bin/env python3
"""Checks the table of the frames each ID3v2 version defines (src/Encspect.Tags.Id3/Id3v2FrameTexts.cs) against
the pinned id3.org documents (id3_specs.py): every frame ID and its section number in ID3v2.2, ID3v2.3.0 and the
ID3v2.4.0 frames document, in both directions, and the replacements the descriptions and the finding name
against section 4 (Deprecated ID3v2 frames) of the ID3v2.4.0 changes document, so that id3v2.frame-not-in-version and the descriptions say only
what the documents say.

    python3 scripts/fixtures/check_id3_frames.py

The summary table of the ID3v2.4.0 frames document gives TXXX as 4.2.2; its section is 4.2.6, which is what the
table holds. Exit status 1 when the table and the documents differ.
"""

import html
import pathlib
import re
import sys
import tempfile

import downloads
import id3_specs

SOURCE = pathlib.Path(__file__).resolve().parents[2] / "src/Encspect.Tags.Id3/Id3v2FrameTexts.cs"

# AENC is missing from the ID3v2.3.0 summary table but has its section: 4.20, "Audio encryption".
FIXES = {"v23": {"AENC": "4.20"}, "v24": {"TXXX": "4.2.6"}}


def text_of(path):
    raw = path.read_text(encoding="latin-1")
    return html.unescape(re.sub(r"<[^>]+>", "", raw))


def table(text, id_length, bracket):
    ids = {}
    pattern = r"^\s*(4\.\d+(?:\.\d+)?)\s+([A-Z0-9]{%d})\s%s" % (id_length, r"+\[" if bracket else "")
    for line in text.splitlines():
        match = re.match(pattern.replace(r"\s+\[", r"\s+\["), line)
        if match:
            ids[match.group(2)] = match.group(1)
    return ids


def changes(text):
    """The frames section 4 of the changes document deprecates: the frames that replace each, or () for none."""
    start = text.index("Deprecated ID3v2 frames", text.index("Tag structure changes", text.index("Table of contents") + 200))
    end = text.index("New frames", start)
    found = {}
    for block in re.split(r"\n\s{2,4}(?=[A-Z0-9]{4} - )", text[start:end])[1:]:
        frame = block[:4]
        if "replaced by" in block:
            after = re.sub(r"\[F:[0-9.]+\]", "", block.split("replaced by", 1)[1])
            found[frame] = tuple(sorted(set(re.findall(r"\b[A-Z][A-Z0-9]{3}\b", after))))
        else:
            found[frame] = ()
    return found


def main():
    with tempfile.TemporaryDirectory() as directory:
        target = pathlib.Path(directory)
        for name, digest, addresses in id3_specs.SPECS:
            downloads.fetch(addresses, digest, target / name)
        v22 = table(text_of(target / "id3v2-00.html"), 3, False)
        v23 = table(text_of(target / "id3v2.3.0.html"), 4, True)
        v24 = table(text_of(target / "id3v2.4.0-frames.html"), 4, False)
        deprecated = changes(text_of(target / "id3v2.4.0-changes.html"))
    v23.update(FIXES["v23"])
    v24.update(FIXES["v24"])

    source = SOURCE.read_text()
    got23, got24, got22 = {}, {}, {}
    for match in re.finditer(r'\["([A-Z0-9]{4})"\] = new\(".*?", (null|"[0-9.]+"), (null|"[0-9.]+")', source):
        frame, a, b = match.groups()
        if a != "null":
            got23[frame] = a.strip('"')
        if b != "null":
            got24[frame] = b.strip('"')
    for match in re.finditer(r'\["([A-Z0-9]{3})"\] = \("([A-Z0-9]{4})", "([0-9.]+)"\)', source):
        got22[match.group(1)] = match.group(3)
    got22["CRM"] = "4.20"

    problems = []
    for name, spec, got in (("ID3v2.2", v22, got22), ("ID3v2.3.0", v23, got23), ("ID3v2.4.0", v24, got24)):
        for frame in sorted(set(spec) | set(got)):
            if spec.get(frame) != got.get(frame):
                problems.append(f"{name} {frame}: documents {spec.get(frame)}, table {got.get(frame)}")
    # What the table says ID3v2.4 did with each frame only ID3v2.3 defines: "replaced it with A and B" or "deprecated it".
    said = {}
    for match in re.finditer(r'\["([A-Z0-9]{4})"\] = new\(".*?", "[0-9.]+", null, "([^"]+)"\)', source):
        frame, gone = match.groups()
        said[frame] = tuple(sorted(re.findall(r"\b[A-Z][A-Z0-9]{3}\b", gone.replace("replaced it with", "")))) if gone.startswith("replaced") else ()
        if gone not in ("deprecated it",) and not gone.startswith("replaced it with "):
            problems.append(f"{frame}: wording \"{gone}\" is not the changes document's")
    for frame in sorted(set(deprecated) | set(said)):
        if deprecated.get(frame) != said.get(frame):
            problems.append(f"ID3v2.4.0 changes {frame}: document {deprecated.get(frame)}, table {said.get(frame)}")
    only23 = set(v23) - set(v24)
    if only23 != set(deprecated):
        problems.append(f"frames only ID3v2.3.0 defines {sorted(only23)} differ from the changes document's deprecated {sorted(deprecated)}")
    for problem in problems:
        print(problem)
    print(f"{len(v22)} ID3v2.2, {len(v23)} ID3v2.3.0 and {len(v24)} ID3v2.4.0 frames checked, {len(problems)} problems.")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
