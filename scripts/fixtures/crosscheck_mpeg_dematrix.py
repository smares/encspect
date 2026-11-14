#!/usr/bin/env python3
"""Checks the dematrixing and de-normalization of Encspect's multichannel decoder against mctoolamed, for every dematrix procedure and tc_allocation
(docs/development.md, "MPEG audio decoding"; ISO/IEC 13818-3 2.5.3.2.1.1 and 2.5.3.2.5).

    crosscheck_mpeg_dematrix.py [--analyzer CMD] [--pcmdump CMD] [--only NAME ...]

The ISO conformance streams use few of the matrix procedures and channel allocations the standard has. This script takes the encoder-written 5.0, 4.0, 2.2 and 2.1 fixtures of
mctoolame (tests/fixtures/mpeg-audio/layer2/external-mctoolame-*), rewrites the `dematrix_procedure` and `tc_allocation` of every multichannel header and fixes the 16-bit
multichannel CRC, so that the same samples are dematrixed in every way the configuration allows (the rewrite changes what the channels mean, not the samples). Encspect and
mctoolamed (derived from the ISO reference software, built from its pinned source and run as a black box) decode each variant, and every channel must agree within 1 LSB of 16 bits,
the samples where both clip included. The positions of the fields come from the analyzer's own report (--json): the header, the CRC and the composite status information.

Exit status 0 where every variant agrees, 1 where one does not, 2 where mctoolamed cannot be built.
"""

import argparse
import array
import json
import math
import pathlib
import shlex
import subprocess
import sys
import tempfile

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import mctoolamed_tool  # noqa: E402

ROOT = HERE.parents[1]
FIXTURES = ROOT / "tests" / "fixtures" / "mpeg-audio" / "layer2"
DEFAULT_ANALYZER = "dotnet " + str(ROOT / "src" / "Encspect.Cli" / "bin" / "Release" / "net10.0" / "encspect.dll")
DEFAULT_PCMDUMP = "dotnet " + str(ROOT / "tests" / "Encspect.PcmDump" / "bin" / "Release" / "net10.0" / "Encspect.PcmDump.dll")

# Per fixture, the variants (dematrix procedure, tc_allocation): every tc_allocation of the configuration that 13818-3 2.5.2.15 assigns, with the procedures the standard allows
# (procedure 2 only in 3/1 and 3/2, tc_allocation 5 of 3/1 only there).
VARIANTS = {
    "external-mctoolame-5-0": [(p, tc) for p in (0, 1, 2) for tc in range(8)],
    "external-mctoolame-4-0": [(p, tc) for p in (0, 1) for tc in range(5)] + [(2, tc) for tc in range(6)],
    "external-mctoolame-2-2": [(p, tc) for p in (0, 1) for tc in range(4)],
    "external-mctoolame-2-1": [(p, tc) for p in (0, 1) for tc in range(3)],
}


def crc_bits(bits, crc=0xFFFF):
    """The CRC-16 of ISO/IEC 11172-3 2.4.3.1 (x^16 + x^15 + x^2 + 1) over bits."""
    for bit in bits:
        top = (crc >> 15) & 1
        crc = (crc << 1) & 0xFFFF
        if top ^ bit:
            crc ^= 0x8005
    return crc


def get_bits(data, start, count):
    return [(data[(start + i) >> 3] >> (7 - ((start + i) & 7))) & 1 for i in range(count)]


def set_bits(data, start, count, value):
    for i in range(count):
        position = start + i
        if (value >> (count - 1 - i)) & 1:
            data[position >> 3] |= 0x80 >> (position & 7)
        else:
            data[position >> 3] &= ~(0x80 >> (position & 7))


def start_bit(node):
    return node["offset"] * 8 + node.get("bitOffset", 0)


def end_bit(node):
    return start_bit(node) + node.get("bitLength", 8 * node["length"])


def walk(node):
    yield node
    for child in node.get("children") or []:
        yield from walk(child)


def multichannel_nodes(report):
    for node in walk(report["root"]):
        if node["name"] == "Multichannel":
            children = {c["name"]: c for c in node["children"]}
            if "SCFSI" in children:
                yield children


def patch(original, headers, procedure, allocation):
    data = bytearray(original)
    for children in headers:
        set_bits(data, start_bit(next(c for c in children["Header"]["children"] if c["name"] == "Dematrix Procedure")), 2, procedure)
        field = next(c for c in children["Composite Status"]["children"] if c["name"] == "Channel Allocation")
        # The node starts at tc_sbgr_select, followed by dyn_cross_on and mc_prediction_on; the tc_allocation fields (one, or one per subband group) follow.
        groups = 1 if get_bits(data, start_bit(field), 1)[0] else 12
        width = (field["bitLength"] - 3) // groups
        for g in range(groups):
            set_bits(data, start_bit(field) + 3 + g * width, width, allocation & ((1 << width) - 1))
        header = start_bit(children["Header"])
        crc_at = start_bit(children["CRC"])
        crc = crc_bits(get_bits(data, header, crc_at - header))
        status = crc_at + 16
        crc = crc_bits(get_bits(data, status, end_bit(children["SCFSI"]) - status), crc)
        set_bits(data, crc_at, 16, crc)
    return bytes(data)


def difference(args, decoder, path, temporary):
    """The largest difference in 16-bit LSB between Encspect's channels and mctoolamed's."""
    dump = temporary / "dump"
    subprocess.run(shlex.split(args.pcmdump) + [str(dump), str(path)], capture_output=True, check=True)
    info = json.loads(next(dump.glob("*.pcm.json")).read_text())["streams"][0]
    floats = array.array("f")
    floats.frombytes((dump / info["pcm"]).read_bytes())
    channels = info["channels"]
    layout = info["channelLayout"]
    reference, lfe = mctoolamed_tool.decode_pcm(decoder, path)
    wide = [i for i, speaker in enumerate(layout) if speaker != "LFE"]
    if len(reference) < len(wide):
        return float("inf")
    worst = 0
    for k, i in enumerate(wide):
        theirs = memoryview(reference[k][:2 * (len(floats) // channels)]).cast("h")
        for n, value in enumerate(theirs):
            ours = max(-32768, min(32767, math.floor(floats[n * channels + i] * 32768.0 + 0.5)))
            worst = max(worst, abs(ours - value))
    return worst


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--analyzer", default=DEFAULT_ANALYZER)
    parser.add_argument("--pcmdump", default=DEFAULT_PCMDUMP)
    parser.add_argument("--only", nargs="*", default=[])
    args = parser.parse_args(argv)
    try:
        decoder = mctoolamed_tool.build()
    except (SystemExit, OSError) as error:
        print(f"mctoolamed is not available: {error}")
        return 2
    failed = checked = 0
    for name, variants in VARIANTS.items():
        if args.only and name not in args.only:
            continue
        original = (FIXTURES / f"{name}.mp2").read_bytes()
        report = json.loads(subprocess.run(shlex.split(args.analyzer) + ["--json", str(FIXTURES / f"{name}.mp2")], capture_output=True, text=True, check=True).stdout)[0]
        headers = list(multichannel_nodes(report))
        with tempfile.TemporaryDirectory() as temporary:
            for procedure, allocation in variants:
                path = pathlib.Path(temporary) / "variant.mp2"
                path.write_bytes(patch(original, headers, procedure, allocation))
                worst = difference(args, decoder, path, pathlib.Path(temporary))
                checked += 1
                if worst > 1:
                    failed += 1
                    print(f"FAIL {name} procedure {procedure}, tc_allocation {allocation}: largest difference {worst} LSB")
        print(f"{name}: {len(variants)} variants, {len(headers)} headers rewritten in each")
    print(f"{checked} variants compared with mctoolamed, {failed} outside 1 LSB")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
