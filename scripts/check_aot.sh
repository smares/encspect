#!/usr/bin/env bash
# Checks that the NativeAOT build of the CLI reports like the JIT build: both analyze all of tests/fixtures in one process each with
# `--json --measure` (which decodes, verifies and measures every stream that has a decoder), and the two reports, their standard error and their exit codes must be identical but for the tool version line.
# CI and the release workflow run it on the CLI they published (-p); without -p it publishes the NativeAOT CLI for this platform first,
# which needs the platform's native toolchain (clang and a linker, see https://learn.microsoft.com/dotnet/core/deploying/native-aot/).
# With -p it takes about three minutes, build of the JIT side included; without it about six. Measuring adds under a second to the run over
# the corpus (19.0 s with --verify, 19.6 s with --measure, JIT, this machine) and about 20 MB to the report. Each report is about 900 MB; they are
# deleted when the two agree, and the output directory too unless one was named.
#   scripts/check_aot.sh [-p published/encspect] [output directory]
set -euo pipefail

published=""
if [ "${1:-}" = "-p" ]; then
    published_directory="$(cd "$(dirname "$2")" && pwd)"
    published="$published_directory/$(basename "$2")"
    shift 2
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
keep=1
if [ -n "${1:-}" ]; then
    mkdir -p "$1"
    out="$(cd "$1" && pwd)"
else
    out="$(mktemp -d)"
    keep=0
fi
trap 'if [ "$keep" = 0 ]; then rm -rf "$out"; fi' EXIT

# The JIT build is reused when the output directory already holds one made from this very tree (the second check of a job, for the
# x86-64-v3 build); any change of the sources or of git's state builds it again.
stamp="$(cd "$root" && { git rev-parse HEAD; git status --porcelain; } 2>/dev/null | md5sum | cut -d' ' -f1)"
if [ "$stamp" = "$(cat "$out/jit.stamp" 2>/dev/null || true)" ] && [ -f "$out/jit/encspect.dll" ]; then
    echo "reusing the JIT build in $out/jit"
else
    dotnet build "$root/src/Encspect.Cli" -c Release -o "$out/jit" > /dev/null
    echo "$stamp" > "$out/jit.stamp"
fi
if [ -z "$published" ]; then
    dotnet publish "$root/src/Encspect.Cli" -c Release -p:PublishAot=true -o "$out/aot" > /dev/null
    published="$out/aot/encspect"
fi
"$published" --version

# The tool version line is the only line allowed to differ, so a build from another commit still compares; carriage returns
# (Windows) are dropped from both sides.
normalize() {
    tr -d '\r' | sed 's/^\( *"version": "\).*"$/\1"/'
}

# run <name> <command...>: the normalized report in $out/<name>.json, standard error in $out/<name>.err, exit code in $out/<name>.status.
# A report with findings gives a nonzero exit code, so the code is compared rather than ignored; a failure of the normalizing is a failure.
run() {
    local name="$1"
    shift
    local statuses
    set +e
    "$@" 2> "$out/$name.err.raw" | normalize > "$out/$name.json"
    statuses=("${PIPESTATUS[@]}")
    set -e
    if [ "${statuses[1]}" != 0 ]; then
        echo "normalizing the $name report failed" >&2
        exit 1
    fi
    tr -d '\r' < "$out/$name.err.raw" > "$out/$name.err"
    rm -f "$out/$name.err.raw"
    echo "${statuses[0]}" > "$out/$name.status"
}

cd "$root"
run jit dotnet "$out/jit/encspect.dll" --json --measure tests/fixtures
run aot "$published" --json --measure tests/fixtures

for part in status err json; do
    if ! cmp -s "$out/jit.$part" "$out/aot.$part"; then
        echo "the NativeAOT $part differs from the JIT build's:" >&2
        if [ "$part" = status ]; then
            echo "JIT $(cat "$out/jit.status"), NativeAOT $(cat "$out/aot.status")" >&2
        else
            diff "$out/jit.$part" "$out/aot.$part" | head -20 >&2 || true
        fi
        exit 1
    fi
done
count="$(grep -c '^    "source":' "$out/jit.json" || true)"
rm -f "$out/jit.json" "$out/aot.json"
echo "NativeAOT CLI ($published) reports like the JIT build on $count inputs"
