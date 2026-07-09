# TICKET-201 – Restore answer-groundedness eval (`--answers`)

**Status:** ✅ Implemented — `src/Shonkor.Bench/AnswersBenchmark.cs` (PR #61, commit `6b42bbb`); tests: `AnswersBenchmarkTests`, `GroundingPrepTests`.

**Severity ref:** K1 · **Effort:** M · **Risk:** low × low

## Context
A groundedness eval (citation validity, must-cite, abstention) existed in `Shonkor.Eval` (introduced in `ccd6c22`) and was removed without replacement during the merge into `Shonkor.Bench` (`009b8d7`) — the commit message says so itself. Since then, nothing measures whether RAG answers are faithful to the supplied context. The orphaned `src/Shonkor.Eval/bin|obj` directories falsely suggest an existing project.

## Acceptance Criteria
- [ ] `shonkor-bench --answers bench/golden/answers.json` runs against the production `BuildRagPrompt` pipeline (same prompt as `/api/ask`), with `temperature=0` and a set `seed`.
- [ ] `answers.json` contains ≥ 40 cases (starting point: the old 7-case set from the git history of `ccd6c22`), of which ≥ 10 are `abstain` cases; schema: `question`, `contextNodeIds`, `kind`, `mustCite`, optional `mustContain`/`mustNotContain`.
- [ ] Metrics in `metrics.json` + `report.md`: citation-validity rate, must-cite recall, abstention recall, abstention precision, uncited-paragraph rate.
- [ ] `--baseline` gates the first four metrics listed (relative drop > 5 % → exit 2).
- [ ] `src/Shonkor.Eval/` (bin/obj remnants) is deleted.

## Affected Areas
`src/Shonkor.Bench/` (new AnswersBenchmark), `bench/golden/`, `OllamaSemanticAnalyzer` (expose prompt reuse as a testable method if needed), docs.

## Dependencies
None hard. Sensible before TICKET-205/206 so their effect is measurable. Ollama required locally.

## Definition of Done
Eval runs reproducibly locally (two runs, identical numbers), baseline checked in, results documented in the report, README "Benchmark" section extended with the groundedness rows.
