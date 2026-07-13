# TICKET-215 Stage 1 — vector search, measured before/after

Synthetic graph of random unit-ish `float[768]` embeddings (nomic-embed-text's dimension). Both paths run
against the **same** database whose vectors were normalized on write, so scoring is identical — this is a
pure performance comparison, not a quality one. "OLD" reproduces the pre-ticket path inline (per-row
`float[]` copy + `TensorPrimitives.CosineSimilarity` + a ×4 overscan heap); "NEW" calls the shipped
`SearchSemanticAsync` (query normalized once, blob read zero-copy via `MemoryMarshal.Cast`, dot-product
scoring, heap capacity = `maxResults`). 5 runs, warmed up. Allocations via
`GC.GetAllocatedBytesForCurrentThread()`.

| Nodes | | Latency | Alloc / query |
|------:|---|--------:|--------------:|
| **20,000** | OLD | 73.9 ms | 118 MB |
| | NEW | 77.7 ms | **59 MB** |
| **100,000** | OLD | 364.7 ms | 590 MB |
| | NEW | 357.3 ms | **299 MB** |

## What the numbers say (honestly)

- **Allocations halve: −49% at both scales.** The old path made *two* per-row allocations — the blob
  `byte[]` (unavoidable with `Microsoft.Data.Sqlite`) **and** a fresh `float[]` copy of it. Zero-copy removes
  the second exactly, so the reduction is ~50% by construction, and that is what we measure (590→299 MB,
  118→59 MB). This is the real, bankable Stage 1 win — 100k fewer GC-tracked arrays per query.

- **Latency barely moves (0.95–1.02×).** At these sizes the query is dominated by **SQLite page I/O** —
  reading every ~3 KB blob off disk — not by the scoring arithmetic. Removing the copy and swapping
  cosine→dot shaves CPU that isn't the bottleneck. This is exactly why the ticket stages the work: **Stage 1
  bounds allocations; latency needs Stage 2's in-memory matrix cache** (which avoids re-reading every blob
  per query).

## Stage 2 is correctly NOT triggered here

Stage 2's trigger is ">20k nodes or latency >200 ms". At the 20k boundary the query is **78 ms**, and
shonkor's own graph is ~3,700 nodes — an order of magnitude below the trigger. Building the in-memory
matrix cache now would be speculative; Stage 1's exactness-preserving, allocation-halving changes are the
right scope for today, with Stage 2 gated behind a real trigger and its own benchmark (as the ticket
specifies).

## Exactness

Dot product of two L2-normalized vectors **equals** their cosine similarity, so rankings and scores are
identical to the old cosine path for every real (non-degenerate) vector — verified by
`VectorScalingTests.Scoring_PreservesCosineRanking_*` and the retained
`SearchSemantic_ZeroMagnitudeVectors_*` regression. The only behavioural change is intentional: a
non-positive similarity (an orthogonal/degenerate hit) is now excluded rather than returned as noise, and
`search_semantic` applies a default 0.5 floor with the score shown per row.
