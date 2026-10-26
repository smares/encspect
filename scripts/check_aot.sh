#!/usr/bin/env bash
# Checks that the NativeAOT build of the CLI reports like the JIT build: both analyze all of tests/fixtures in one process each with
# `--json --verify`, and the two reports must be byte-identical but for the tool version line. CI runs it on the CLI it published
# (-p); without -p it publishes the NativeAOT CLI for this platform first, which needs the platform's native toolchain (clang and the
# linker, see https://learn.microsoft.com/dotnet/core/deploying/native-aot/). A local run takes about three minutes.
#   scripts/check_aot.sh [-p published/encspect] [output directory]
set -euo pipefail

published=""
if [ "${1:-}" = "-p" ]; then
    published="$2"
    shift 2
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:-$(mktemp -d)}"
mkdir -p "$out"

dotnet build "$root/src/Encspect.Cli" -c Release -o "$out/jit" > /dev/null
if [ -z "$published" ]; then
    dotnet publish "$root/src/Encspect.Cli" -c Release -p:PublishAot=true -o "$out/aot" > /dev/null
    published="$out/aot/encspect"
fi
"$published" --version

# The tool version line is the only line allowed to differ, so a build from another commit still compares.
normalize() {
    sed 's/^\( *"version": "\).*"$/\1"/'
}

cd "$root"
# A report with findings gives a nonzero exit code, so the codes are compared instead of ignored.
jit_status=0
aot_status=0
dotnet "$out/jit/encspect.dll" --json --verify tests/fixtures | normalize > "$out/jit.json" || jit_status=${PIPESTATUS[0]}
"$published" --json --verify tests/fixtures | normalize > "$out/aot.json" || aot_status=${PIPESTATUS[0]}
if [ "$jit_status" != "$aot_status" ]; then
    echo "the exit codes differ: JIT $jit_status, NativeAOT $aot_status" >&2
    exit 1
fi
if ! cmp -s "$out/jit.json" "$out/aot.json"; then
    echo "the NativeAOT report differs from the JIT report:" >&2
    diff "$out/jit.json" "$out/aot.json" | head -20 >&2
    exit 1
fi
echo "NativeAOT CLI ($published) reports like the JIT build on $(grep -c '^    "source":' "$out/jit.json") inputs"
