"""Builds faad2 2.11.1, the version Ubuntu 24.04 ships, with a trace of what its parser reads in every raw data block, so the
analyzer's reading of each access unit can be checked against an independent decoder frame by frame (crosscheck_mp4_fixtures.py).

faad2's own ANALYSIS build no longer compiles, so a few fprintf calls are added to libfaad/syntax.c, ms.c and sbr_syntax.c at build time (faad_trace.patch),
after the fields they print are read; each line starts with "@@" on standard error:

- "@@AU <bit>": a raw data block starts; "@@EL <id> <bit>": an element starts; "@@END <bit>": the block ends, after its byte alignment;
- "@@CPE <common_window> <ms_mask_present> <ms_used flags>": a channel pair's shared information;
- "@@ICS <window_sequence> <window_shape> <max_sfb> <groups> <global_gain> <pulse> <tns> <gain control> <predictor> <TNS filters>
  <codebooks>": a channel stream, read to the end of its spectral data;
- "@@MS <ms_mask_present> <bands>": faad2's own decision of ms_decode, after the channel pair's two "@@ICS" lines: for each band of each window group, 1
  where it applies mid/side decoding (ms_used set or mask 2, and neither intensity stereo in the second channel nor noise substitution in the first);
- "@@EXT <type>": an extension payload; "@@SBRHDR <start> <stop> <xover>": an SBR header; "@@SBREXT <id>": an SBR extension.

Bit positions count from the start of what the decoder was handed, so only differences between them are compared. The build lives in
.cache/tools (git-ignored); faad2 is under the GPL, and so is the patch (faad_trace.patch, a modification of faad2 headed with faad2's copyright and license); both are test
tools for checking this project's fixtures, in neither build and not redistributed.

    python3 scripts/fixtures/faad_trace_tool.py file.m4a
"""

import hashlib
import pathlib
import subprocess
import sys

REPOSITORY = "https://github.com/knik0/faad2"
COMMIT = "216f00e8ddba6f2c64caf481a04f1ddd78b93e78"  # tag 2.11.1
ROOT = pathlib.Path(__file__).resolve().parents[2] / ".cache" / "tools" / "faad2-trace"

PATCH = pathlib.Path(__file__).with_name("faad_trace.patch")


def build():
    """Returns the faad binary with the trace, building it once."""
    binary = ROOT / "build" / "faad"
    stamp = ROOT / "build" / "patches.sha256"
    # The binary is rebuilt when the patches or the pinned commit changed, not only when it is missing.
    wanted = hashlib.sha256(COMMIT.encode() + PATCH.read_bytes()).hexdigest()
    if binary.exists() and stamp.exists() and stamp.read_text() == wanted:
        return binary
    if not ROOT.exists():
        ROOT.parent.mkdir(parents=True, exist_ok=True)
        subprocess.run(["git", "clone", "-q", REPOSITORY, str(ROOT)], check=True)
    subprocess.run(["git", "-C", str(ROOT), "checkout", "-q", "--force", COMMIT], check=True)
    # git apply fails, and the build with it, where faad2's sources no longer have the code the trace follows.
    subprocess.run(["git", "-C", str(ROOT), "apply", str(PATCH)], check=True)
    subprocess.run(["cmake", "-S", str(ROOT), "-B", str(ROOT / "build"), "-DCMAKE_BUILD_TYPE=Release"], check=True, capture_output=True)
    subprocess.run(["cmake", "--build", str(ROOT / "build"), "-j8"], check=True, capture_output=True)
    stamp.write_text(wanted)
    return binary


def trace(path):
    """The raw data blocks faad2 reads in a file, in order: per block its length in bits and its records, as lists of fields."""
    binary = build()
    result = subprocess.run([str(binary), "-o", "/dev/null", str(path)], capture_output=True)
    blocks = []
    start = None
    for line in result.stderr.decode("latin-1").splitlines():
        if not line.startswith("@@"):
            continue
        kind, *fields = line[2:].split()
        if kind == "AU":
            start = int(fields[0])
            blocks.append({"records": []})
        elif kind == "END" and blocks:
            blocks[-1]["bits"] = int(fields[0]) - start
        elif blocks:
            if kind == "EL":
                fields[1] = str(int(fields[1]) - start)
            if kind == "ICS" and len(fields) == 10:
                fields.append("")
            blocks[-1]["records"].append([kind] + fields)
    return blocks


if __name__ == "__main__":
    for block in trace(sys.argv[1]):
        print(block.get("bits"), block["records"])
