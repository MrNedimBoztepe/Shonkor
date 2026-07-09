# TICKET-202 – Fix retrieval eval: circularity, hybrid, coverage symmetry, CI gate

**Severity ref:** K1, M1 · **Effort:** M · **Risk:** low × medium (numbers become more honest = worse; README must follow)

## Context
1. `bench/golden/doc-intent.json` is circular for the vector retriever: query = `<summary>` text of the target (`GoldenSetGenerator.cs:38,50-53`), which sits in the embedding document via `EmbeddingTextBuilder` (`:40,52`). P@1 0.88 is an upper bound for doc-comment self-matching.
2. `search_hybrid` (the default mode) is never benchmarked.
3. `RagBaselineBenchmark.cs:79-83` measures Shonkor's coverage on the pre-budget subgraph, the baseline on delivered chunks — the +21-pp claim is asymmetric.
4. Gate metric P@k is ineffective at 1 relevant/k=10 (max 0.1) with a 0.03 tolerance (`Program.cs:157-172`); no CI wiring; without Ollama, vector rows are silently skipped (`RetrievalBenchmark.cs:44-49`).
5. The prefix A/B that yielded "within noise" embedded queries with the document prefix (`RetrievalBenchmark.cs:68` → kind-less overload).

## Acceptance Criteria
- [ ] New set `intent-paraphrased.json` (~150 cases): LLM-paraphrased queries + automatic circularity check (no query shares > 4 content words with the target's embedding document); sample review documented.
- [ ] Sets `agent-queries.json` (≥ 30 real MCP queries, hand-labeled) and `negatives.json` (≥ 20 cases with no hit in the graph) created.
- [ ] `search_hybrid` as a third retriever row in `RetrievalBenchmark`.
- [ ] Coverage measurement checks the delivered capsule text (node-header/signature string check); new metric seed-survival rate.
- [ ] Query embedding in the benchmark uses `EmbeddingKind.Query`; prefix A/B re-measured on the paraphrased set and result documented (TICKET-215/V15 follow-up decision).
- [ ] Gate on P@1/MRR/Recall@10 relative (> 5 %), P@k removed from the gate.
- [ ] CI: PR job builds the fixture DB (the repo indexes itself) and gates FTS rows; nightly job (Ollama) gates vector/hybrid; missing Ollama in the nightly = hard fail, no silent skip.
- [ ] README benchmark tables switched to the new numbers, each with commit/date of the run; unsubstantiated "~88 %" claim removed or backed by a stored run.

## Affected Areas
`src/Shonkor.Bench/**`, `bench/golden/`, `.github/workflows/`, README.

## Dependencies
None. In parallel with TICKET-201.

## Definition of Done
Two identical nightly runs, baselines checked in, CI green, README updated.
