# #110 — the code-intent "regression" was benchmark self-contamination, not doc-vs-code ranking

## What #110 assumed

TICKET-211 (#109) gave `MarkdownSection` nodes a body and measured a code-intent regression: FTS P@1
`0.061 → 0.030`, hybrid Recall@10 `0.788 → 0.758`. The hypothesis was that section bodies now **compete with
code nodes in the BM25 pool** and outrank them, and the proposed fix was a type-aware down-weight of
`MarkdownSection`.

## What Phase 0 actually found

Inspecting the real top hits for code-intent queries (not just the aggregate), the #1 hit was almost always
the golden set's **own JSON file** — `bench/golden/agent-queries.json` **contains every query string
verbatim**, so its File node is a guaranteed false #1 hit for keyword search. The eval's ground truth was in
the retrieval corpus. This is circular (the concern TICKET-202 raised), and it had been silently distorting
the code-intent numbers all along.

### Isolated A/B — two FRESH graphs, differing ONLY in whether `bench/golden/` is indexed

(Both freshly indexed so neither carries stale Concept nodes; 33-case `agent-queries.json`.)

| Metric | golden **in** corpus | golden **excluded** |
|---|---:|---:|
| FTS P@1 | 0.030 | **0.061** |
| Semantic P@1 | 0.485 | 0.485 |
| Hybrid P@1 | 0.091 | **0.485** |
| Hybrid MRR | 0.361 | **0.590** |
| Hybrid Recall@10 | 0.818 | 0.818 |

- **FTS P@1 halves** (0.061 → 0.030) purely from the golden File node ranking #1. Removing it recovers exactly
  the pre-#109 value #110's AC targets.
- **Hybrid P@1 drops 5×** (0.485 → 0.091): the golden file's FTS rank-1 pulls it up through RRF fusion,
  displacing the correct code node.
- **Semantic P@1 is identical** (0.485) — the golden file's embedding doesn't rank #1, so vector search was
  never affected by it. (The lower semantic numbers seen on an older working DB were stale duplicate Concept
  nodes, an unrelated accumulation, not this.)

**The contaminants are File nodes (golden fixtures) and Concept nodes — not `MarkdownSection` nodes.** A
type-aware down-weight of sections, the ticket's proposed fix, would not have moved any of these numbers.

## What actually shipped

1. **Exclude `bench/golden/**` from the indexed corpus** (`shonkor.json`): the eval's ground-truth files are
   pure test data and should never be retrievable — for the bench *or* for an MCP user querying this repo.
2. **A bench-side guard** (`RetrievalBenchmark.IsGoldenFixture`): results under `bench/golden/` are dropped
   before ranking, so the eval is trustworthy even against a graph indexed without the exclude. On the
   contaminated graph the guard alone reproduces the clean numbers above — no re-index needed.

### ACs, on the de-contaminated measurement

| AC | Target | Result |
|---|---|---|
| code-intent FTS P@1 | ≥ 0.061 | **0.061** ✓ |
| code-intent hybrid Recall@10 | ≥ 0.788 | **0.818** ✓ |
| doc-sections Recall@10 (FTS) | 1.000 | **1.000** ✓ (unchanged — #86 not undone) |
| `--baseline` CI gate | exit 0 | **exit 0** ✓ (self-retrieval FTS P@1 0.885, unaffected) |

## The type-weight was NOT added — deliberately

AC4 requires any weight to be "a named constant with a comment stating why — no unexplained tuning". Since the
aggregate regression was contamination, not section competition, a `MarkdownSection` down-weight would be a
tuned constant fixing a non-problem — exactly what AC4 forbids.

A **real but isolated** doc-vs-code competition does exist in semantic search: e.g. `search_semantic
"incrementally re-index changed files into the graph"` ranks doc sections ("Incremental Updates",
"6.1 Incremental Indexing") above `GraphIndexScanner`. But it is inconsistent (other code queries rank code
cleanly), it does not move the aggregate once de-contaminated, and down-weighting sections risks doc recall
for an unproven gain. The type-weight mechanism is therefore **deferred**, to be reconsidered only if a
reproducible, de-contaminated doc-outranks-code case appears — tracked, not implemented on a guess.
