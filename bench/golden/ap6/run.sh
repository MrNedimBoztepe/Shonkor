#!/usr/bin/env bash
# run.sh — AP6 part 1 two-arm harness driver (#473): every task of bench/golden/ap6/tasks.json, answered by
# the same agent and model twice — once with only the shonkor MCP tools, once with only ripgrep + file
# reads — three runs each, recorded and scored.
#
# Usage:  bench/golden/ap6/run.sh [<run-dir>] [--smoke] [--class A|B|C] [--model <id>] [--auth subscription|api]
#                                 [--resume] [--dry-run]
#         <run-dir>  where streams, prompts and configs go. MUST be outside both repositories: class-C prompts
#                    and streams carry customer names. Default: $AP6_RUNS_ROOT/ap6/<timestamp>
#                    (AP6_RUNS_ROOT defaults to C:/Projects/shonkor-bench-runs). Re-running with the same
#                    <run-dir> skips runs that already have a stream.jsonl and are a measurement (see --resume).
#         --smoke    A-01, B-01, C1-01 × both arms × 1 run — the connection and tool-set check before the full
#                    set is spent. Read the report's "Arm violations" first.
#         --class X  only that class (e.g. A/B while the corpus graph is not yet re-indexed).
#         --model    override MODEL below for this run (recorded in env.json).
#         --auth     override AUTH_MODE below (recorded in env.json).
#         --resume   continue an interrupted set in an existing <run-dir>: env.json and plan.* are kept as they
#                    are (no re-plan), runs with a result that is a measurement are skipped, runs that hit a
#                    usage/rate limit, an API error or died without a result are moved aside and run again.
#         --dry-run  print every claude command line and the generated MCP configs, run nothing. Env-gate
#                    failures are printed as "would abort" instead of aborting, so the plan is visible on a
#                    machine that is not signed in.
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
# Auth mode (ratified 2026-09-10: subscription, no API cost):
#   subscription  claude -p signed in with the claude.ai subscription (`claude auth status`), NO --bare — bare
#                 mode never reads the OAuth login (cli-reference: "Anthropic auth is strictly ANTHROPIC_API_KEY").
#                 The gate refuses a set while ANTHROPIC_API_KEY / ANTHROPIC_AUTH_TOKEN is set: in -p the key
#                 "is always used when present" (authentication doc, credential order 3 > 7) and would be billed.
#                 Isolation without --bare, every piece a documented switch:
#                   --restricted                 loads only managed settings and --settings, so no user/project/
#                                                local hooks, plugins or permission rules; built for "an
#                                                evaluation harness [that] drives claude" (cli-reference, ≥ 2.1.248)
#                   --strict-mcp-config          only the servers of --mcp-config (mcp doc)
#                   --settings <run-dir>/arm-<arm>.settings.json   the arm's own settings file. NOTE: this used
#                                                to be '{"disableAllHooks":true}' and deliberately is not any
#                                                more (#513) — under --restricted no user/project/local hook is
#                                                loaded in the first place, so the switch bought nothing, and it
#                                                would switch off the harness's OWN PreToolUse hook, which is the
#                                                only thing that keeps the rg arm inside ripgrep. Do not put it back.
#                   --permission-mode dontAsk    nothing waits for an answer that cannot come in -p (cli-reference)
#                   --permission-prompts none    "anything that would prompt is denied automatically" (cli-reference)
#                   --disallowedTools <mirror>   each arm denies the other arm's tools by bare name, which removes
#                                                them from the context entirely (permissions doc)
#                   --disable-slash-commands     skills and custom commands off (cli-reference)
#                   CLAUDE_CODE_DISABLE_CLAUDE_MDS=1   "prevent loading any CLAUDE.md memory files into context,
#                                                including user, project, and auto memory files" (env-vars doc)
#                   CLAUDE_CODE_DISABLE_AUTO_MEMORY=1  auto memory neither loaded nor written (env-vars doc)
#                 --max-budget-usd and total_cost_usd keep working: both are client-side estimates at list price
#                 (headless/costs docs) — no bill in this mode, the numbers stay as an effort measure.
#   api           claude -p --bare with ANTHROPIC_API_KEY: the pre-2026-09-10 mode, billed per token.
# Why the version pin: before 2.1.221 `-p` with --mcp-config did not wait for the stdio server before turn
# 1, so the MCP arm could start with shonkor "pending" — the connection gate would then discard the run;
# --restricted needs 2.1.248.
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
AUTH_MODE="subscription"     # subscription (no --bare, no API cost — ratified 2026-09-10) | api (--bare + ANTHROPIC_API_KEY)
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

RUN_DIR=""; SMOKE=0; CLASS=""; DRY=0; RESUME=0
while [ $# -gt 0 ]; do
  case "$1" in
    --smoke) SMOKE=1 ;;
    --class) CLASS="${2:?--class needs A|B|C}"; shift ;;
    --model) MODEL="${2:?--model needs an id}"; shift ;;
    --auth) AUTH_MODE="${2:?--auth needs subscription|api}"; shift ;;
    --resume) RESUME=1 ;;
    --dry-run) DRY=1 ;;
    --*) echo "unknown option $1" >&2; exit 2 ;;
    *) RUN_DIR="$1" ;;
  esac
  shift
done
case "$AUTH_MODE" in subscription|api) ;; *) echo "AUTH_MODE must be subscription or api, was '$AUTH_MODE'" >&2; exit 2 ;; esac
[ -n "$RUN_DIR" ] || RUN_DIR="${AP6_RUNS_ROOT:-C:/Projects/shonkor-bench-runs}/ap6/$(date -u +%Y%m%dT%H%M%SZ)"
RUN_DIR="${RUN_DIR//\\//}"
[ "$SMOKE" -eq 1 ] && RUNS=1
SMOKE_IDS=" A-01 B-01 C1-01 "

# A run directory inside either repository would put customer names under version control's nose. The path is
# canonicalised first (a relative path, an MSYS /c/... form or a differently-cased drive letter would otherwise
# slip past a literal prefix compare); the corpus root is checked the same way once plan.env names it.
mkdir -p "$RUN_DIR"
RUN_DIR="$(cd "$RUN_DIR" && (pwd -W 2>/dev/null || pwd))"; RUN_DIR="${RUN_DIR//\\//}"
outside_repo() { # <dir> <root> <label> — exit 2 when <dir> is <root> or lies inside it (case-insensitive: Windows paths)
  local dir="$1" root="$2" label="$3" hit=0
  shopt -s nocasematch; case "$dir" in "$root"|"$root"/*) hit=1 ;; esac; shopt -u nocasematch
  [ "$hit" -eq 0 ] || { rmdir "$dir" 2>/dev/null || true; echo "run dir must be outside the $label repository: $dir" >&2; exit 2; }
}
outside_repo "$RUN_DIR" "$BRAIN" "Brain"
if [ "$RESUME" -eq 1 ] && { [ ! -f "$RUN_DIR/env.json" ] || [ ! -f "$RUN_DIR/plan.tsv" ]; }; then
  echo "--resume needs an existing run dir with env.json and plan.tsv: $RUN_DIR" >&2; exit 2
fi

# ---------- helpers ----------
FAILED=0
gate_fail() { # message — abort, or in --dry-run report and continue
  if [ "$DRY" -eq 1 ]; then echo "[DRY-RUN] would abort: $*"; FAILED=1; else echo "abort: $*" >&2; exit 1; fi
}
json_str() { local s=${1//\\/\\\\}; s=${s//\"/\\\"}; s=${s//$'\r'/}; s=${s//$'\n'/\\n}; s=${s//$'\t'/\\t}; printf '"%s"' "$s"; }
ver_ge() { [ "$(printf '%s\n%s\n' "$2" "$1" | sort -V | head -n1)" = "$2" ]; }  # $1 >= $2
sha256_of() { if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
tally_field() { printf '%s' "$1" | sed -nE "s/.*[[:space:]]$2=\[([^]]*)\].*/\1/p"; }  # <tally line> <redo|limit> → comma list
in_list() { case ",$2," in *",$1,"*) return 0 ;; *) return 1 ;; esac; }             # <item> <comma list>

# ---------- 1. env gate ----------
export ENABLE_TOOL_SEARCH=false                 # deferred tool loading would hide tools from init.tools — the arm check reads that list
export CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1  # no telemetry / update pings during a measured run
export DISABLE_AUTOUPDATER=1                    # the version is pinned above; an update mid-set would change the harness
export CLAUDE_CODE_DISABLE_CLAUDE_MDS=1         # no CLAUDE.md of any layer (user, project, auto memory) — the arms get the prompt only
export CLAUDE_CODE_DISABLE_AUTO_MEMORY=1        # auto memory neither read nor written by a measured run

CLAUDE_VERSION="$(claude --version 2>/dev/null | head -n1 | sed -E 's/^([0-9]+\.[0-9]+\.[0-9]+).*/\1/')" || CLAUDE_VERSION=""
[ -n "$CLAUDE_VERSION" ] || gate_fail "claude CLI not found on PATH"
if [ -n "$CLAUDE_VERSION" ] && ! ver_ge "$CLAUDE_VERSION" "$MIN_CLAUDE"; then
  gate_fail "claude $CLAUDE_VERSION < $MIN_CLAUDE — update the CLI; older -p does not wait for the stdio MCP server (see header)"
fi
RG_BIN="$(type -P rg || true)"
[ -n "$RG_BIN" ] || gate_fail "rg (ripgrep) not found as a binary on PATH — the pilot silently fell back to grep; this harness refuses to"
RG_VERSION="$( [ -n "$RG_BIN" ] && "$RG_BIN" --version | head -n1 || echo "")"

CLAUDE_AUTH_METHOD=""
if [ "$AUTH_MODE" = "subscription" ]; then
  # Credential order (authentication doc): an API key or bearer token outranks the subscription login and, in -p,
  # is used without asking — the set would be billed. Refuse rather than measure on the wrong account.
  [ -z "${ANTHROPIC_API_KEY:-}" ] || gate_fail "ANTHROPIC_API_KEY is set — in auth mode subscription claude -p would use it and bill the set; unset it in this shell (or run with --auth api)"
  [ -z "${ANTHROPIC_AUTH_TOKEN:-}" ] || gate_fail "ANTHROPIC_AUTH_TOKEN is set — it outranks the subscription login; unset it in this shell"
  # `claude auth status`: JSON with loggedIn/authMethod, exit 0 when signed in and 1 when not (cli-reference).
  AUTH_JSON="$(claude auth status --json 2>/dev/null || true)"
  CLAUDE_AUTH_METHOD="$(printf '%s' "$AUTH_JSON" | sed -nE 's/.*"authMethod"[[:space:]]*:[[:space:]]*"([^"]*)".*/\1/p' | head -n1)"
  if ! printf '%s' "$AUTH_JSON" | grep -Eq '"loggedIn"[[:space:]]*:[[:space:]]*true'; then
    gate_fail "claude is not signed in (claude auth status: loggedIn != true) — run \`claude auth login\` with the subscription account"
  fi
else
  [ -n "${ANTHROPIC_API_KEY:-}" ] || gate_fail "ANTHROPIC_API_KEY is not set — auth mode api runs claude -p --bare, which authenticates with the API key only (never the OAuth login); export it in this shell"
  CLAUDE_AUTH_METHOD="api-key"
fi
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

# ---------- isolation flags per auth mode (see header) ----------
# The per-arm --settings and --disallowedTools are added at the call (they differ by arm); these are the
# flags both arms share.
if [ "$AUTH_MODE" = "subscription" ]; then
  ISOLATION=(--restricted --strict-mcp-config --disable-slash-commands --permission-mode dontAsk --permission-prompts none)
else
  ISOLATION=(--bare --strict-mcp-config)
fi
ISOLATION_TEXT="${ISOLATION[*]}"

# ---------- arm purity: what each arm may call, and what stops it (#513) ----------
# --allowedTools only PRE-APPROVES; it denies nothing, and Claude Code runs its built-in read-only Bash set
# (ls, cat, grep, find, wc, cd, ...) without a prompt in every mode. The first smoke run therefore measured an
# "rg arm" that answered out of grep. Two mirror-image measures, neither of them a rule about command text
# (rule syntax cannot say "Bash: only rg" — precedence is deny > ask > allow, so a Bash rule catches rg too):
#   rg arm   a PreToolUse hook on Bash that exits 2 for anything that is not one plain rg command, plus
#            --disallowedTools "mcp__*" so the graph is not reachable even if a server were configured.
#   mcp arm  --tools "" (no built-in tool at all) plus --disallowedTools naming the file/command tools by
#            bare name, which takes them out of the model's context rather than merely refusing them.
# The hook is what actually refuses; the flags are the backstop. Both are recorded in env.json and printed
# beside every class table, because a purity claim nobody can read back is not evidence.
RG_HOOK="$HERE/rg-only-hook.sh"
[ -f "$RG_HOOK" ] || gate_fail "rg-only-hook.sh missing next to run.sh — the rg arm has nothing keeping it inside ripgrep"
# Hooks are fail-OPEN by documentation: a path that does not resolve, a script that is not executable, an
# exit code other than 2 — the call runs. Existence therefore proves nothing; the hook is run here, on a
# payload it must refuse, and the run set does not start unless it does (#514). Three lines against a lost
# set of paid runs, and against the silent version of the #513 defect: a hook that never fires looks exactly
# like an arm that behaved.
HOOK_SELFTEST_EXIT=0
printf '%s' '{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"grep -rn x ."}}' \
  | bash "$RG_HOOK" >/dev/null 2>&1 || HOOK_SELFTEST_EXIT=$?
[ "$HOOK_SELFTEST_EXIT" -eq 2 ] || gate_fail "the rg-only hook did not block a grep command (exit $HOOK_SELFTEST_EXIT, expected 2) — hooks fail open, so the rg arm would run unguarded. Check that node is on PATH and run bash $HERE/rg-only-hook.test.sh"
# ripgrep reads a config file from RIPGREP_CONFIG_PATH, and that file may contain --pre (a preprocessor
# command). The hook vets the command text, not the environment, so the variable is taken out of the runs'
# environment rather than trusted to be unset (#514).
unset RIPGREP_CONFIG_PATH
MCP_DENY="Bash Read Grep Glob Edit Write WebFetch WebSearch"
RG_DENY="mcp__*"
PERMISSION_RULES="mcp arm: --tools '' --allowedTools 'mcp__shonkor__*' --disallowedTools '$MCP_DENY'; rg arm: --tools 'Bash,Read' --allowedTools 'Bash(rg *)' --disallowedTools '$RG_DENY'"
HOOKS_TEXT="rg arm: PreToolUse(Bash) -> rg-only-hook.sh (exit 2 unless the command is one plain rg call: no UNQUOTED shell separator or expansion, no --pre/-z/--hostname-bin; verified against a grep payload before the set starts); mcp arm: none"
# Written on every invocation, --resume included: these are inputs to the runs, not the set's record (that is
# env.json, which --resume keeps). A resume whose settings file was missing would run an rg arm with no hook.
# A settings FILE, not inline JSON: the hook command carries a path with slashes and quoting it inline is
# one escaping mistake away from a hook that never fires — and a hook that never fires looks like a clean run.
printf '{\n  "hooks": {\n    "PreToolUse": [\n      { "matcher": "Bash", "hooks": [ { "type": "command", "command": %s } ] }\n    ]\n  }\n}\n' \
  "$(json_str "bash '$RG_HOOK'")" > "$RUN_DIR/arm-rg.settings.json"
printf '{ "hooks": {} }\n' > "$RUN_DIR/arm-mcp.settings.json"

# ---------- 2. plan (preconditions live in shonkor-bench, not here) ----------
CORPUS_ROOT=""; CORPUS_PROJECT="Corpus-A"; CORPUS_DB=""; CORPUS_REVISION=""; BRAIN_PROJECT="Shonkor"; BRAIN_DB=""
if [ "$RESUME" -eq 0 ]; then
  PLAN_ARGS=(--ap6-plan "$TASKS" --workspace "$BRAIN" --out "$RUN_DIR")
  [ -n "$CLASS" ] && PLAN_ARGS+=(--class "$CLASS")
  # A dry run still wants the command lines on a machine whose graphs are stale: the plan prints the failures
  # and writes the files anyway. A real run never passes this flag.
  [ "$DRY" -eq 1 ] && PLAN_ARGS+=(--ignore-preconditions)
  # A plan that fails must not leave last time's plan.* behind for step 3 to pick up.
  rm -f "$RUN_DIR/plan.tsv" "$RUN_DIR/plan.env" "$RUN_DIR/plan.json"
  if [ -x "$BENCH" ]; then
    if ! "$BENCH" "${PLAN_ARGS[@]}"; then gate_fail "--ap6-plan reported precondition failures (above)"; fi
  else
    gate_fail "cannot plan without shonkor-bench"
  fi
fi
if [ -f "$RUN_DIR/plan.env" ]; then
  # KEY=VALUE lines written by --ap6-plan; values are paths/names without quotes.
  while IFS='=' read -r k v; do case "$k" in BRAIN_PROJECT|BRAIN_DB|CORPUS_ROOT|CORPUS_PROJECT|CORPUS_DB|CORPUS_REVISION) printf -v "$k" '%s' "$v" ;; esac; done < "$RUN_DIR/plan.env"
fi
[ -z "$CORPUS_ROOT" ] || outside_repo "$RUN_DIR" "${CORPUS_ROOT%/}" "corpus"
CORPUS_HEAD="$( [ -n "$CORPUS_ROOT" ] && git -C "$CORPUS_ROOT" rev-parse HEAD 2>/dev/null || echo "")"

# ---------- MCP configs into the run dir (absolute forward-slash paths; the templates hold placeholders only) ----------
if [ "$RESUME" -eq 0 ]; then
  sed -e "s#__SHONKOR_EXE__#$SHONKOR#g" -e "s#__BRAIN_ROOT__#$BRAIN#g" -e "s#__BRAIN_PROJECT__#$BRAIN_PROJECT#g" \
    "$HERE/mcp-brain.template.json" > "$RUN_DIR/mcp-brain.json"
  sed -e "s#__SHONKOR_EXE__#$SHONKOR#g" -e "s#__BRAIN_ROOT__#$BRAIN#g" -e "s#__CORPUS_PROJECT__#$CORPUS_PROJECT#g" \
    "$HERE/mcp-corpus.template.json" > "$RUN_DIR/mcp-corpus.json"
  cp "$HERE/mcp-none.json" "$RUN_DIR/mcp-none.json"
fi

# ---------- env.json (kept as it is on --resume: the set's record is the first invocation's; resumes go to resume.log) ----------
if [ "$RESUME" -eq 0 ]; then
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
  printf '  "authMode": %s,\n' "$(json_str "$AUTH_MODE")"
  printf '  "claudeAuthMethod": %s,\n' "$(json_str "$CLAUDE_AUTH_METHOD")"
  printf '  "isolationFlags": %s,\n' "$(json_str "$ISOLATION_TEXT")"
  printf '  "permissionRules": %s,\n' "$(json_str "$PERMISSION_RULES")"
  printf '  "hooks": %s,\n' "$(json_str "$HOOKS_TEXT")"
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
  printf '  "envFlags": { "ENABLE_TOOL_SEARCH": "false", "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC": "1", "DISABLE_AUTOUPDATER": "1", "CLAUDE_CODE_DISABLE_CLAUDE_MDS": "1", "CLAUDE_CODE_DISABLE_AUTO_MEMORY": "1" }\n'
  printf '}\n'
} > "$RUN_DIR/env.json"
else
  printf '%s resume claude=%s rg=%s authMode=%s authMethod=%s brainHead=%s corpusHead=%s pluginVerifyExit=%s\n' \
    "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$CLAUDE_VERSION" "$RG_VERSION" "$AUTH_MODE" "$CLAUDE_AUTH_METHOD" "$BRAIN_HEAD" "$CORPUS_HEAD" "$PLUGIN_VERIFY_EXIT" >> "$RUN_DIR/resume.log"
fi

if [ "$DRY" -eq 1 ]; then
  echo "=== dry run: env.json ==="; cat "$RUN_DIR/env.json"
  echo "=== dry run: MCP configs and arm settings ==="; for f in mcp-brain.json mcp-corpus.json mcp-none.json arm-rg.settings.json arm-mcp.settings.json; do echo "--- $f"; cat "$RUN_DIR/$f"; done
fi
[ "$FAILED" -eq 0 ] || echo "[DRY-RUN] one or more gate checks would abort a real run (see above)."

# ---------- 3. the runs ----------
SCHEMA_JSON="$(cat "$SCHEMA")"
[ -f "$RUN_DIR/plan.tsv" ] || { echo "no plan.tsv in $RUN_DIR — nothing to run" >&2; exit 1; }
# Runs recorded so far that are no measurement (limit hit, API error, no result event) — they are run again.
REDO=""
if [ "$DRY" -eq 0 ]; then REDO="$(tally_field "$("$BENCH" --ap6-tally "$RUN_DIR")" redo)"; fi
total_cost="0"
while IFS=$'\t' read -r id cls corpus prompt; do
  [ -n "$id" ] || continue
  if [ "$SMOKE" -eq 1 ]; then case "$SMOKE_IDS" in *" $id "*) ;; *) continue ;; esac; fi
  if [ "$cls" = "C" ]; then cwd="$CORPUS_ROOT"; mcp_cfg="$RUN_DIR/mcp-corpus.json"; else cwd="$BRAIN"; mcp_cfg="$RUN_DIR/mcp-brain.json"; fi
  for arm in mcp rg; do
    if [ "$arm" = "mcp" ]; then tools=""; allowed="mcp__shonkor__*"; denied="$MCP_DENY"; cfg="$mcp_cfg"
    else tools="Bash,Read"; allowed="Bash(rg *)"; denied="$RG_DENY"; cfg="$RUN_DIR/mcp-none.json"; fi
    arm_settings="$RUN_DIR/arm-$arm.settings.json"
    n=1
    while [ "$n" -le "$RUNS" ]; do
      dir="$RUN_DIR/$id/$arm/$n"; mkdir -p "$dir"; key="$id/$arm/$n"
      # --json-schema is appended at the call: the schema text is long and would drown the dry-run listing.
      cmd=(claude -p "${ISOLATION[@]}" --settings "$arm_settings" --output-format stream-json --verbose --model "$MODEL" --effort "$EFFORT"
           --tools "$tools" --allowedTools "$allowed" --disallowedTools "$denied" --mcp-config "$cfg"
           --max-turns "$MAX_TURNS" --max-budget-usd "$MAX_USD" --no-session-persistence)
      if [ "$DRY" -eq 1 ]; then
        # The corpus root is a customer path: it goes into the command, never onto stdout.
        printf '\n[DRY-RUN] %s  (cwd %s)\n  ' "$key" "$([ "$cls" = "C" ] && echo "<corpus>" || echo "$cwd")"
        printf '%q ' "${cmd[@]}"; printf -- "--json-schema \"\$(cat %q)\" " "$SCHEMA"
        printf '< %q > %q 2> %q\n' "$RUN_DIR/$prompt" "$dir/stream.jsonl" "$dir/stderr.log"
      elif [ -s "$dir/stream.jsonl" ] && ! in_list "$key" "$REDO"; then
        echo "skip $key (stream exists)"
      else
        if [ -s "$dir/stream.jsonl" ]; then
          # The failed attempt stays on record beside the new one; only stream.jsonl/result.json are read by the scorer.
          ts="$(date -u +%Y%m%dT%H%M%SZ)"
          mv "$dir/stream.jsonl" "$dir/stream.notrun-$ts.jsonl"
          [ -f "$dir/result.json" ] && mv "$dir/result.json" "$dir/result.notrun-$ts.json"
          [ -f "$dir/stderr.log" ] && mv "$dir/stderr.log" "$dir/stderr.notrun-$ts.log"
          echo "redo $key (previous attempt was no measurement — kept as *.notrun-$ts.*)"
        else
          echo "run  $key"
        fi
        ( cd "$cwd" && "${cmd[@]}" --json-schema "$SCHEMA_JSON" < "$RUN_DIR/$prompt" > "$dir/stream.jsonl" 2> "$dir/stderr.log" ) || echo "  claude exited $? (recorded in $dir/stderr.log)"
        tally="$("$BENCH" --ap6-tally "$RUN_DIR")"; echo "  $tally"
        if in_list "$key" "$(tally_field "$tally" limit)"; then
          echo "abort: $key hit a usage/rate limit (see $dir/result.json) — session limit; resume after the reset with: run.sh $RUN_DIR --resume" >&2
          exit 3
        fi
        if in_list "$key" "$(tally_field "$tally" redo)"; then
          echo "abort: $key is no measurement (API error or no result event — see $dir/result.json and stderr.log); the runs so far are kept; continue with: run.sh $RUN_DIR --resume" >&2
          exit 3
        fi
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
if [ "$DRY" -eq 1 ]; then
  # Printed with the corpus database path masked — it lies under the corpus root.
  printf '\n[DRY-RUN] score: '; for a in "${SCORE[@]}"; do [ -n "$CORPUS_ROOT" ] && a="${a//${CORPUS_ROOT%/}/<corpus>}"; printf '%q ' "$a"; done; echo; exit 0
fi
"${SCORE[@]}"
