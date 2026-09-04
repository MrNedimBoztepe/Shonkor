#!/usr/bin/env bash
# build-tasks.sh — regenerate bench/golden/ap6/tasks.json from both key scripts (#466).
#
# Usage:  bench/golden/ap6/scripts/build-tasks.sh <corpus-root>      (from the repository root)
#         <corpus-root> = the "Corpus-A" checkout from projects.json. keys-ab.sh reads this repository's
#         merge history; keys-c.sh reads the CMS checkout and writes the out-of-repo mapping there.
#         The ledgers of both scripts go to stderr — keep them for the PR (counts, rejections, reasons).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out="$here/../tasks.json"
ab=$(bash "$here/keys-ab.sh" develop 10)
c=$(bash "$here/keys-c.sh" "$1" 4 3 3)
# Each script prints a complete array; join them by dropping the closing / opening bracket lines.
{ printf '%s' "$ab" | sed '$d'; printf ',\n'; printf '%s\n' "$c" | sed '1d'; } > "$out"
echo "wrote $out ($(grep -c '"id":' "$out") tasks)" >&2
