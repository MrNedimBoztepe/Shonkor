#!/usr/bin/env bash
# build-tasks.sh — regenerate bench/golden/ap6/tasks.json from both key scripts (#466).
#
# Usage:  bench/golden/ap6/scripts/build-tasks.sh [<corpus-root>]      (from the repository root)
#         <corpus-root> = the "Corpus-A" checkout from projects.json. keys-ab.sh reads this repository's
#         merge history; keys-c.sh reads the CMS checkout and writes the out-of-repo mapping there.
#         Without <corpus-root> the class C block is carried over verbatim from the committed tasks.json
#         (keys-c.sh renumbers the anonymised tokens on every run — regenerate C only on purpose, #490).
#         The ledgers of both scripts go to stderr — keep them for the PR (counts, rejections, reasons).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
out="$here/../tasks.json"
# corpus keyed at develop before #488 (de44654); the walk is pinned so that later merges cannot shift the
# A/B rows — bump deliberately, with a corpus re-baseline.
walk_ref=de44654380032c1766d089d859c7e3c86ac79a74
ab=$(bash "$here/keys-ab.sh" "$walk_ref" 10)
if [ $# -ge 1 ]; then
  c=$(bash "$here/keys-c.sh" "$1" 4 3 3)
else
  # The C block is the tail of the committed file from the first class C task to the closing bracket.
  # `|| true`: under `set -o pipefail` a grep without a hit would end the script silently (exit 1)
  # before the check below ever ran.
  from=$(grep -n '"class": "C"' "$out" | head -n1 | cut -d: -f1 || true)
  [ -n "$from" ] || { echo "no class C task in $out — pass <corpus-root>" >&2; exit 1; }
  c=$(printf '[\n'; tail -n +"$from" "$out")
  c_count=$(printf '%s\n' "$c" | grep -c '"id":' || true)
  [ "$c_count" -eq 10 ] || { echo "expected 10 class C tasks in $out, found $c_count — pass <corpus-root>" >&2; exit 1; }
  echo "C: carried over $c_count tasks from the committed $out" >&2
fi
# Each script prints a complete array; join them by dropping the closing / opening bracket lines.
# Assert the total before touching the file so that a short run never overwrites a good corpus.
joined=$(printf '%s' "$ab" | sed '$d'; printf ',\n'; printf '%s\n' "$c" | sed '1d')
total=$(printf '%s\n' "$joined" | grep -c '"id":' || true)
[ "$total" -eq 30 ] || { echo "expected 30 tasks, got $total — not writing $out" >&2; exit 1; }
printf '%s\n' "$joined" > "$out"
echo "wrote $out ($total tasks)" >&2
