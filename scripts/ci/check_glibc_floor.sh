#!/usr/bin/env bash
# Fails unless the Linux executable needs nothing from the system but glibc 2.27 or older and the libraries glibc ships:
#   scripts/ci/check_glibc_floor.sh <executable>
# It reads the dynamic section and the version references (objdump -p), so a requirement that no symbol shows, such as GLIBC_ABI_DT_RELR
# (glibc 2.36), counts too. An empty answer, a file that is not an ELF executable and a missing file fail as well.
set -euo pipefail

file="${1:?usage: check_glibc_floor.sh <executable>}"
limit="2.27"
dump="$(objdump -p "$file")"

needed="$(awk '$1 == "NEEDED" { print $2 }' <<< "$dump")"
references="$(awk '/^Version References:/ { in_section = 1; next } in_section && /^[^ ]/ { in_section = 0 } in_section && NF == 4 && $1 ~ /^0x/ { print $4 }' <<< "$dump")"

if [ -z "$needed" ] || [ -z "$references" ]; then
    echo "$file: no libraries or no version references found; not a dynamically linked executable?" >&2
    exit 1
fi

status=0
while IFS= read -r library; do
    if ! [[ "$library" =~ ^(libc\.so\.6|libm\.so\.6|libdl\.so\.2|librt\.so\.1|libpthread\.so\.0|ld-linux-[a-z0-9_-]+\.so\.[0-9]+)$ ]]; then
        echo "$file needs $library, which glibc does not ship" >&2
        status=1
    fi
done <<< "$needed"

highest="0"
while IFS= read -r reference; do
    if [[ "$reference" =~ ^GLIBC_([0-9]+(\.[0-9]+)*)$ ]]; then
        version="${BASH_REMATCH[1]}"
        if [ "$(printf '%s\n' "$version" "$limit" | sort -V | tail -n 1)" != "$limit" ]; then
            echo "$file needs $reference, above GLIBC_$limit" >&2
            status=1
        fi
        if [ "$(printf '%s\n' "$version" "$highest" | sort -V | tail -n 1)" = "$version" ]; then
            highest="$version"
        fi
    else
        echo "$file has the version reference $reference, which is not a plain GLIBC_x.y" >&2
        status=1
    fi
done <<< "$references"

if [ "$status" = 0 ]; then
    echo "$file needs glibc $highest at most (limit $limit)"
fi
exit "$status"
