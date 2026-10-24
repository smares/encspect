"""Builds liba52 0.7.4 from its original source with a trace of what its audio block parser decides, so the analyzer's reading of each AC-3
audio block can be checked against an independent decoder block by block (crosscheck_ac3_fixtures.py).

The release is the original source of Ubuntu's liba52-0.7.4-dev, whose liba52 a52_blocks.c is linked against; its a52_state_t declares chincpl,
cplendmant, rematflg and endmant[5], but blksw is a local array of a52_block (parse.c), so the trace patch adds one printf at the end of
a52_block, after the block is decoded:

    @@BLK <acmod> <blksw of each full bandwidth channel> | <chincpl> <rematflg> | <endmant of each full bandwidth channel> <cplendmant>

chincpl is the bit mask of the coupled channels (0 when coupling is not in use), rematflg the rematrixing flags of the last rematstr (2/0 only)
and endmant[ch] the end bin of a channel (cplstrtmant for a coupled one, chbwcod * 3 + 73 otherwise); cplendmant is the end bin of the coupling
channel, 37 + 12 * (cplendf + 3). The build lives in .cache/tools (git-ignored); liba52 is under the GPL and is built for checking this project's
fixtures, not redistributed.

    python3 scripts/fixtures/a52_trace_tool.py file.ac3
"""

import hashlib
import pathlib
import re
import subprocess
import sys
import tarfile

import downloads

VERSION = "0.7.4"
ARCHIVE = f"a52dec_{VERSION}.orig.tar.gz"
SHA256 = "a21d724ab3b3933330194353687df82c475b5dfb997513eef4c25de6c865ec33"
ADDRESSES = [
    f"https://deb.debian.org/debian/pool/main/a/a52dec/{ARCHIVE}",
    f"http://archive.ubuntu.com/ubuntu/pool/universe/a/a52dec/{ARCHIVE}",
]
ROOT = pathlib.Path(__file__).resolve().parents[2] / ".cache" / "tools" / "a52-trace"
HERE = pathlib.Path(__file__).resolve().parent

PATCH = (
    "    return 0;\n}\n\nvoid a52_free (a52_state_t * state)",
    "    {\n        int j;\n        printf (\"@@BLK %d\", state->acmod);\n"
    "        for (j = 0; j < nfchans_tbl[state->acmod]; j++)\n            printf (\" %d\", blksw[j]);\n"
    "        printf (\" | %d %d |\", state->chincpl, state->rematflg);\n"
    "        for (j = 0; j < nfchans_tbl[state->acmod]; j++)\n            printf (\" %d\", state->endmant[j]);\n"
    "        printf (\" %d\\n\", state->cplendmant);\n    }\n    return 0;\n}\n\nvoid a52_free (a52_state_t * state)",
)


def build():
    """Returns the trace program, building it once (again when the patch or the main program changed)."""
    binary = ROOT / "a52_trace"
    stamp = ROOT / "stamp.sha256"
    main = (HERE / "a52_trace.c").read_bytes()
    wanted = hashlib.sha256(repr((SHA256, PATCH)).encode() + main).hexdigest()
    if binary.exists() and stamp.exists() and stamp.read_text() == wanted:
        return binary
    ROOT.mkdir(parents=True, exist_ok=True)
    archive = ROOT / ARCHIVE
    downloads.fetch(ADDRESSES, SHA256, archive)
    source = ROOT / f"a52dec-{VERSION}"
    if source.exists():
        subprocess.run(["rm", "-rf", str(source)], check=True)
    with tarfile.open(archive) as tar:
        tar.extractall(ROOT, filter="data")
    parse = source / "liba52" / "parse.c"
    text = parse.read_text(encoding="latin-1")
    if text.count(PATCH[0]) != 1:
        raise SystemExit("liba52's parse.c no longer ends a52_block where the trace patch expects")
    text = text.replace(PATCH[0], PATCH[1])
    if "#include <stdio.h>" not in text:
        text = "#include <stdio.h>\n" + text
    parse.write_text(text, encoding="latin-1")
    # The build needs the configuration header only for the C99 integer types and an aligned allocation; the portable C code is used.
    config = ROOT / "config"
    config.mkdir(exist_ok=True)
    (config / "config.h").write_text("#define HAVE_INTTYPES_H 1\n#define HAVE_MEMALIGN 1\n")
    sources = [str(p) for p in sorted((source / "liba52").glob("*.c"))]
    subprocess.run(["cc", "-O2", f"-I{config}", f"-I{source / 'include'}", f"-I{source / 'liba52'}", "-o", str(binary), str(HERE / "a52_trace.c"), *sources, "-lm"],
                   check=True, capture_output=True)
    stamp.write_text(wanted)
    return binary


def trace(path):
    """The frames liba52 reads in a raw file: per frame its offset, length, whether it rejected the frame, and per decoded block a dict of
    acmod, blksw, chincpl, rematflg, endmant and cplendmant; "error" is True when a block could not be decoded."""
    result = subprocess.run([str(build()), str(path)], capture_output=True, text=True, check=True)
    frames = []
    for line in result.stdout.splitlines():
        if line.startswith("@@FRAME"):
            fields = line.split()
            frames.append({"offset": int(fields[1]), "length": int(fields[2]), "rejected": "rejected" in fields, "error": False, "blocks": []})
        elif line.startswith("@@ERROR"):
            frames[-1]["error"] = True
        elif line.startswith("@@BLK"):
            left, coupling, ends = line[len("@@BLK "):].split("|")
            acmod, *blksw = [int(v) for v in left.split()]
            chincpl, rematflg = (int(v) for v in coupling.split())
            *endmant, cplendmant = [int(v) for v in ends.split()]
            frames[-1]["blocks"].append({"acmod": acmod, "blksw": blksw, "chincpl": chincpl, "rematflg": rematflg, "endmant": endmant, "cplendmant": cplendmant})
    return frames


if __name__ == "__main__":
    for frame in trace(sys.argv[1]):
        print(frame)
