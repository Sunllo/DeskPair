#!/bin/bash
# Holds every <lang>.lproj/Localizable.strings to en.lproj: well-formed, same keys, same placeholders.
set -euo pipefail
APP="$(cd "$(dirname "$0")/../../mobile/iosApp/iosApp" && pwd)"
keys() { { grep -o '^"[^"]*"' "$1" || true; } | tr -d '"' | sort; }
placeholders_of() { # key file -> the placeholders of that key's value, sorted, on one line
    { grep "^\"$1\" = " "$2" || true; } | sed -E 's/^"[^"]*" = "(.*)";$/\1/' | { grep -oE '%([0-9]+\$)?(@|lld|d|ld)' || true; } | sort | tr '\n' ' '
}
status=0
for f in "$APP"/*.lproj/Localizable.strings "$APP"/*.lproj/InfoPlist.strings; do
    plutil -lint "$f" >/dev/null || { echo "malformed: $f" >&2; status=1; }
done
en="$APP/en.lproj/Localizable.strings"
for f in "$APP"/*.lproj/Localizable.strings; do
    [ "$f" = "$en" ] && continue
    lang="$(basename "$(dirname "$f")")"
    missing="$(comm -23 <(keys "$en") <(keys "$f") | tr '\n' ' ')"
    stale="$(comm -13 <(keys "$en") <(keys "$f") | tr '\n' ' ')"
    [ -z "$missing" ] || { echo "$lang is missing: $missing" >&2; status=1; }
    [ -z "$stale" ] || { echo "$lang carries keys English no longer has: $stale" >&2; status=1; }
    while read -r key; do
        a="$(placeholders_of "$key" "$en")"; b="$(placeholders_of "$key" "$f")"
        [ "$a" = "$b" ] || { echo "$lang: placeholders differ for $key (en: $a, $lang: $b)" >&2; status=1; }
    done < <(comm -12 <(keys "$en") <(keys "$f"))
done
[ $status -eq 0 ] && echo "translations agree with English" || exit $status
