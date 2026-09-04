#!/usr/bin/env bash
# keys-ab.sh — class A / class B tasks for the AP6 part 1 corpus (#466), keyed by merged commits.
#
# Usage:  bench/golden/ap6/scripts/keys-ab.sh [<branch>=develop] [<per-class>=10]  > ab.json
#         Run from the repository root. Prints a JSON array of tasks (class A first, then B) on stdout and
#         the candidate ledger (accepted / rejected, with the reason) on stderr.
#
# The key is what a merged commit touched — mechanical, chosen by nobody who runs or grades an arm, and
# invisible to every arm. The method of the pilot (bench/golden/ap6-part1-positioning.md).
#
# Classification of a first-parent merge M on <branch>, walked newest-first, first <per-class> accepted per
# class in that order (no hand picking):
#
#   product .cs   = paths matching ^(src|plugins)/.*\.cs$ that M adds or modifies (diff M^1..M, renames count
#                   as their new name, deletions are dropped — an arm cannot find a file that is gone).
#   test .cs      = paths matching ^tests/.*\.cs$ likewise.
#   exists@HEAD   = every key file must still exist at HEAD, and the seed must still occur at HEAD, because
#                   the arms run against the working tree, not against M.
#
#   class A (local navigation, expectation: rg wins)
#     - exactly ONE product .cs.
#     - key.files   = that file + the test .cs files;  key.symbols = top-level types declared in that file at M.
#     - seed        = the first member declaration (method / property / field / ctor) that the diff of that
#                     file ADDS, in diff order; fallback when the diff adds no member: the first type name, and
#                     the task is flagged seedInKey.
#     - plausibility: `git grep -lw <seed> M -- '*.cs'` names <= 3 files (a seed found in half the repository
#                     does not test local navigation). Rejected otherwise.
#     - query       = "Which file and type declare `<seed>`?"
#
#   class B (whole-program, expectation: graph wins)
#     - 3..8 product .cs across >= 2 distinct src/<Project> directories.
#     - seed        = basename (without .cs) of the LARGEST product .cs at M.
#     - key.files   = every product + test .cs of the merge EXCEPT the seed's file;
#       key.symbols = top-level types declared in those files at M.
#     - plausibility: the seed occurs textually (`git grep -lw`) in <= 50 % of the key files — otherwise grep
#                     alone answers the task and it measures nothing. Rejected otherwise.
#     - query       = "Which other files and types must change together with `<seed>` when its contract changes?"
#
#   seedInKey is set (never silently) when the seed equals a key symbol or a key file stem, whole-word.
#
# Symbols are bare `TypeName` — no namespace, arity or span — because the rg arm cannot produce node ids and
# the harness matches by id-substring OR exact name (BenchModels.cs, GoldenMatch).
set -euo pipefail

BRANCH="${1:-develop}"
PER_CLASS="${2:-10}"

json_str() { local s=${1//\\/\\\\}; s=${s//\"/\\\"}; printf '"%s"' "$s"; }
json_arr() { local out="" x; for x in "$@"; do out+="${out:+, }$(json_str "$x")"; done; printf '[%s]' "$out"; }

# Top-level type names declared in <blob>: indent 0 under a file-scoped namespace (`namespace X;`), indent
# 0..4 under a namespace block. Nested types sit one level deeper and are not top-level.
top_level_types() {
  local src indent='{0,4}'
  src=$(git show "$1" 2>/dev/null | sed -E 's/\r$//') || true
  printf '%s\n' "$src" | grep -qE '^namespace [^{]*;' && indent='{0}'
  printf '%s\n' "$src" \
    | grep -E "^ $indent(public |internal |private |protected )?(static |sealed |abstract |partial |readonly |ref |unsafe |file )*(class|record|interface|struct|enum)( struct| class)? +[A-Za-z_][A-Za-z0-9_]*" \
    | sed -E 's/^.*(class|record|interface|struct|enum)( struct| class)? +([A-Za-z_][A-Za-z0-9_]*).*/\3/' \
    | awk '!seen[$0]++'
}

# The first member declaration the diff of <file> adds between <base> and <commit>.
first_added_member() {
  git diff "$1" "$2" -- "$3" \
    | sed -E 's/\r$//' \
    | grep -E '^\+ +(public|internal|private|protected) ' \
    | grep -vE '\b(class|record|interface|struct|enum|namespace|operator|event)\b' \
    | sed -E 's/^\+//' \
    | while IFS= read -r line; do
        line=$(printf '%s' "$line" | sed -E 's/<[^<>]*>//g; s/<[^<>]*>//g; s/^([^(={;]*).*/\1/')
        name=$(printf '%s' "$line" | grep -oE '[A-Za-z_][A-Za-z0-9_]*' | tail -n1 || true)
        case "$name" in ""|get|set|init|add|remove|this|new|where|override|virtual|async|static|readonly|const|abstract|sealed|partial|required|unsafe) continue ;; esac
        printf '%s\n' "$name"; break
      done || true
}

exists_at_head() { git cat-file -e "HEAD:$1" 2>/dev/null; }
grep_files_at() { git grep -lw -- "$2" "$1" -- "${@:3}" 2>/dev/null | sed -E "s/^$1://" || true; }

contains_word() { # <text> <word>
  printf '%s\n' "$1" | grep -qE "(^|[^A-Za-z0-9_-])$(printf '%s' "$2" | sed 's/[.[\*^$]/\\&/g')([^A-Za-z0-9_-]|$)"
}
seed_in_key() { # <seed> <symbols...> -- <files...>
  local seed=$1; shift; local x
  for x in "$@"; do
    [ "$x" = "--" ] && continue
    local base=${x##*/}; base=${base%.cs}
    if [ "$x" = "$seed" ] || [ "$base" = "$seed" ] || [ "${base%%.*}" = "$seed" ]; then return 0; fi
  done
  return 1
}

emit_task() { # id class query files-json symbols-json ref rule seedInKey expectation
  printf '  { "schemaVersion": 1, "id": %s, "class": %s, "corpus": "Brain",\n    "query": %s,\n    "key": { "files": %s, "symbols": %s },\n    "keySource": { "method": "merge-commit", "ref": %s, "rule": %s },\n    "seedInKey": %s, "expectation": %s }' \
    "$(json_str "$1")" "$(json_str "$2")" "$(json_str "$3")" "$4" "$5" "$(json_str "$6")" "$(json_str "$7")" "$8" "$(json_str "$9")"
}

declare -a A_TASKS=() B_TASKS=()
nA=0; nB=0; seenA=0; seenB=0; rejA=0; rejB=0

while read -r M; do
  [ "$nA" -ge "$PER_CLASS" ] && [ "$nB" -ge "$PER_CLASS" ] && break
  P="$M^1"
  mapfile -t CS < <(git diff --name-only --diff-filter=AMR "$P" "$M" | sed -E 's/\r$//' | grep -E '\.cs$' || true)
  mapfile -t PROD < <(printf '%s\n' "${CS[@]}" | grep -E '^(src|plugins)/' || true)
  mapfile -t TEST < <(printf '%s\n' "${CS[@]}" | grep -E '^tests/' || true)
  [ "${#PROD[@]}" -eq 0 ] && continue
  short=$(git rev-parse --short "$M")

  if [ "${#PROD[@]}" -eq 1 ] && [ "$nA" -lt "$PER_CLASS" ]; then
    seenA=$((seenA+1))
    f=${PROD[0]}
    if ! exists_at_head "$f"; then echo "A reject $short: $f gone at HEAD" >&2; rejA=$((rejA+1)); continue; fi
    mapfile -t TYPES < <(top_level_types "$M:$f")
    if [ "${#TYPES[@]}" -eq 0 ]; then echo "A reject $short: no top-level type in $f" >&2; rejA=$((rejA+1)); continue; fi
    seed=$(first_added_member "$P" "$M" "$f")
    flag=false; how="first member declaration added by the diff"
    if [ -z "$seed" ]; then seed=${TYPES[0]}; flag=true; how="no member added — fallback to the first type name"; fi
    hits=$(grep_files_at "$M" "$seed" '*.cs' | wc -l | tr -d ' ')
    if [ "$hits" -gt 3 ]; then echo "A reject $short: seed $seed in $hits files at M (> 3)" >&2; rejA=$((rejA+1)); continue; fi
    if [ "$(grep_files_at HEAD "$seed" '*.cs' | wc -l | tr -d ' ')" -eq 0 ]; then echo "A reject $short: seed $seed gone at HEAD" >&2; rejA=$((rejA+1)); continue; fi
    FILES=("$f"); for t in "${TEST[@]}"; do exists_at_head "$t" && FILES+=("$t"); done
    seed_in_key "$seed" "${TYPES[@]}" -- "${FILES[@]}" && flag=true
    nA=$((nA+1)); id=$(printf 'A-%02d' "$nA")
    rule="single product .cs in the merge; key = that file + changed tests; symbols = top-level types at the commit; seed = $how; git grep -lw seed at the commit = $hits file(s) (rule: <= 3)"
    A_TASKS+=("$(emit_task "$id" A "Which file and type declare \`$seed\`?" "$(json_arr "${FILES[@]}")" "$(json_arr "${TYPES[@]}")" "$M" "$rule" "$flag" rg)")
    echo "A accept $short → $id seed=$seed files=${#FILES[@]} symbols=${#TYPES[@]} grep=$hits seedInKey=$flag" >&2
    continue
  fi

  if [ "${#PROD[@]}" -ge 3 ] && [ "${#PROD[@]}" -le 8 ] && [ "$nB" -lt "$PER_CLASS" ]; then
    projects=$(printf '%s\n' "${PROD[@]}" | sed -E 's#^((src|plugins)/[^/]+)/.*#\1#' | sort -u | wc -l | tr -d ' ')
    [ "$projects" -lt 2 ] && continue
    seenB=$((seenB+1))
    gone=""; for f in "${PROD[@]}" "${TEST[@]}"; do exists_at_head "$f" || gone+=" $f"; done
    if [ -n "$gone" ]; then echo "B reject $short: gone at HEAD:$gone" >&2; rejB=$((rejB+1)); continue; fi
    seedfile=""; seedsize=-1
    for f in "${PROD[@]}"; do s=$(git cat-file -s "$M:$f"); if [ "$s" -gt "$seedsize" ]; then seedsize=$s; seedfile=$f; fi; done
    seed=${seedfile##*/}; seed=${seed%.cs}
    FILES=(); for f in "${PROD[@]}" "${TEST[@]}"; do [ "$f" != "$seedfile" ] && FILES+=("$f"); done
    TYPES=(); for f in "${FILES[@]}"; do mapfile -t T < <(top_level_types "$M:$f"); TYPES+=("${T[@]}"); done
    mapfile -t TYPES < <(printf '%s\n' "${TYPES[@]}" | awk 'NF && !seen[$0]++')
    if [ "${#TYPES[@]}" -eq 0 ]; then echo "B reject $short: no top-level types in key files" >&2; rejB=$((rejB+1)); continue; fi
    hits=$(grep_files_at "$M" "$seed" "${FILES[@]}" | wc -l | tr -d ' ')
    if [ $((hits * 2)) -gt "${#FILES[@]}" ]; then echo "B reject $short: seed $seed textually in $hits/${#FILES[@]} key files (> 50 %)" >&2; rejB=$((rejB+1)); continue; fi
    if [ "$(grep_files_at HEAD "$seed" '*.cs' | wc -l | tr -d ' ')" -eq 0 ]; then echo "B reject $short: seed $seed gone at HEAD" >&2; rejB=$((rejB+1)); continue; fi
    flag=false; seed_in_key "$seed" "${TYPES[@]}" -- "${FILES[@]}" && flag=true
    nB=$((nB+1)); id=$(printf 'B-%02d' "$nB")
    rule="${#PROD[@]} product .cs across $projects src projects; key = all .cs touched by the merge, seed file ($seedfile) removed; symbols = top-level types at the commit; seed = basename of the largest product .cs; seed occurs textually in $hits of ${#FILES[@]} key files (rule: <= 50 %)"
    B_TASKS+=("$(emit_task "$id" B "Which other files and types must change together with \`$seed\` when its contract changes?" "$(json_arr "${FILES[@]}")" "$(json_arr "${TYPES[@]}")" "$M" "$rule" "$flag" graph)")
    echo "B accept $short → $id seed=$seed files=${#FILES[@]} symbols=${#TYPES[@]} grep=$hits/${#FILES[@]} seedInKey=$flag" >&2
  fi
done < <(git log --first-parent --merges --format=%H "$BRANCH")

echo "A: $nA accepted of $seenA single-file candidates ($rejA rejected); B: $nB accepted of $seenB multi-project candidates ($rejB rejected)" >&2
[ "$nA" -eq "$PER_CLASS" ] && [ "$nB" -eq "$PER_CLASS" ] || { echo "not enough candidates" >&2; exit 1; }

printf '[\n'
first=1
for t in "${A_TASKS[@]}" "${B_TASKS[@]}"; do
  [ $first -eq 1 ] || printf ',\n'
  first=0
  printf '%s' "$t"
done
printf '\n]\n'
