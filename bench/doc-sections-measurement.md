# TICKET-211 — doc-section retrieval, measured

Controlled A/B on shonkor's own repo (3,689 nodes; 407 MarkdownSection nodes, of which 8 are `::part::`
continuations from splitting). Both databases carry the **same** code nodes and the **same** embeddings
(`nomic-embed-text`, 3,688 vectors). The only difference:

| | MarkdownSection `Content` | line range | `::part::` nodes | section embedding document |
|---|---|---|---|---|
| **before** | empty | none | none | `MarkdownSection <title>` |
| **after** | the section body | `StartLine`–`EndLine` | present | title + body |

Reproduce: index the repo (`shonkor index . --embed`), copy the db, blank the section bodies and
re-embed them from the title alone, then score both with `bench/golden/doc-sections.json`.

## New golden set: `bench/golden/doc-sections.json` (12 doc-intent cases)

Every query uses vocabulary that appears **only in the section body**, never in its title — otherwise the
title alone would already retrieve it and the measurement would be self-fulfilling. FTS5 ANDs its terms, so
the queries are short and every term is present in the target section.

### Recall@10 (k = 10)

| Mode | before | after |
|---|---:|---:|
| graph (FTS5) | 0.000 | **1.000** |
| semantic (vector) | 0.083 | **0.500** |
| hybrid (RRF) | 0.083 | **1.000** |

### Precision@1

| Mode | before | after |
|---|---:|---:|
| graph (FTS5) | 0.000 | **0.917** |
| semantic (vector) | 0.083 | 0.167 |
| hybrid (RRF) | 0.000 | **0.917** |

Sections were previously unreachable by their own content: only their title was indexed and embedded.

## What it costs (measured, not hidden)

Section bodies now compete with code nodes for the top-10. On the **code**-intent set
(`bench/golden/agent-queries.json`, 33 cases):

| Mode | metric | before | after |
|---|---|---:|---:|
| graph (FTS5) | P@1 | 0.061 | 0.030 |
| semantic | Recall@10 | 0.727 | 0.697 |
| hybrid | Recall@10 | 0.788 | 0.758 |

That is one case each, well inside the ±0.12–0.16 confidence intervals of a 33-case set — but it is a real
directional cost, not noise to be waved away. The CI gate set (200-case exact-name self-retrieval) is
effectively unchanged: FTS P@1 0.900 → 0.895, Recall@10 0.986 → 0.986, hybrid identical.
`--baseline bench/retrieval-baseline.json` still exits 0.

## Honest scope

- The golden set is **keyword-friendly by construction** (body vocabulary, all terms present), which favours
  FTS. It measures "is a section body retrievable at all", not "is vector search good". Semantic recall still
  improves 0.083 → 0.500.
- 12 cases is a small set: the semantic Recall@10 of 0.500 carries a ±0.283 confidence interval.
- No AI summaries exist in this graph, so the new `Summary` FTS column contributes nothing to these numbers.
  Its behaviour is covered by unit tests (`FtsSummaryTests`), not by this benchmark.
