# AP6 part 1 — two-arm harness (#473)

Runs the task corpus of #466 (`tasks.json`) through two arms — `mcp` (only `mcp__shonkor__*` tools, over
the real `shonkor mcp` stdio process) and `rg` (only `Bash(rg *)` + `Read`) — three runs per task and arm,
and scores every run against the mechanical key. Output: `bench/ap6-part1-report.md` (one table per class,
no aggregate across classes) and `results-<class>.json` beside this file (pinned by
`Ap6ReportNumbersTests`). Nothing in this directory is indexed (`shonkor.json` excludes `bench/golden/**`).

| File | Role |
|---|---|
| `run.sh` | the driver: env gate → `shonkor-bench --ap6-plan` → runs → `shonkor-bench --ap6`. Limits and `AUTH_MODE` at its top (#505; auth mode `subscription` ratified 2026-09-10). |
| `answer-schema.json` | the structured output both arms must emit: `{schemaVersion: 1, files: string[], symbols: string[]}` |
| `prompt-template.txt` | the prompt both arms receive (`{query}` + the same instruction) |
| `mcp-brain.template.json`, `mcp-corpus.template.json`, `mcp-none.json` | MCP config templates; `run.sh` fills the paths and writes them into the run directory |
| `results-<class>.json` | the pinned numbers of the last scored run (written by `--ap6`; absent until a run exists) |
| `scripts/` | the corpus generators of #466 |

## Before a scored run (procedure — the driver checks, it does not do)

1. **Build.** `dotnet build Shonkor.slnx -c Release`. The harness uses only the repository build
   (`src/Shonkor.CLI/bin/Release/net10.0/shonkor.exe`, hashed into `env.json`); the global `shonkor`
   tool is stale and is never used.
2. **Plugins.** In the Brain root: `src/Shonkor.CLI/bin/Release/net10.0/shonkor.exe plugin verify .`
   Exit 0 is required. On `STALE`, `shonkor plugin install <zip>` per plugin from the fresh build, then
   verify again. A scan with old plugin binaries looks exactly like a good scan (FPM: 340 `IMPORTS`
   instead of 4 371).
3. **Copy the previous graphs** to `C:/Projects/shonkor-prescan-<date>/` — a re-index is not reversible.
4. **Re-index the corpus** at the mapping's revision: `cd <corpus-root>` (projects.json: `Corpus-A`),
   `git rev-parse HEAD` must equal `corpusRevision` in `<corpus-root>/bench/ap6-mapping.json`, then
   `SHONKOR_WORKSPACE=C:/Projects/Brain <brain>/src/Shonkor.CLI/bin/Release/net10.0/shonkor.exe index . --force`.
   Semantic C# is on by default in the CLI; the corpus has no `shonkor.json`, so the default exclude
   patterns apply — the report should say so.
5. **Re-index Brain** the same way at the HEAD the class A/B keys were checked against.
6. `shonkor-bench <db> --provenance` per graph before and after, so the edge census is on record.
7. `claude auth status` reports `loggedIn: true` with the subscription account, and **no** `ANTHROPIC_API_KEY` /
   `ANTHROPIC_AUTH_TOKEN` is set in the shell that runs `run.sh` (see *Auth mode* below).

The env gate of `run.sh` verifies (2), `claude --version` ≥ the pin, a real `rg` binary, the sign-in (and the
absence of an API key), and — through `--ap6-plan` — the revision equalities (mapping ↔ tasks, checkout ↔
mapping, graph ↔ checkout), clean working trees for the relevant file kinds, and that every class-C token has
an unambiguous entry.

## Auth mode and isolation (ratified 2026-09-10: subscription, no API cost)

`AUTH_MODE=subscription` at the top of `run.sh` (override per run with `--auth api`). The runs go through the
claude.ai subscription login, so **no bill is produced**; `total_cost_usd` and `--max-budget-usd` keep working
because both are client-side estimates at list price ([headless](https://code.claude.com/docs/en/headless):
"Both figures are client-side estimates"; [costs](https://code.claude.com/docs/en/costs): "Claude Max and Pro
subscribers have usage included in their subscription, so the session cost figure isn't relevant for
billing"). The USD columns and the `TOTAL_USD` stop therefore stay as an **effort measure**, not as money.

Why no `--bare`: bare mode "never reads OAuth credentials" — its auth "is strictly `ANTHROPIC_API_KEY`"
([cli-reference](https://code.claude.com/docs/en/cli-reference), `claude --help`). Without `--bare`, `-p`
"loads the same context an interactive session would" (headless), so the driver switches every piece off
with its own documented switch:

| Switch | What it removes | Source |
|---|---|---|
| `--restricted` | user/project/local settings (hooks, plugins, permission rules): "loads only managed settings and `--settings`"; built "when an evaluation harness drives `claude`" | cli-reference (≥ 2.1.248) |
| `--strict-mcp-config` | every MCP server except the one in `--mcp-config` | mcp doc |
| `--settings '{"disableAllHooks":true}'` | hooks, including any a managed layer could add | permissions doc, "Before you run `claude -p` in a repository you didn't write" |
| `--disable-slash-commands` | skills and custom commands | cli-reference |
| `CLAUDE_CODE_DISABLE_CLAUDE_MDS=1` | "any CLAUDE.md memory files … including user, project, and auto memory files" — the untracked `CLAUDE.md` in the Brain root and `~/.claude/CLAUDE.md` included | env-vars doc |
| `CLAUDE_CODE_DISABLE_AUTO_MEMORY=1` | auto memory, read and write | env-vars doc |

The gate refuses a subscription set while `ANTHROPIC_API_KEY` or `ANTHROPIC_AUTH_TOKEN` is set: in `-p` "the
key is always used when present" and both outrank the login ([authentication](https://code.claude.com/docs/en/authentication),
credential order) — the set would be billed to the key. `env.json` records `authMode`, `claudeAuthMethod`
(from `claude auth status`) and `isolationFlags`; the report prints them in *Environment*.

**Session limits.** The subscription has rolling usage windows ("You've hit your session limit · resets 4pm",
HTTP 429 — [errors doc](https://code.claude.com/docs/en/errors)). A run that ends on one comes back as a
`result` with `is_error: true`, subtype `success`, `api_error_status` and the message in `result` (SDK
reference). The driver reads that through `--ap6-tally` after every run and **stops the set at once** —
`abort: <task/arm/n> hit a usage/rate limit … resume after the reset with: run.sh <run-dir> --resume` — so no
further task is burned as `noAnswer`. `--resume` keeps `env.json`/`plan.*`, skips every run whose result is a
measurement, moves the failed attempt to `stream.notrun-<ts>.jsonl` and runs it again. The scorer lists such
a run as `notRun` (not counted, never `noAnswer`); until it is re-run the task's arm is `incomplete`. The same
holds for any other API error and for a stream without a `result` event; `error_max_turns`,
`error_max_budget_usd` and `error_max_structured_output_retries` are **not** re-run — they are the pinned
limits of #505 doing their job, and stay scored as `noAnswer`.

`--auth api` restores the previous mode: `claude -p --bare` with `ANTHROPIC_API_KEY`, billed per token.

## Checklist before a scored run (stakeholder)

- [ ] `claude update` — the driver pins `MIN_CLAUDE` (`run.sh`); older `-p` starts before the stdio MCP server is up.
- [ ] `rg --version` prints a real ripgrep (the gate refuses a shell fallback to `grep`); on Windows:
      `winget install BurntSushi.ripgrep.MSVC`, then a new shell.
- [ ] `claude auth status` → `"loggedIn": true` with the subscription account; `ANTHROPIC_API_KEY` and
      `ANTHROPIC_AUTH_TOKEN` **unset** in the shell that runs `run.sh` (the gate refuses otherwise).
- [ ] Limits at the top of `run.sh` read and confirmed: `MODEL`, `EFFORT`, `MAX_TURNS`, `MAX_USD` per run,
      `TOTAL_USD` per run set (#505), `AUTH_MODE`. They are recorded in `env.json` and printed beside every table.
- [ ] Steps 1-7 above done; `run.sh --dry-run` shows **no** "would abort" line.
- [ ] `--smoke` first (3 tasks × 2 arms × 1 run); read *Arm violations* in the report before the full set.
- [ ] On `abort: … hit a usage/rate limit`: wait for the reset named in `<task>/<arm>/<n>/result.json`, then
      `run.sh <run-dir> --resume`.
- [ ] Never pass `--ignore-preconditions` to `--ap6-plan` for a run that is meant to be scored — the flag exists
      for `--dry-run` on a machine whose graphs are stale.
- [ ] **Before committing a scored run**: read `git diff bench/golden/ap6/results-C.json` and
      `bench/ap6-part1-report.md` by hand, tool-call inputs included. `Ap6Corpus.FindLeaks` is a net, not a
      proof — a customer name in a shape it does not know passes it, and git history cannot be un-published.

## Running

```
bench/golden/ap6/run.sh --dry-run                      # prints the command lines and configs, runs nothing
bench/golden/ap6/run.sh <run-dir> --smoke              # A-01, B-01, C1-01 × 2 arms × 1 run
bench/golden/ap6/run.sh <run-dir>                      # the full set: 30 × 2 × 3
bench/golden/ap6/run.sh <run-dir> --class A            # one class only
bench/golden/ap6/run.sh <run-dir> --resume             # continue after a session limit / API error (keeps env.json, plan.*)
bench/golden/ap6/run.sh <run-dir> --auth api           # the billed mode: --bare + ANTHROPIC_API_KEY
```

`<run-dir>` must lie outside both repositories (default `C:/Projects/shonkor-bench-runs/ap6/<timestamp>`):
class-C prompts and streams contain customer names. Re-running with the same directory skips runs that are
a measurement; `--resume` additionally keeps the first invocation's `env.json` and `plan.*` and re-runs the
attempts that were none (limit, API error, no result). The run-set cost cap (`TOTAL_USD`), the limit stop and
the redo list all come from `shonkor-bench --ap6-tally` after every run.

After a **smoke** run, read the report's *Arm violations* line before the full set: if Claude Code lists a
tool in `system/init.tools` that belongs to neither arm (a harness-neutral helper), add its name to
`Ap6Scorer.ToleratedTools` — never a tool that reads files or the graph.

## Run directory layout

```
env.json                     versions, model, limits, auth mode, isolation flags, shonkor.dll SHA-256, git HEADs, plugin verify output
resume.log                   one line per --resume invocation (versions, HEADs at that time); env.json is never rewritten
plan.tsv / plan.env / plan.json   the tasks to run, the roots and databases (paths — stays out of the repo)
prompts/<task>.txt           the resolved prompt (class C: real names)
mcp-brain.json / mcp-corpus.json / mcp-none.json
<task>/<arm>/<n>/stream.jsonl    the full stream-json output
<task>/<arm>/<n>/stderr.log
<task>/<arm>/<n>/result.json     the last result event (written by --ap6-tally)
<task>/<arm>/<n>/meta.json       the parsed record + verdict (written by --ap6)
<task>/<arm>/<n>/*.notrun-<ts>.* an attempt that was no measurement (limit, API error), moved aside by --resume
```

## Scoring

`shonkor-bench <brain.db> --ap6 <run-dir> --db-c <corpus.db> [--ap6-match recall|exact]`

- **Match mode** (one-way door, `Ap6Scorer.DefaultMatchMode`): `Recall` — every key file and symbol is in
  the answer; extras are counted as `overSelect`. `Exact` — set equality. The report names the mode used.
- **tokensApprox** (the gate's column, identical for both arms): Σ characters of all `tool_result` text
  blocks in the main conversation / 4. **usageExact**: Σ `message.usage` over assistant messages.
- **Connection gate**: a run counts only if `system/init` shows the arm's tool set and, for `mcp`,
  `shonkor` connected (for `rg`, no server connected). Otherwise `armViolation` — listed, not counted.
- **noAnswer**: no parseable `structured_output` or `schemaVersion ≠ 1` → incorrect.
- **notRun**: the run ended on the API's or the loop's side — `is_error` with a subtype other than the
  pinned-limit ones (`error_max_turns`, `error_max_budget_usd`, `error_max_structured_output_retries`), i.e. a
  failed final request (usage/rate limit, HTTP 429, other API errors), `error_during_execution`, or no
  `result` event at all → listed with its reason, not counted, never `noAnswer`; `--resume` runs it again.
- **Majority**: correct in ≥ 2 of 3 scored runs; fewer than 3 scored → `incomplete`, never correct.
- **Class C**: answers are translated back into tokens through the mapping; unmapped items are counted
  (`unmappedFiles/Symbols`), ambiguous type names flagged; `results-C.json` passes `Ap6Corpus.FindLeaks`
  (fixed patterns + the mapping's deny words) before it is written.
- **Gate** (class C, verbatim in the report): *MCP arm correct on at least 3 more class-C tasks than the
  rg arm (majority of 3 runs) AND fewer file-content tokens read at equal correctness* —
  `C_mcp − C_rg ≥ 3` and Σ tokensApprox(mcp) < Σ tokensApprox(rg) over the tasks both arms got right.

## Re-baseline (#498)

Three pins interact; none of them moves on its own.

| Pin | Where | What it freezes | What it does **not** freeze |
|---|---|---|---|
| `walk_ref` | `scripts/build-tasks.sh` | the merge walk of `keys-ab.sh` — which merges are visited, in which order, so later merges into `develop` cannot shift the A/B rows | the HEAD-side checks (`exists_at_head`, `declares_type HEAD:` in `keys-ab.sh`) — they read the working checkout, so a rename or delete of a key file/type at HEAD can still reject a row and shift the ones after it |
| `corpusRevision` | `<corpus-root>/bench/ap6-mapping.json` (= every class-C `keySource.ref`) | the corpus commit the class-C keys and the token mapping were derived from | the corpus checkout itself — `--ap6-plan` refuses a scored run whose checkout differs |
| `results-<class>.json` | this directory | the numbers of the last scored run, pinned by `Ap6ReportNumbersTests` | — |

**Class C is never regenerated by accident.** `build-tasks.sh` without `<corpus-root>` carries the committed
C block over verbatim; only `build-tasks.sh <corpus-root>` runs `keys-c.sh`, which re-walks the corpus at its
current HEAD and **renumbers every token** (`Rendering-nnn`, `Controller-nnn`, `View-nnn`, `Template-nnn`,
`Model-nnn` are assigned in walk order, not by content). After a regeneration the same token may denote a
different file than before, and the same task id (`C1-03`) may be a different chain.

**When.** Bump `walk_ref` when the class-B supply under the current pin runs dry (< 20 merges) or when a key
type of an A/B row is renamed or deleted at HEAD. Re-baseline class C when the corpus checkout moves to a
new revision by stakeholder decision — never because `git pull` happened to move it.

**How.**

1. Corpus: `git -C <corpus-root> rev-parse HEAD` is the revision to key at; the working tree must be clean
   for `.yml/.cs/.cshtml/.sln` (`keys-c.sh` refuses otherwise). Do not switch the corpus branch for this.
2. Copy the old mapping to `C:/Projects/shonkor-prescan-<date>/ap6-mapping-<oldrev>.json` — it is the only
   record of what the old tokens meant.
3. `bash bench/golden/ap6/scripts/build-tasks.sh <corpus-root>` (≈ 9 min; ledgers on stderr — keep the
   counts for the PR). Afterwards every class-C `keySource.ref` and the mapping's `corpusRevision` equal the
   corpus HEAD, and the A/B block is byte-identical unless `walk_ref` was bumped as well.
4. Tests: `TheCorpus_Validates_WithThirtyTasks_TenPerClass`, `TheCorpus_CarriesNoCustomerData`, and — with
   the corpus present — `TheMappingFile_DenyWords_HashToTheEmbeddedSet`. If the deny words changed (a new
   root namespace in the corpus), do **not** just update the embedded hashes: the new word must be reviewed as
   a deny word first.
5. Report in the PR, counts only: which class-C ids changed, the ledger populations before/after, whether
   token numbers shifted (sample: does `Rendering-001` still point at the same path?).

**What it means for scored runs.** A run is scored against one `tasks.json` + one mapping at one
`corpusRevision`. After a re-baseline, earlier `results-<class>.json` for the regenerated class are numbers
of a different corpus — they are not comparable, must not be carried into a new report, and the pinned
tests will fail until a new scored run replaces them. Re-baseline only while no scored run exists, or
accept that the previous run is retired.
