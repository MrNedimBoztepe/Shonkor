#!/usr/bin/env bash
# run.sh — AP6 part 1 two-arm harness driver (#473): every task of bench/golden/ap6/tasks.json, answered by
# the same agent and model twice — once with only the shonkor MCP tools, once with only ripgrep + file
# reads — three runs each, recorded and scored.
#
# Usage:  bench/golden/ap6/run.sh [<run-dir>] [--smoke] [--class A|B|C] [--model <id>] [--dry-run]
#         <run-dir>  where streams, prompts and configs go. MUST be outside both repositories: class-C prompts
#                    and streams carry customer names. Default: $AP6_RUNS_ROOT/ap6/<timestamp>
#                    (AP6_RUNS_ROOT defaults to C:/Projects/shonkor-bench-runs). Re-running with the same
#                    <run-dir> resumes: runs that already have a stream.jsonl are skipped.
#         --smoke    A-01, B-01, C1-01 × both arms × 1 run — the connection and tool-set check before money
#                    is spent on the full set. Read the report's "Arm violations" first.
#         --class X  only that class (e.g. A/B while the corpus graph is not yet re-indexed).
#         --model    override MODEL below for this run (recorded in env.json).
#         --dry-run  print every claude command line and the generated MCP configs, run nothing. Env-gate
#                    failures are printed as "would abort" instead of aborting, so the plan is visible on a
#                    machine without an API key.
#
# Steps: 1. env gate  2. shonkor-bench --ap6-plan (preconditions, prompts)  3. the runs  4. shonkor-bench --ap6.
#
# The three pieces the driver does NOT do itself, because a mistake there costs a scored run (procedure,
# not automation — see README.md next to this file):
#   (1) build:            dotnet build Shonkor.slnx -c Release
#   (2) plugins:          src/Shonkor.CLI/bin/Release/net10.0/shonkor.exe plugin verify .   (exit 0, or
#                         `plugin install <zip>` per stale plugin and verify again)
#   (3) graphs:           re-index the corpus AND Brain with the verified plugins at the revision the keys
#                         name (`shonkor index . --force` in each root, SHONKOR_WORKSPACE=<brain-root>),
#                         after a copy of the previous databases. The gate below only CHECKS (2) and the
#                         indexedRevision equality (through --ap6-plan); it never re-indexes.
#
# Why claude -p --bare: it authenticates with ANTHROPIC_API_KEY only (never the OAuth login), loads no
# user settings, hooks or plugins, and with --strict-mcp-config sees exactly the MCP config given here.
# Why the version pin: before 2.1.221 `-p` with --mcp-config did not wait for the stdio server before turn
# 1, so the MCP arm could start with shonkor "pending" — the connection gate would then discard the run.
set -euo pipefail

# ---------- limits (#505) — change here before a run; every value is recorded in env.json and printed beside each table ----------
MODEL="claude-opus-5"        # pinned model id: the comparison is between arms, never between models
EFFORT="high"                # the reasoning budget the arm gets; the same for both arms
MAX_TURNS=25                 # per run: enough for a 5-hop chain with retries, too few to brute-force the tree
MAX_USD="2.00"               # per run (claude's own --max-budget-usd): a runaway run stops itself
TOTAL_USD="250"              # per run set, summed here across runs: 60 runs × 2.00 would already exceed it, so a
                             # set that hits it is a set whose limits are wrong, not one to keep paying for
RUNS=3                       # runs per task and arm — the majority of 3 is the ratified correctness (#473)
MIN_CLAUDE="2.1.263"         # oldest claude CLI whose -p waits for the stdio MCP server before turn 1 (+ margin)
# ------------------------------------------------------------------------------------------------------------

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BRAIN="$(cd "$HERE/../../.." && (pwd -W 2>/dev/null || pwd))"
BRAIN="${BRAIN//\\//}"
BENCH="$BRAIN/src/Shonkor.Bench/bin/Release/net10.0/shonkor-bench"
SHONKOR="$BRAIN/src/Shonkor.CLI/bin/Release/net10.0/shonkor"
[ -x "$BENCH.exe" ] && BENCH="$BENCH.exe"
[ -x "$SHONKOR.exe" ] && SHONKOR="$SHONKOR.exe"
TASKS="$BRAIN/bench/golden/ap6/tasks.json"
SCHEMA="$BRAIN/bench/golden/ap6/answer-schema.json"

RUN_DIR=""; SMOKE=0; CLASS=""; DRY=0
while [ $# -gt 0 ]; do
  case "$1" in
    --smoke) SMOKE=1 ;;
    --class) CLASS="${2:?--class needs A|B|C}"; shift ;;
    --model) MODEL="${2:?--model needs an id}"; shift ;;
    --dry-run) DRY=1 ;;
    --*) echo "unknown option $1" >&2; exit 2 ;;
    *) RUN_DIR="$1" ;;
  esac
  shift
done
[ -n "$RUN_DIR" ] || RUN_DIR="${AP6_RUNS_ROOT:-C:/Projects/shonkor-bench-runs}/ap6/$(date -u +%Y%m%dT%H%M%SZ)"
RUN_DIR="${RUN_DIR//\\//}"
[ "$SMOKE" -eq 1 ] && RUNS=1
SMOKE_IDS=" A-01 B-01 C1-01 "

# A run directory inside either repository would put customer names under version control's nose.
case "$RUN_DIR" in "$BRAIN"/*) echo "run dir must be outside the Brain repository: $RUN_DIR" >&2; exit 2 ;; esac
mkdir -p "$RUN_DIR"

# ---------- helpers ----------
FAILED=0
gate_fail() { # message — abort, or in --dry-run report and continue
  if [ "$DRY" -eq 1 ]; then echo "[DRY-RUN] would abort: $*"; FAILED=1; else echo "abort: $*" >&2; exit 1; fi
}
json_str() { local s=${1//\\/\\\\}; s=${s//\"/\\\"}; s=${s//$'\r'/}; s=${s//$'\n'/\\n}; s=${s//$'\t'/\\t}; printf '"%s"' "$s"; }
ver_ge() { [ "$(printf '%s\n%s\n' "$2" "$1" | sort -V | head -n1)" = "$2" ]; }  # $1 >= $2
sha256_of() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

# ---------- 1. env gate ----------
export ENABLE_TOOL_SEARCH=false                 # deferred tool loading would hide tools from init.tools — the arm check reads that list
export CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1  # no telemetry / update pings during a measured run
export DISABLE_AUTOUPDATER=1                    # the version is pinned above; an update mid-set would change the harness

CLAUDE_VERSION="$(claude --version 2>/dev/null | head -n1 | sed -E 's/^([0-9]+\.[0-9]+\.[0-9]+).*/\1/')" || CLAUDE_VERSION=""
[ -n "$CLAUDE_VERSION" ] || gate_fail "claude CLI not found on PATH"
if [ -n "$CLAUDE_VERSION" ] && ! ver_ge "$CLAUDE_VERSION" "$MIN_CLAUDE"; then
  gate_fail "claude $CLAUDE_VERSION < $MIN_CLAUDE — update the CLI; older -p does not wait for the stdio MCP server (see header)"
fi
RG_BIN="$(type -P rg || true)"
[ -n "$RG_BIN" ] || gate_fail "rg (ripgrep) not found as a binary on PATH — the pilot silently fell back to grep; this harness refuses to"
RG_VERSION="$( [ -n "$RG_BIN" ] && "$RG_BIN" --version | head -n1 || echo "")"
[ -n "${ANTHROPIC_API_KEY:-}" ] || gate_fail "ANTHROPIC_API_KEY is not set — claude -p --bare authenticates with the API key only (never the OAuth login); export it in this shell"
[ -x "$BENCH" ] || gate_fail "shonkor-bench not built at $BENCH — dotnet build Shonkor.slnx -c Release"
[ -x "$SHONKOR" ] || gate_fail "shonkor CLI not built at $SHONKOR — dotnet build Shonkor.slnx -c Release (the global tool is NOT used: it is stale)"
SHONKOR_DLL="$(dirname "$SHONKOR")/shonkor.dll"
SHONKOR_SHA="$( [ -f "$SHONKOR_DLL" ] && sha256_of "$SHONKOR_DLL" || echo "")"

PLUGIN_VERIFY_EXIT=0; PLUGIN_VERIFY_OUT=""
if [ -x "$SHONKOR" ]; then
  PLUGIN_VERIFY_OUT="$(cd "$BRAIN" && "$SHONKOR" plugin verify . 2>&1)" || PLUGIN_VERIFY_EXIT=$?
  [ "$PLUGIN_VERIFY_EXIT" -eq 0 ] || gate_fail "shonkor plugin verify exited $PLUGIN_VERIFY_EXIT — install the freshly built plugins and verify again (README.md step 2)"
fi
BRAIN_HEAD="$(git -C "$BRAIN" rev-parse HEAD 2>/dev/null || echo "")"

# ---------- 2. plan (preconditions live in shonkor-bench, not here) ----------
PLAN_ARGS=(--ap6-plan "$TASKS" --workspace "$BRAIN" --out "$RUN_DIR")
[ -n "$CLASS" ] && PLAN_ARGS+=(--class "$CLASS")
# A dry run still wants the command lines on a machine whose graphs are stale: the plan prints the failures
# and writes the files anyway. A real run never passes this flag.
[ "$DRY" -eq 1 ] && PLAN_ARGS+=(--ignore-preconditions)
if [ -x "$BENCH" ]; then
  if ! "$BENCH" "${PLAN_ARGS[@]}"; then gate_fail "--ap6-plan reported precondition failures (above)"; fi
else
  gate_fail "cannot plan without shonkor-bench"
fi
CORPUS_ROOT=""; CORPUS_PROJECT="Corpus-A"; CORPUS_DB=""; CORPUS_REVISION=""; BRAIN_PROJECT="Shonkor"; BRAIN_DB=""
if [ -f "$RUN_DIR/plan.env" ]; then
  # KEY=VALUE lines written by --ap6-plan; values are paths/names without quotes.
  while IFS='=' read -r k v; do case "$k" in BRAIN_PROJECT|BRAIN_DB|CORPUS_ROOT|CORPUS_PROJECT|CORPUS_DB|CORPUS_REVISION) printf -v "$k" '%s' "$v" ;; esac; done < "$RUN_DIR/plan.env"
fi
CORPUS_HEAD="$( [ -n "$CORPUS_ROOT" ] && git -C "$CORPUS_ROOT" rev-parse HEAD 2>/dev/null || echo "")"

# ---------- MCP configs into the run dir (absolute forward-slash paths; the templates hold placeholders only) ----------
sed -e "s#__SHONKOR_EXE__#$SHONKOR#g" -e "s#__BRAIN_ROOT__#$BRAIN#g" -e "s#__BRAIN_PROJECT__#$BRAIN_PROJECT#g" \
  "$HERE/mcp-brain.template.json" > "$RUN_DIR/mcp-brain.json"
sed -e "s#__SHONKOR_EXE__#$SHONKOR#g" -e "s#__BRAIN_ROOT__#$BRAIN#g" -e "s#__CORPUS_PROJECT__#$CORPUS_PROJECT#g" \
  "$HERE/mcp-corpus.template.json" > "$RUN_DIR/mcp-corpus.json"
cp "$HERE/mcp-none.json" "$RUN_DIR/mcp-none.json"

# ---------- env.json ----------
{
  printf '{\n'
  printf '  "schemaVersion": 1,\n'
  printf '  "generatedAt": %s,\n' "$(json_str "$(date -u +%Y-%m-%dT%H:%M:%SZ)")"
  printf '  "dryRun": %s,\n' "$([ "$DRY" -eq 1 ] && echo true || echo false)"
  printf '  "smoke": %s,\n' "$([ "$SMOKE" -eq 1 ] && echo true || echo false)"
  printf '  "classFilter": %s,\n' "$(json_str "$CLASS")"
  printf '  "claudeVersion": %s,\n' "$(json_str "$CLAUDE_VERSION")"
  printf '  "minClaudeVersion": %s,\n' "$(json_str "$MIN_CLAUDE")"
  printf '  "rgVersion": %s,\n' "$(json_str "$RG_VERSION")"
  printf '  "rgBinary": %s,\n' "$(json_str "$RG_BIN")"
  printf '  "model": %s,\n' "$(json_str "$MODEL")"
  printf '  "effort": %s,\n' "$(json_str "$EFFORT")"
  printf '  "maxTurns": %s,\n' "$MAX_TURNS"
  printf '  "maxUsdPerRun": %s,\n' "$MAX_USD"
  printf '  "runSetUsdCap": %s,\n' "$TOTAL_USD"
  printf '  "runsPerArm": %s,\n' "$RUNS"
  printf '  "shonkorExe": %s,\n' "$(json_str "$SHONKOR")"
  printf '  "shonkorDllSha256": %s,\n' "$(json_str "$SHONKOR_SHA")"
  printf '  "brainHead": %s,\n' "$(json_str "$BRAIN_HEAD")"
  printf '  "corpusHead": %s,\n' "$(json_str "$CORPUS_HEAD")"
  printf '  "corpusRevision": %s,\n' "$(json_str "$CORPUS_REVISION")"
  printf '  "pluginVerifyExit": %s,\n' "$PLUGIN_VERIFY_EXIT"
  printf '  "pluginVerifyOutput": %s,\n' "$(json_str "$PLUGIN_VERIFY_OUT")"
  printf '  "envFlags": { "ENABLE_TOOL_SEARCH": "false", "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC": "1", "DISABLE_AUTOUPDATER": "1" }\n'
  printf '}\n'
} > "$RUN_DIR/env.json"

if [ "$DRY" -eq 1 ]; then
  echo "=== dry run: env.json ==="; cat "$RUN_DIR/env.json"
  echo "=== dry run: MCP configs ==="; for f in mcp-brain.json mcp-corpus.json mcp-none.json; do echo "--- $f"; cat "$RUN_DIR/$f"; done
fi
[ "$FAILED" -eq 0 ] || echo "[DRY-RUN] one or more gate checks would abort a real run (see above)."

# ---------- 3. the runs ----------
SCHEMA_JSON="$(cat "$SCHEMA")"
[ -f "$RUN_DIR/plan.tsv" ] || { echo "no plan.tsv in $RUN_DIR — nothing to run" >&2; exit 1; }
total_cost="0"
while IFS=$'\t' read -r id cls corpus prompt; do
  [ -n "$id" ] || continue
  if [ "$SMOKE" -eq 1 ]; then case "$SMOKE_IDS" in *" $id "*) ;; *) continue ;; esac; fi
  if [ "$cls" = "C" ]; then cwd="$CORPUS_ROOT"; mcp_cfg="$RUN_DIR/mcp-corpus.json"; else cwd="$BRAIN"; mcp_cfg="$RUN_DIR/mcp-brain.json"; fi
  for arm in mcp rg; do
    if [ "$arm" = "mcp" ]; then tools=""; allowed="mcp__shonkor__*"; cfg="$mcp_cfg"
    else tools="Bash,Read"; allowed="Bash(rg *)"; cfg="$RUN_DIR/mcp-none.json"; fi
    n=1
    while [ "$n" -le "$RUNS" ]; do
      dir="$RUN_DIR/$id/$arm/$n"; mkdir -p "$dir"
      # --json-schema is appended at the call: the schema text is long and would drown the dry-run listing.
      cmd=(claude -p --bare --output-format stream-json --verbose --model "$MODEL" --effort "$EFFORT"
           --tools "$tools" --allowedTools "$allowed" --mcp-config "$cfg" --strict-mcp-config
           --max-turns "$MAX_TURNS" --max-budget-usd "$MAX_USD" --no-session-persistence)
      if [ "$DRY" -eq 1 ]; then
        printf '\n[DRY-RUN] %s/%s/%s  (cwd %s)\n  ' "$id" "$arm" "$n" "$cwd"
        printf '%q ' "${cmd[@]}"; printf -- "--json-schema \"\$(cat %q)\" " "$SCHEMA"
        printf '< %q > %q 2> %q\n' "$RUN_DIR/$prompt" "$dir/stream.jsonl" "$dir/stderr.log"
      elif [ -s "$dir/stream.jsonl" ]; then
        echo "skip $id/$arm/$n (stream exists)"
      else
        echo "run  $id/$arm/$n"
        ( cd "$cwd" && "${cmd[@]}" --json-schema "$SCHEMA_JSON" < "$RUN_DIR/$prompt" > "$dir/stream.jsonl" 2> "$dir/stderr.log" ) || echo "  claude exited $? (recorded in $dir/stderr.log)"
        tally="$("$BENCH" --ap6-tally "$RUN_DIR")"; echo "  $tally"
        total_cost="$(printf '%s' "$tally" | sed -E 's/.*cost_usd=([0-9.]+).*/\1/')"
        if [ "$(awk -v a="$total_cost" -v b="$TOTAL_USD" 'BEGIN{print (a>b)?1:0}')" -eq 1 ]; then
          echo "abort: run-set cost $total_cost USD exceeds the cap of $TOTAL_USD USD (TOTAL_USD in run.sh) — fix the limits before continuing; the runs so far are kept" >&2
          exit 1
        fi
      fi
      n=$((n+1))
    done
  done
done < "$RUN_DIR/plan.tsv"

# ---------- 4. score ----------
SCORE=("$BENCH" "$BRAIN_DB" --ap6 "$RUN_DIR")
[ -n "$CORPUS_DB" ] && SCORE+=(--db-c "$CORPUS_DB")
if [ "$DRY" -eq 1 ]; then printf '\n[DRY-RUN] score: '; printf '%q ' "${SCORE[@]}"; echo; exit 0; fi
"${SCORE[@]}"
