# AP6 part 1 — two-arm harness (#473)

Runs the task corpus of #466 (`tasks.json`) through two arms — `mcp` (only `mcp__shonkor__*` tools, over
the real `shonkor mcp` stdio process) and `rg` (only `Bash(rg *)` + `Read`) — three runs per task and arm,
and scores every run against the mechanical key. Output: `bench/ap6-part1-report.md` (one table per class,
no aggregate across classes) and `results-<class>.json` beside this file (pinned by
`Ap6ReportNumbersTests`). Nothing in this directory is indexed (`shonkor.json` excludes `bench/golden/**`).

| File | Role |
|---|---|
| `run.sh` | the driver: env gate → `shonkor-bench --ap6-plan` → runs → `shonkor-bench --ap6`. Limits at its top (#505). |
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
7. `export ANTHROPIC_API_KEY=…` in the shell that runs `run.sh`; `claude -p --bare` never uses the OAuth login.

The env gate of `run.sh` verifies (2), `claude --version` ≥ the pin, a real `rg` binary, the API key, and
— through `--ap6-plan` — the revision equalities (mapping ↔ tasks, checkout ↔ mapping, graph ↔ checkout),
clean working trees for the relevant file kinds, and that every class-C token has an unambiguous entry.

## Checklist before spending money (stakeholder)

- [ ] `claude update` — the driver pins `MIN_CLAUDE` (`run.sh`); older `-p` starts before the stdio MCP server is up.
- [ ] `rg --version` prints a real ripgrep (the gate refuses a shell fallback to `grep`).
- [ ] `export ANTHROPIC_API_KEY=…` in the shell that runs `run.sh` (never the OAuth login; `--bare` ignores it).
- [ ] Limits at the top of `run.sh` read and confirmed: `MODEL`, `EFFORT`, `MAX_TURNS`, `MAX_USD` per run,
      `TOTAL_USD` per run set (#505). They are recorded in `env.json` and printed beside every table.
- [ ] Steps 1-6 above done; `run.sh --dry-run` shows **no** "would abort" line.
- [ ] `--smoke` first (3 tasks × 2 arms × 1 run); read *Arm violations* in the report before the full set.
- [ ] Never pass `--ignore-preconditions` to `--ap6-plan` for a run that is meant to be scored — the flag exists
      for `--dry-run` on a machine whose graphs are stale.

## Running

```
bench/golden/ap6/run.sh --dry-run                      # prints the command lines and configs, runs nothing
bench/golden/ap6/run.sh <run-dir> --smoke              # A-01, B-01, C1-01 × 2 arms × 1 run
bench/golden/ap6/run.sh <run-dir>                      # the full set: 30 × 2 × 3
bench/golden/ap6/run.sh <run-dir> --class A            # one class only
```

`<run-dir>` must lie outside both repositories (default `C:/Projects/shonkor-bench-runs/ap6/<timestamp>`):
class-C prompts and streams contain customer names. Re-running with the same directory resumes. The
run-set cost cap (`TOTAL_USD`) is enforced after every run via `shonkor-bench --ap6-tally`.

After a **smoke** run, read the report's *Arm violations* line before the full set: if Claude Code lists a
tool in `system/init.tools` that belongs to neither arm (a harness-neutral helper), add its name to
`Ap6Scorer.ToleratedTools` — never a tool that reads files or the graph.

## Run directory layout

```
env.json                     versions, model, limits, shonkor.dll SHA-256, git HEADs, plugin verify output
plan.tsv / plan.env / plan.json   the tasks to run, the roots and databases (paths — stays out of the repo)
prompts/<task>.txt           the resolved prompt (class C: real names)
mcp-brain.json / mcp-corpus.json / mcp-none.json
<task>/<arm>/<n>/stream.jsonl    the full stream-json output
<task>/<arm>/<n>/stderr.log
<task>/<arm>/<n>/result.json     the last result event (written by --ap6-tally)
<task>/<arm>/<n>/meta.json       the parsed record + verdict (written by --ap6)
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
- **Majority**: correct in ≥ 2 of 3 scored runs; fewer than 3 scored → `incomplete`, never correct.
- **Class C**: answers are translated back into tokens through the mapping; unmapped items are counted
  (`unmappedFiles/Symbols`), ambiguous type names flagged; `results-C.json` passes `Ap6Corpus.FindLeaks`
  (fixed patterns + the mapping's deny words) before it is written.
- **Gate** (class C, verbatim in the report): *MCP arm correct on at least 3 more class-C tasks than the
  rg arm (majority of 3 runs) AND fewer file-content tokens read at equal correctness* —
  `C_mcp − C_rg ≥ 3` and Σ tokensApprox(mcp) < Σ tokensApprox(rg) over the tasks both arms got right.
