#!/usr/bin/env bash
# keys-c.sh — class C tasks for the AP6 part 1 corpus (#466): cross-tech CMS chains keyed from Unicorn .yml.
#
# Usage:  bench/golden/ap6/scripts/keys-c.sh <corpus-root> [<c1>=5] [<c2>=0] [<c3>=5]  > c.json
#         <corpus-root> is the checkout of the CMS solution (projects.json: "Corpus-A"). The script only
#         READS it, with two exceptions: it writes <corpus-root>/bench/ap6-mapping.json (the anonymised ↔
#         real mapping, resolved by the harness at run time) and adds `bench/` to
#         <corpus-root>/.git/info/exclude so the mapping never enters the customer repository either.
#         Prints a JSON array of anonymised tasks on stdout; the candidate ledger on stderr.
#         <c2> must be 0 (kept as a positional argument so that callers do not shift; see below).
#         Refuses to run while tracked .yml/.cs/.cshtml/.sln files of the corpus are modified: `git grep`
#         reads the working tree, but keySource.ref claims HEAD.
#
# NOTE: Model tokens are keyed by path + class (#488). The first revision keyed them by class simple name
#         (same-named classes in different files collided: 6 Model tokens were assigned twice); the committed
#         tasks.json and mapping were regenerated with the current keying in #491.
#
# C2 (controller → rendering items) is not generated. At 9d7f9ce every one of the 11 controllers named by
# ≥ 2 renderings is mentioned in exactly as many tracked .yml files as it has renderings —
# `git grep -lw <Controller> -- '*.yml'` returns the key with zero over-selection — so no C2 task can carry
# `graph-only` truthfully, and the rule "accept only when yml mentions exceed the key size" selects nothing.
# Class C is C1 × 5 + C3 × 5 (#491). The C2 walk stays as a ledger-only diagnostic: if a later corpus
# revision reports an over-selecting controller, C2 becomes admissible under that rule — reopen the
# decision, never re-enable silently. `graph-only` here means: the key is not reachable by one grep on the
# seed; every class-C chain is text-derivable by construction (this script is text-only); what the arms
# measure is hops, over-selection and tokens read.
#
# Text only. Every link is a regex over tracked files; nothing here opens shonkor.db or any graph.
#
#   rendering  = tracked .yml carrying the Controller field (id e64ad073-dfcc-4d20-8c0b-fe5aa6226cd7);
#                fields read: Controller ("Namespace.Type, Assembly"), Datasource Template (item path).
#   controller = the simple type name of the Controller value, resolved to tracked .cs files declaring
#                `class <Name>`; resolvable = exactly one file.
#   view       = the single distinct `View("~/Views/…")` / `PartialView("~/Views/…")` in the controller .cs,
#                resolved to exactly one tracked .cshtml whose path ends with /Views/….
#   template   = tracked .yml with Template "ab86861a-6030-46c5-b394-e8f99e8b87db", matched to a rendering's
#                Datasource Template by exact item Path; its ID is matched (case-insensitively) to a
#                `TemplateIdString = "<id>"` constant, whose enclosing `<X>Constants` class must be named by
#                exactly one `[SitecoreType(TemplateId = <X>Constants.TemplateIdString)]` CLASS (not interface).
#   model      = that class.
#
#   Anonymisation: tokens are assigned per kind over the WHOLE population (all renderings, all resolvable
#   controllers, all views, all templates, all Glass-attributed model classes), ordered by the `/`-normalised
#   path with LC_ALL=C (ordinal), numbered 1..n zero-padded to the population width (min 2). A token is
#   therefore a function of the corpus revision alone.
#
#   Selection (first N per type, in population order — no hand picking):
#     C1 rendering → controller → view: controller resolvable, exactly one view that resolves.
#        plausibility (graph-only): the rendering item name, verbatim and with spaces removed, does not occur
#        in the controller .cs (case-insensitive) — grep on the seed cannot reach the key.
#        Since #502 the same test is applied to the view .cshtml (the second half of the key), and the chain
#        is also rejected when the view basename (without .cshtml, case-insensitive) equals the rendering
#        name in either form — Sitecore convention often names the view after the rendering, so a filename
#        search on the seed would reach half the key. Both are recorded as yes/no flags in the rule text.
#        distinct keys: a controller + view pair that is already the key of an earlier C1 task is skipped.
#        key.files = [Controller-nn, View-nn]; key.symbols = [Controller-nn].
#     C2 controller → renderings (ledger only, no tasks — see above): controller resolvable, named by >= 2
#        renderings. Reported: how many tracked .yml mention the controller simple name at all (an arm's
#        grep ceiling) and how many controllers over-select (yml mentions > renderings).
#     C3 rendering → datasource template → model: the chain above resolves uniquely at every hop.
#        plausibility: the rendering item name (both forms) does not occur in the model .cs.
#        distinct keys: a model that is already the key of an earlier C3 task is skipped (several renderings
#        share one datasource template; measuring the same chain three times would measure one task).
#        key.files = key.symbols = [Model-nn].
#     Should C3 yield fewer than <c3> chains, the shortfall is filled from C1 and reported.
#
#   denyWords (mapping file only): the distinct root namespace segments of the resolvable controllers (the
#   customer's own code; vendor controllers have no .cs in the tree) plus the basenames of the tracked .sln
#   files, lower-cased. Their SHA-256 digests live in the deny-list test (Ap6CorpusTests.DenyWordHashes).
set -euo pipefail

ROOT="${1:?corpus root}"; N1="${2:-5}"; N2="${3:-0}"; N3="${4:-5}"
ROOT="${ROOT//\\//}"; ROOT="${ROOT%/}"
[ "$N2" -eq 0 ] || { echo "C2 is not generated (#491) — <c2> must be 0, was $N2" >&2; exit 1; }
CONTROLLER_FIELD='e64ad073-dfcc-4d20-8c0b-fe5aa6226cd7'
TEMPLATE_TEMPLATE='ab86861a-6030-46c5-b394-e8f99e8b87db'
REV=$(git -C "$ROOT" rev-parse HEAD)
# `git grep` reads the working tree; the tasks claim $REV. Only the file kinds this script reads matter.
dirty=$(git -C "$ROOT" diff --name-only HEAD -- '*.yml' '*.cs' '*.cshtml' '*.sln' | wc -l | tr -d ' ')
[ "$dirty" -eq 0 ] || { echo "corpus working tree has $dirty modified tracked .yml/.cs/.cshtml/.sln file(s) — keys would not match $REV; stash or commit first" >&2; exit 1; }

json_str() { local s=${1//\\/\\\\}; s=${s//\"/\\\"}; printf '"%s"' "$s"; }
json_arr() { local out="" x; for x in "$@"; do out+="${out:+, }$(json_str "$x")"; done; printf '[%s]' "$out"; }
lower() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }
ls_tracked() { git -C "$ROOT" ls-files -- "$@" | sed -E 's/\r$//' | LC_ALL=C sort; }
strip_cr() { sed -E 's/\r$//; s/^\xEF\xBB\xBF//'; }
width() { local n=${#1}; [ "$n" -lt 2 ] && n=2; printf '%s' "$n"; }

# ---------- populations ----------
declare -A R_NAME R_ID R_CTRL R_DST R_TOKEN                # by rendering path
mapfile -t R_PATHS < <(git -C "$ROOT" grep -lF -- "$CONTROLLER_FIELD" -- '*.yml' | sed -E 's/\r$//' | LC_ALL=C sort)
RW=$(width "${#R_PATHS[@]}")
i=0
for p in "${R_PATHS[@]}"; do
  i=$((i+1)); R_TOKEN[$p]=$(printf 'Rendering-%0*d' "$RW" "$i")
  n=${p##*/}; R_NAME[$p]=${n%.yml}
  IFS=$'\t' read -r id ctrl dst < <(strip_cr < "$ROOT/$p" | awk '
    /^ID: /      && !id  { gsub(/["\r]/, "", $2); id=$2 }
    /^ *Hint: /  { hint=$0; sub(/^ *Hint: */, "", hint); sub(/ *$/, "", hint); next }
    /^ *Value: / { v=$0; sub(/^ *Value: */, "", v); sub(/ *$/, "", v)
                   if (hint=="Controller") ctrl=v; else if (hint=="Datasource Template") dst=v; hint="" }
    END { printf "%s\t%s\t%s\n", id, ctrl, dst }')
  R_ID[$p]=$id; R_CTRL[$p]=$ctrl; R_DST[$p]=$dst
done
echo "renderings with a Controller field: ${#R_PATHS[@]}" >&2

# class name → tracked .cs files declaring it (one pass over the tree)
declare -A CLASS_FILES
while IFS= read -r line; do
  path=${line%%:*}; name=$(printf '%s' "$line" | sed -E 's/^.*class +([A-Za-z_][A-Za-z0-9_]*).*/\1/')
  case " ${CLASS_FILES[$name]:-} " in *" $path "*) ;; *) CLASS_FILES[$name]="${CLASS_FILES[$name]:-} $path" ;; esac
done < <(git -C "$ROOT" grep -E '^\s*(public|internal|private|protected)?\s*(static\s+|abstract\s+|sealed\s+|partial\s+)*class\s+[A-Za-z_][A-Za-z0-9_]*' -- '*.cs' | sed -E 's/\r$//')

# controllers: distinct full names; resolvable ones get a token ordered by their .cs path
declare -A C_FULL C_PATH C_TOKEN C_RENDERINGS   # by simple name
declare -A NS_ROOTS
for p in "${R_PATHS[@]}"; do
  full=${R_CTRL[$p]%%,*}; full=${full// /}; [ -z "$full" ] && continue
  simple=${full##*.}
  C_FULL[$simple]=$full
  C_RENDERINGS[$simple]="${C_RENDERINGS[$simple]:+${C_RENDERINGS[$simple]}$'\n'}$p"   # newline-separated: paths carry spaces
done
unresolved=0
for simple in "${!C_FULL[@]}"; do
  read -r -a files <<< "${CLASS_FILES[$simple]:-}"
  if [ "${#files[@]}" -eq 1 ]; then
    C_PATH[$simple]=${files[0]}
    NS_ROOTS[$(lower "${C_FULL[$simple]%%.*}")]=1   # only the customer's own code — vendor controllers have no .cs here
  else unresolved=$((unresolved+1)); fi
done
mapfile -t C_ORDER < <(for s in "${!C_PATH[@]}"; do printf '%s\t%s\n' "${C_PATH[$s]}" "$s"; done | LC_ALL=C sort | cut -f2)
CW=$(width "${#C_ORDER[@]}"); i=0
for s in "${C_ORDER[@]}"; do i=$((i+1)); C_TOKEN[$s]=$(printf 'Controller-%0*d' "$CW" "$i"); done
echo "distinct controllers: ${#C_FULL[@]}, resolvable to exactly one .cs: ${#C_ORDER[@]}, unresolvable: $unresolved" >&2

# views: every tracked .cshtml
declare -A V_TOKEN
mapfile -t V_PATHS < <(ls_tracked '*.cshtml')
VW=$(width "${#V_PATHS[@]}"); i=0
for p in "${V_PATHS[@]}"; do i=$((i+1)); V_TOKEN[$p]=$(printf 'View-%0*d' "$VW" "$i"); done
resolve_view() { # ~/Views/x → exactly one tracked path, else empty
  local rel=${1#\~/}; local hits=(); local p
  for p in "${V_PATHS[@]}"; do case "$p" in *"/$rel") hits+=("$p") ;; esac; done
  [ "${#hits[@]}" -eq 1 ] && printf '%s' "${hits[0]}" || true
}

# templates: tracked .yml of the Template template, by item Path
declare -A T_TOKEN T_ID T_NAME T_PATH_OF_ITEM   # T_* by file path; T_PATH_OF_ITEM by item path
mapfile -t T_PATHS < <(git -C "$ROOT" grep -lE -- "^Template: \"$TEMPLATE_TEMPLATE\"" -- '*.yml' | sed -E 's/\r$//' | LC_ALL=C sort)
TW=$(width "${#T_PATHS[@]}"); i=0
for p in "${T_PATHS[@]}"; do
  i=$((i+1)); T_TOKEN[$p]=$(printf 'Template-%0*d' "$TW" "$i")
  IFS=$'\t' read -r id item < <(strip_cr < "$ROOT/$p" | awk '/^ID: / && !id { gsub(/"/, "", $2); id=$2 } /^Path: / && !ip { ip=$0; sub(/^Path: */, "", ip) } END { printf "%s\t%s\n", id, ip }')
  T_ID[$p]=$(lower "$id"); n=${p##*/}; T_NAME[$p]=${n%.yml}
  T_PATH_OF_ITEM[$item]="${T_PATH_OF_ITEM[$item]:+${T_PATH_OF_ITEM[$item]}$'\n'}$p"
done
echo "template items: ${#T_PATHS[@]}" >&2

# constants: template id (lower) → "<X>Constants" class names; models: constants class → model keys
# A model key is "<path>::<class>" (path `/`-normalised by git): tokens are a function of the path, as for
# every other kind, so same-named classes in different files never share a token.
declare -A CONST_OF_ID M_CLASSES_OF_CONST M_PATH M_NAME M_TOKEN M_FULL   # M_* by model key
while IFS=$'\t' read -r id cls; do CONST_OF_ID[$id]="${CONST_OF_ID[$id]:-} $cls"; done < <(
  git -C "$ROOT" grep -l -E 'TemplateIdString = "' -- '*.cs' | sed -E 's/\r$//' | while IFS= read -r f; do
    strip_cr < "$ROOT/$f" | awk '
      /class +[A-Za-z_][A-Za-z0-9_]*/ { c=$0; sub(/^.*class +/, "", c); sub(/[^A-Za-z0-9_].*$/, "", c) }
      /TemplateIdString = "/ { v=$0; sub(/^.*TemplateIdString = "/, "", v); sub(/".*$/, "", v); printf "%s\t%s\n", tolower(v), c }'
  done)
mapfile -t M_ORDER < <(
  git -C "$ROOT" grep -l -E '^\s*\[SitecoreType\(' -- '*.cs' | sed -E 's/\r$//' | LC_ALL=C sort | while IFS= read -r f; do
    strip_cr < "$ROOT/$f" | awk -v f="$f" '
      /^[ \t]*namespace / { ns=$2; sub(/[ {;].*$/, "", ns) }
      /^[ \t]*\[SitecoreType\(/ { a=$0; c=""; if (match(a, /TemplateId *= *[A-Za-z_][A-Za-z0-9_.]*Constants\.TemplateIdString/)) { c=substr(a, RSTART, RLENGTH); sub(/^TemplateId *= */, "", c); sub(/\.TemplateIdString$/, "", c); sub(/^.*\./, "", c) } pending=c; next }
      pending!="" && /^[ \t]*public +(partial +)?class +[A-Za-z_][A-Za-z0-9_]*/ { m=$0; sub(/^.*class +/, "", m); sub(/[^A-Za-z0-9_].*$/, "", m); printf "%s\t%d\t%s\t%s\t%s\n", f, NR, m, pending, ns; pending="" ; next }
      /^[ \t]*public +(partial +)?(class|interface) / { pending="" }'
  done | LC_ALL=C sort)
MW=$(width "${#M_ORDER[@]}"); i=0
for rec in "${M_ORDER[@]}"; do
  IFS=$'\t' read -r f ln m c ns <<< "$rec"
  k="$f::$m"
  i=$((i+1)); M_TOKEN[$k]=$(printf 'Model-%0*d' "$MW" "$i"); M_PATH[$k]=$f; M_NAME[$k]=$m; M_FULL[$k]="$ns.$m"
  M_CLASSES_OF_CONST[$c]="${M_CLASSES_OF_CONST[$c]:+${M_CLASSES_OF_CONST[$c]}$'\n'}$k"
done
echo "Glass model classes: ${#M_ORDER[@]}" >&2

# ---------- plausibility helpers ----------
name_in_file() { # <item name> <file>: 0 when the name (verbatim or without spaces) occurs, case-insensitively
  local n=$1 f=$ROOT/$2
  grep -qiF -- "$n" "$f" && return 0
  grep -qiF -- "${n// /}" "$f" && return 0
  return 1
}

# ---------- selection ----------
declare -a TASKS=(); declare -A USED
emit() { # id query files-json symbols-json rule
  TASKS+=("$(printf '  { "schemaVersion": 1, "id": %s, "class": "C", "corpus": "Corpus-A",\n    "query": %s,\n    "key": { "files": %s, "symbols": %s },\n    "keySource": { "method": "unicorn-yml", "ref": %s, "rule": %s },\n    "seedInKey": false, "expectation": "graph-only" }' \
    "$(json_str "$1")" "$(json_str "$2")" "$3" "$4" "$(json_str "$REV")" "$(json_str "$5")")")
}
use() { local k; for k in "$@"; do USED[$k]=1; done; }

# C1
n=0; seen=0; rej=0; rej_view=0; rej_base=0; declare -A USED_C1_KEY
c1_reject() { echo "C1 reject ${R_TOKEN[$1]}: $2" >&2; rej=$((rej+1)); }
view_basename_is() { # <item name> <view path>: 0 when the .cshtml basename equals the name (verbatim or without spaces), case-insensitively
  local b=${2##*/}; b=$(lower "${b%.cshtml}")
  [ "$b" = "$(lower "$1")" ] || [ "$b" = "$(lower "${1// /}")" ]
}
for p in "${R_PATHS[@]}"; do
  [ "$n" -ge "$N1" ] && break
  full=${R_CTRL[$p]%%,*}; full=${full// /}; simple=${full##*.}
  [ -z "$simple" ] && continue
  seen=$((seen+1))
  cp=${C_PATH[$simple]:-}; [ -z "$cp" ] && { c1_reject "$p" "controller not resolvable to exactly one .cs"; continue; }
  mapfile -t views < <(grep -oE '(Partial)?View\("~/Views/[^"]+"' "$ROOT/$cp" | sed -E 's/^.*\("//; s/"$//' | sort -u)
  [ "${#views[@]}" -eq 1 ] || { c1_reject "$p" "${#views[@]} distinct views in the controller"; continue; }
  vp=$(resolve_view "${views[0]}"); [ -n "$vp" ] || { c1_reject "$p" "view does not resolve to exactly one .cshtml"; continue; }
  [ -n "${USED_C1_KEY[$cp::$vp]:-}" ] && { c1_reject "$p" "controller + view already the key of an earlier C1 task"; continue; }
  name_in_file "${R_NAME[$p]}" "$cp" && { c1_reject "$p" "rendering name occurs in the controller .cs"; continue; }
  # #502: the view is the other half of the key — the same test there, plus the view's own file name.
  name_in_file "${R_NAME[$p]}" "$vp" && { c1_reject "$p" "rendering name occurs in the view .cshtml"; rej_view=$((rej_view+1)); continue; }
  view_basename_is "${R_NAME[$p]}" "$vp" && { c1_reject "$p" "view basename equals the rendering name"; rej_base=$((rej_base+1)); continue; }
  USED_C1_KEY[$cp::$vp]=1
  n=$((n+1))
  emit "$(printf 'C1-%02d' "$n")" "Which C# class and which Razor view render \`${R_TOKEN[$p]}\`?" \
    "$(json_arr "${C_TOKEN[$simple]}" "${V_TOKEN[$vp]}")" "$(json_arr "${C_TOKEN[$simple]}")" \
    "rendering item → Controller field type → its single .cs → its single View(\"~/Views/…\") → .cshtml; rendering item name (verbatim and without spaces) absent from the controller .cs: yes; rendering item name absent from the view .cshtml: yes; view basename differs from rendering name: yes"
  use "${R_TOKEN[$p]}" "${C_TOKEN[$simple]}" "${V_TOKEN[$vp]}"
  echo "C1 accept ${R_TOKEN[$p]} → ${C_TOKEN[$simple]} + ${V_TOKEN[$vp]}" >&2
done
echo "C1: $n accepted of $seen renderings walked ($rej rejected)" >&2
echo "C1 view rule (#502): rejected because the rendering name occurs in the view .cshtml: $rej_view, because the view basename equals the rendering name: $rej_base" >&2
c1=$n

# C2 — ledger only (#491): no task is emitted. A controller over-selects when more tracked .yml mention its
# simple name than renderings execute it; only then would a text arm have to disambiguate.
seen=0; over=0
for s in "${C_ORDER[@]}"; do
  mapfile -t rs <<< "${C_RENDERINGS[$s]}"
  [ "${#rs[@]}" -ge 2 ] || continue
  seen=$((seen+1))
  yml_hits=$(git -C "$ROOT" grep -lw -- "$s" -- '*.yml' | wc -l | tr -d ' ')
  flag=""; [ "$yml_hits" -gt "${#rs[@]}" ] && { over=$((over+1)); flag=" — over-selecting"; }
  echo "C2 candidate ${C_TOKEN[$s]}: ${#rs[@]} renderings, yml mentions: $yml_hits$flag" >&2
done
echo "C2 (ledger only): controllers with >= 2 renderings: $seen, over-selecting (yml mentions > renderings): $over" >&2
[ "$over" -eq 0 ] || echo "C2 NOTE: $over over-selecting controller(s) — C2 is admissible under the #491 rule; reopen the decision before generating C2 tasks" >&2

# C3
n=0; seen=0; rej=0; declare -A USED_MODEL
c3_reject() { echo "C3 reject ${R_TOKEN[$1]}: $2" >&2; rej=$((rej+1)); }
for p in "${R_PATHS[@]}"; do
  [ "$n" -ge "$N3" ] && break
  dst=${R_DST[$p]}; [ -z "$dst" ] && continue
  seen=$((seen+1))
  tps=(); [ -n "${T_PATH_OF_ITEM[$dst]:-}" ] && mapfile -t tps <<< "${T_PATH_OF_ITEM[$dst]}"
  [ "${#tps[@]}" -eq 1 ] || { c3_reject "$p" "datasource template path matches ${#tps[@]} template items"; continue; }
  tp=${tps[0]}
  read -r -a consts <<< "${CONST_OF_ID[${T_ID[$tp]}]:-}"
  [ "${#consts[@]}" -eq 1 ] || { c3_reject "$p" "template id matches ${#consts[@]} TemplateIdString constants"; continue; }
  models=(); [ -n "${M_CLASSES_OF_CONST[${consts[0]}]:-}" ] && mapfile -t models <<< "${M_CLASSES_OF_CONST[${consts[0]}]}"
  [ "${#models[@]}" -eq 1 ] || { c3_reject "$p" "constants class named by ${#models[@]} model classes"; continue; }
  m=${models[0]}
  [ -n "${USED_MODEL[$m]:-}" ] && { c3_reject "$p" "model already the key of an earlier C3 task"; continue; }
  name_in_file "${R_NAME[$p]}" "${M_PATH[$m]}" && { c3_reject "$p" "rendering name occurs in the model .cs"; continue; }
  USED_MODEL[$m]=1
  same="no"; [ "$(lower "${T_NAME[$tp]// /}")" = "$(lower "${M_NAME[$m]}")" ] && same="yes"
  n=$((n+1))
  emit "$(printf 'C3-%02d' "$n")" "Which C# model class maps the datasource template of \`${R_TOKEN[$p]}\`?" \
    "$(json_arr "${M_TOKEN[$m]}")" "$(json_arr "${M_TOKEN[$m]}")" \
    "rendering item → Datasource Template path → template item (exact Path) → its ID → the single TemplateIdString constant → the single [SitecoreType(TemplateId = …Constants.TemplateIdString)] class; rendering item name absent from the model .cs: yes; template item name equals model class name: $same"
  use "${R_TOKEN[$p]}" "${T_TOKEN[$tp]}" "${M_TOKEN[$m]}"
  echo "C3 accept ${R_TOKEN[$p]} → ${T_TOKEN[$tp]} → ${M_TOKEN[$m]} (template name = class name: $same)" >&2
done
echo "C3: $n accepted of $seen renderings with a datasource template ($rej rejected)" >&2
c3=$n
if [ "$c3" -lt "$N3" ]; then echo "C3 shortfall: $((N3-c3)) — fill from C1 by rerunning with a larger N1 (not done automatically)" >&2; fi
[ $((c1+c3)) -eq $((N1+N3)) ] || { echo "expected $((N1+N3)) class C tasks, produced $((c1+c3))" >&2; exit 1; }

# ---------- mapping (out of repo) ----------
mapfile -t DENY < <({ for w in "${!NS_ROOTS[@]}"; do printf '%s\n' "$w"; done; ls_tracked '*.sln' | sed -E 's#^.*/##; s/\.sln$//' | tr '[:upper:]' '[:lower:]'; } | LC_ALL=C sort -u)
mkdir -p "$ROOT/bench"
grep -qxF 'bench/' "$ROOT/.git/info/exclude" 2>/dev/null || printf 'bench/\n' >> "$ROOT/.git/info/exclude"
{
  printf '{\n  "schemaVersion": 1,\n  "corpusRoot": %s,\n  "corpusRevision": %s,\n  "generatedBy": "bench/golden/ap6/scripts/keys-c.sh",\n  "denyWords": %s,\n  "entries": {\n' \
    "$(json_str "$ROOT")" "$(json_str "$REV")" "$(json_arr "${DENY[@]}")"
  first=1
  # No subshells here: ~1 500 entries × 5 fields would be minutes of process spawns on Windows.
  q() { local s=${2//\\/\\\\}; s=${s//\"/\\\"}; printf -v "$1" '"%s"' "$s"; }
  entry() { # token kind k1 v1 [k2 v2 …]
    local tok kind body="" k v; q tok "$1"; q kind "$2"; shift 2
    while [ $# -ge 2 ]; do q k "$1"; q v "$2"; body+=", $k: $v"; shift 2; done
    [ $first -eq 1 ] || printf ',\n'; first=0
    printf '    %s: { "kind": %s%s }' "$tok" "$kind" "$body"
  }
  for p in "${R_PATHS[@]}"; do entry "${R_TOKEN[$p]}" rendering path "$p" name "${R_NAME[$p]}" id "{${R_ID[$p]}}" controller "${R_CTRL[$p]}"; done
  for s in "${C_ORDER[@]}"; do entry "${C_TOKEN[$s]}" controller path "${C_PATH[$s]}" name "$s" fullName "${C_FULL[$s]}"; done
  for p in "${V_PATHS[@]}"; do entry "${V_TOKEN[$p]}" view path "$p" name "${p##*/}"; done
  for p in "${T_PATHS[@]}"; do entry "${T_TOKEN[$p]}" template path "$p" name "${T_NAME[$p]}" id "{${T_ID[$p]}}"; done
  for rec in "${M_ORDER[@]}"; do IFS=$'\t' read -r f ln m c ns <<< "$rec"; k="$f::$m"; entry "${M_TOKEN[$k]}" model path "$f" name "$m" fullName "${M_FULL[$k]}"; done
  printf '\n  }\n}\n'
} > "$ROOT/bench/ap6-mapping.json"
echo "mapping: $ROOT/bench/ap6-mapping.json (${#DENY[@]} deny words, $((${#R_PATHS[@]}+${#C_ORDER[@]}+${#V_PATHS[@]}+${#T_PATHS[@]}+${#M_ORDER[@]})) entries)" >&2

printf '[\n'; first=1
for t in "${TASKS[@]}"; do [ $first -eq 1 ] || printf ',\n'; first=0; printf '%s' "$t"; done
printf '\n]\n'
