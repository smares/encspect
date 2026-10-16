#!/usr/bin/env python3
"""Writes src/Encspect.Formats.Matroska/Elements.cs from the pinned specifications, or checks that the file
holds exactly what they give:

    python3 scripts/fixtures/matroska_tables.py [--check] [directory]

The table lists every element of RFC 9559 (section 5 and the deprecated elements of appendix A) and of
RFC 8794 (the EBML header, CRC-32 and Void, sections 11.2 and 11.3): its ID, name, type, path, whether its
size may be unknown, and a description, which is the first sentence of the specification's own definition
with the section it comes from. The parser reads the table to name, type and describe the elements it
meets, and to know where an element belongs. RFC 9559's registry (section 27.1) lists the IDs; the script
fails unless the registry and the definitions agree.

The elements of the Matroska element registry (ebml_matroska.xml, pinned by commit) that RFC 9559 does not list are added,
described as from "the Matroska element registry", and the registry gives every element its default value, which RFC 8794 section 6.1
has an empty element take (the EBML header elements' defaults are RFC 8794's own).

The specifications are fetched by matroska_specs.py into the directory (default matroska-specs).
"""

import pathlib
import re
import sys
import xml.etree.ElementTree as ET

import matroska_specs

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUTPUT = ROOT / "src" / "Encspect.Formats.Matroska" / "Elements.cs"

TYPES = {
    "master": "Master", "uinteger": "Unsigned", "integer": "Signed", "float": "Float", "string": "String", "utf-8": "Utf8",
    "date": "Date", "binary": "Binary",
    "Master Element": "Master", "Unsigned Integer": "Unsigned", "Signed Integer": "Signed", "Float": "Float", "String": "String",
    "UTF-8": "Utf8", "Date": "Date", "Binary": "Binary",
}

PAGE = re.compile(r"^(Lhomme, et al\.|RFC 9559\s+Matroska|Bradshaw, et al\.|RFC 8794\s+EBML)")
FIELD = re.compile(r"^   ([a-zA-Z][a-zA-Z /]*):\s+(.*)$")
HEADER_9559 = re.compile(r"^(\d+(?:\.\d+)+)\.\s+(\w[\w -]*) Element$")
HEADER_APPENDIX = re.compile(r"^(A\.\d+)\.\s+(\w[\w -]*) Element$")
HEADER_8794 = re.compile(r"^(11\.[23]\.\d+)\.\s+([\w-]+) Element$")


def clean(lines):
    return [line.rstrip() for line in lines if not PAGE.match(line)]


def blocks(lines, header):
    """(section, name, field lines) of every element definition: the lines up to the next section heading."""
    result = []
    current = None
    for line in lines:
        if re.match(r"^(\d+\.|A\.\d+\.|Appendix)", line) and not line.startswith(" "):
            if current:
                result.append(current)
            m = header.match(line)
            current = (m.group(1), m.group(2), []) if m else None
        elif current is not None:
            current[2].append(line)
    if current:
        result.append(current)
    return result


def fields(body):
    """The labeled fields of a definition; a wrapped line continues the field before it. A path wraps in the middle of a
    word, so its lines join without a space, and so do other lines that end in a hyphen."""
    out = {}
    label = None
    for line in body:
        m = FIELD.match(line)
        if m and not line.startswith("      "):
            label = m.group(1).strip()
            out[label] = m.group(2)
        elif label and line.startswith("      ") and line.strip():
            glue = "" if label == "path" or out[label].endswith("-") else " "
            out[label] += glue + line.strip()
        else:
            label = None
    return out


def sentence(text):
    text = re.sub(r"\s+", " ", text.strip())
    text = re.sub(r"\[(RFC) ?(\d+)\]", r"\1 \2", text)
    text = re.sub(r"\bSection (\d+(?:\.\d+)*) of \[MatroskaCodec\]", r"section \1 of the Matroska codec mapping", text)
    text = text.replace("[MatroskaCodec]", "the Matroska codec mapping").replace("[MatroskaTags]", "the Matroska tag specification")
    text = re.sub(r"\[([A-Za-z0-9]+)\]", r"\1", text)
    text = re.sub(r"\bSection (\d)", r"RFC 9559 section \1", text)
    # The description cites its own section at the end; pointers to other sections add nothing to it.
    text = re.sub(r" ?\([Ss]ee [^)]*\)", "", text)
    text = re.sub(r"[;,]? see (?:RFC 9559 )?section \d+(?:\.\d+)*(?: of the Matroska codec mapping)?(?: on [A-Z][A-Za-z' ]*[a-z])?"
                  r"(?: for (?:a complete list of values|more (?:details|information)))?", "", text)
    m = re.search(r"(?<!e\.g)(?<!i\.e)\.(?=\s+[A-Z(])", text)
    return (text[:m.start()] if m else text).rstrip(".").replace(" ,", ",")


def last(path):
    return path.rsplit("\\", 1)[1].lstrip("+")


def cs(text):
    return '"' + text.replace("\\", "\\\\").replace('"', '\\"') + '"'


def elements(rfc9559, rfc8794):
    table = {}
    lines = clean(rfc9559.splitlines())
    start = next(i for i, l in enumerate(lines) if l.startswith("5.  Matroska Schema"))
    end = next(i for i, l in enumerate(lines) if l.startswith("6.  Matroska Element Ordering"))
    for section, name, body in blocks(lines[start:end], HEADER_9559):
        f = fields(body)
        ident, kind = [part.strip() for part in f["id / type"].split("/")][:2] if "id / type" in f else (f["id / type / default"].split("/")[0].strip(), f["id / type / default"].split("/")[1].strip())
        table[int(ident, 16)] = dict(id=int(ident, 16), name=last(f["path"]), type=TYPES[kind], path=f["path"], section=f"RFC 9559, section {section}",
                                    text=f["definition"], unknown=f.get("unknownsizeallowed") == "True", deprecated=False)
    start = next(i for i, l in enumerate(lines) if l.startswith("Appendix A.  Historic Deprecated Elements"))
    end = next(i for i, l in enumerate(lines) if l.startswith("Authors' Addresses"))
    for section, name, body in blocks(lines[start:end], HEADER_APPENDIX):
        f = fields(body)
        kind, ident = [part.strip() for part in f["type / id"].split("/")]
        table[int(ident, 16)] = dict(id=int(ident, 16), name=last(f["path"]), type=TYPES[kind], path=f["path"], section=f"RFC 9559, appendix {section}",
                                    text=f["documentation"], unknown=False, deprecated=True)

    lines = clean(rfc8794.splitlines())
    start = next(i for i, l in enumerate(lines) if l.startswith("11.2.  EBML Header Elements"))
    end = next(i for i, l in enumerate(lines) if l.startswith("12.  Considerations for Reading EBML Data"))
    for section, name, body in blocks(lines[start:end], HEADER_8794):
        f = fields(body)
        ident = int(f["id"], 16)
        table[ident] = dict(id=ident, name=f["name"], type=TYPES[f["type"]], path=f["path"].strip('"'), section=f"RFC 8794, section {section}",
                           text=f["description"], unknown=False, deprecated=False, default=default_of(f.get("default")))
    return table


def default_of(text):
    """A default value as the table writes it: a hexadecimal float as a decimal number."""
    if text is None:
        return None
    if re.fullmatch(r"-?0x[0-9a-fA-F.]+p[+-]\d+", text):
        return repr(float.fromhex(text))
    return text


def xml_registry(xml_text):
    """The elements of ebml_matroska.xml by ID: name, type, path, default, unknown-size, deprecated and the first definition."""
    out = {}
    ns = "{urn:ietf:rfc:8794}"
    for e in ET.fromstring(xml_text).iter(ns + "element"):
        ident = int(e.get("id"), 16)
        docs = [d for d in e.findall(ns + "documentation") if d.get("purpose") == "definition" and d.get("lang", "en") == "en"]
        out[ident] = dict(id=ident, name=last(e.get("path")), type=TYPES[e.get("type")], path=e.get("path"), default=default_of(e.get("default")),
                          unknown=e.get("unknownsizeallowed") == "1", deprecated=e.get("maxver") == "0",
                          text=re.sub(r"\s+", " ", "".join(docs[0].itertext())).strip() if docs else "")
    return out


def registry(rfc9559):
    """The (ID, name) pairs of RFC 9559's section 27.1 table."""
    lines = clean(rfc9559.splitlines())
    start = next(i for i, l in enumerate(lines) if l.startswith("27.1.  Matroska Element IDs Registry"))
    end = next(i for i, l in enumerate(lines) if l.startswith("27.2.  Matroska Compression Algorithms Registry"))
    return {int(m.group(1), 16): m.group(2) for m in (re.match(r"^\s*\|\s*(0x[0-9A-Fa-f]+)\|\s*(\S+)", l) for l in lines[start:end]) if m}


def registry_text(text):
    """The registry's markdown definition as plain text: references [@!RFC5646] and [@!RFC9559, Section 12] as "RFC 5646" and "RFC 9559
    section 12", no emphasis or code marks, no pointers to its own anchors."""
    # The registry has the typo "`BlockAddIDValue`that" (no space) in TagBlockAddIDValue's definition.
    text = text.replace("`BlockAddIDValue`that", "`BlockAddIDValue` that")
    text = re.sub(r"\[@!?RFC(\d+), Section (\d+(?:\.\d+)*)\]", r"RFC \1 section \2", text)
    text = re.sub(r"\[@!?RFC(\d+)\]", r"RFC \1", text)
    text = re.sub(r"; see RFC \d+ section [\d.]+ on language codes", "", text)
    return re.sub(r"\*\*|`|\(#[a-z0-9-]+\)", "", text)


def merge_registry(table, xml):
    """Adds the registry's elements the RFC does not list, and every element's registry default."""
    added = []
    for ident, e in xml.items():
        if ident not in table:
            table[ident] = dict(e, section="the Matroska element registry", text=registry_text(e["text"]), unknown=False)
            added.append(ident)
        elif table[ident].get("default") is None:
            table[ident]["default"] = e["default"]
        elif e["default"] is not None and table[ident]["default"] != e["default"]:
            raise SystemExit(f"{table[ident]['name']}: RFC 8794 gives the default {table[ident]['default']}, the registry {e['default']}")
    return added


def render(table):
    rows = []
    for ident in sorted(table):
        e = table[ident]
        text = sentence(e["text"])
        description = f"{text} ({e['section']})." if not e["deprecated"] else f"{text} ({e['section']}); no longer used."
        rows.append(f"        new(0x{ident:X}, {cs(e['name'])}, ElementType.{e['type']}, {cs(e['path'])}, {cs(description)}"
                    f"{', isUnknownSizeAllowed: true' if e['unknown'] else ''}{', isDeprecated: true' if e['deprecated'] else ''}"
                    f"{', defaultValue: ' + cs(e['default']) if e.get('default') is not None else ''}),")
    return (
        "// Generated by scripts/fixtures/matroska_tables.py from RFC 9559, RFC 8794 and the Matroska element registry; do not edit. The workflow checks it.\n"
        "namespace Encspect.Formats.Matroska;\n\n"
        "internal static partial class Schema\n{\n"
        "    private static readonly ElementDefinition[] Definitions =\n    [\n" + "\n".join(rows) + "\n    ];\n}\n")


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    directory = matroska_specs.fetch(args[0] if args else "matroska-specs")
    rfc9559 = (directory / "rfc9559.txt").read_text(encoding="utf-8")
    rfc8794 = (directory / "rfc8794.txt").read_text(encoding="utf-8")
    table = elements(rfc9559, rfc8794)
    listed = registry(rfc9559)
    xml = xml_registry((directory / "ebml_matroska.xml").read_text(encoding="utf-8"))
    missing = {i: n for i, n in listed.items() if i not in table and n != "Reserved"}
    extra = [i for i in table if i not in listed and i < 0x1A45DFA3 or (i not in listed and i > 0x1A45DFA3)]
    renamed = [(i, n, table[i]["name"]) for i, n in listed.items() if i in table and table[i]["name"] != n]
    ebml_header = {0x1A45DFA3, 0x4286, 0x42F7, 0x42F2, 0x42F3, 0x4282, 0x4287, 0x4285, 0x4281, 0x4283, 0x4284, 0xBF, 0xEC}
    extra = [i for i in extra if i not in ebml_header]
    if missing or extra or renamed:
        raise SystemExit(f"The registry and the definitions disagree: only in the registry {missing}; only defined {extra}; names {renamed}")
    added = merge_registry(table, xml)
    unexpected = [hex(i) for i in added if i in listed]
    if unexpected:
        raise SystemExit(f"Listed by RFC 9559's registry but not defined: {unexpected}")
    text = render(table)
    if "--check" in sys.argv:
        if not OUTPUT.exists() or OUTPUT.read_text(encoding="utf-8") != text:
            raise SystemExit(f"{OUTPUT.relative_to(ROOT)} differs from what the specifications give; run scripts/fixtures/matroska_tables.py.")
        print(f"{OUTPUT.relative_to(ROOT)}: {len(table)} elements, as the specifications give them")
    else:
        OUTPUT.write_text(text, encoding="utf-8", newline="\n")
        print(f"Wrote {len(table)} elements to {OUTPUT.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
