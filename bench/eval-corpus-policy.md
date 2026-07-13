# Eval corpus policy — what the retrieval benchmark may and may not retrieve

The benchmark measures retrieval quality on shonkor's **own** repo. That repo contains documents *about*
the code, written in the same vocabulary as the golden queries. If those documents are retrievable, they can
outrank the code they describe — a circular measurement (the eval's answer key is in the corpus). #110 fixed
the acute case (golden files, query strings verbatim); #133 handles the diffuse tail.

## The line: product vs dev-process

| Path | In the graph? | Retrievable by the eval? | Why |
|---|---|---|---|
| `src/**` | ✅ | ✅ | product code — the thing we measure retrieval of |
| `docs/**` (arc42, user guides) | ✅ | ✅ | product documentation; the doc-intent set scores against it |
| `bench/**/*.cs` | ✅ | ✅ | the harness is code, not query-paraphrasing prose |
| `bench/golden/*.json` | ❌ (index-excluded) | ❌ | ground truth — contains query strings **verbatim** (#110) |
| `tickets/**` | ❌ (index-excluded) | ❌ | dev-process prose; legacy (tracking moved to GitHub) |
| `review/**` | ❌ (index-excluded) | ❌ | one-off analysis docs that paraphrase features in query words |
| `bench/**/*.md` | ✅ | ❌ (guard-filtered) | measurement notes (this file, the #110 note…) quote example queries |

Evidence for the tail: `review/shonkor-bug-report.md`, `review/shonkor-review.md`, `tickets/TICKET-*.md`
each contain **100% of the query terms** for many `agent-queries.json` cases — and `bench/code-intent-
decontamination.md` (written for #110) quotes the query `"incrementally re-index changed files into the
graph"` verbatim. Left in the corpus, they crowd out the code.

## Two mechanisms (both, like #132)

1. **Index-time exclusion** (`shonkor.json`): `bench/golden/`, `tickets/`, `review/`. These are non-product
   and carry no agent value that isn't already in GitHub, so removing them cleans the eval graph *and* the
   MCP graph, and — crucially — lets the retriever fetch a full top-k of real nodes (no post-filter
   under-fetch). This is the primary fix.
2. **Bench guard** (`RetrievalBenchmark.IsEvalMetaNode`, applied in `Score`): drops golden / tickets / review
   / `bench/*.md` hits before ranking. Defence-in-depth (a graph built without the excludes is still
   non-circular for P@1/MRR) **and** the mechanism for `bench/*.md`, which stays indexed (entangled with the
   harness source, and only a handful of files).

`docs/` is deliberately kept — excluding it would undo #86's doc-intent retrieval, whose whole point is that
`docs/` sections are retrievable.

## Measured effect (33-case `agent-queries.json`)

| Metric | golden-only excluded (post-#132) | + tickets/review excluded (this PR) |
|---|---:|---:|
| FTS P@1 | 0.061 | **0.091** |
| Hybrid P@1 | 0.485 | **0.515** |
| Hybrid Recall@10 | 0.818 | 0.788 |
| doc-sections Recall@10 | 1.000 | **1.000** (preserved) |

The code-intent gain is ~1 case — well inside the ±0.14 confidence interval, so **not** a proven improvement.
The justification is **methodological, not numerical**: the eval stays trustworthy as `review/`, `tickets/`
and measurement notes accumulate (they increasingly quote queries — the #110 note already does). `--baseline`
exits 0; the 200-case self-retrieval gate is unaffected (those target code symbols).

## For contributors

When adding a document that *describes* shonkor's code (a new review, a measurement note, a design doc), ask:
is it **product documentation** (belongs in `docs/`, retrievable) or **dev-process prose** (belongs in
`tickets/`/`review/`/`bench/*.md`, excluded)? Put it in the right place, or extend `IsEvalMetaNode`. A new
class of meta-doc under a new directory must be added to both the `shonkor.json` excludes and the guard.
