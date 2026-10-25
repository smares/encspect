#!/usr/bin/env bash
# Publishes the CLI with NativeAOT and checks that the binary starts and gives the same CSV report, with decoding, as the framework-dependent
# build for every tenth fixture. A local check, not part of CI: the whole run takes about three minutes and needs the platform's native toolchain
# (clang and the linker, see https://learn.microsoft.com/dotnet/core/deploying/native-aot/).
#   scripts/check_aot.sh [output directory]
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:-$(mktemp -d)}"

dotnet build "$root/src/Encspect.Cli" -c Release -o "$out/jit" > /dev/null
dotnet publish "$root/src/Encspect.Cli" -c Release -p:PublishAot=true -o "$out/aot" > /dev/null
"$out/aot/encspect" --version

checked=0
while IFS= read -r fixture; do
    "$out/jit/encspect" --csv --verify "$fixture" > "$out/jit.csv" 2>&1 || true
    "$out/aot/encspect" --csv --verify "$fixture" > "$out/aot.csv" 2>&1 || true
    if ! cmp -s "$out/jit.csv" "$out/aot.csv"; then
        echo "differs: $fixture" >&2
        diff "$out/jit.csv" "$out/aot.csv" | head -5 >&2
        exit 1
    fi
    checked=$((checked + 1))
done < <(find "$root/tests/fixtures" -type f ! -name '*.json' ! -name '*.md' ! -name '*.txt' ! -name '*.py' | sort | awk 'NR % 10 == 1')
echo "NativeAOT build in $out/aot starts and reports like the JIT build on $checked fixtures"
