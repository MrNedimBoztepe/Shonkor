# BUG-001 — NaN poisons the semantic search Top-K heap; a second NaN → infinite loop

**Severity:** Critical · **Status:** Confirmed (Logic) · **Area:** Retrieval

## Context

`SearchSemanticAsync` ([SqliteGraphStorageProvider.cs:390-404](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs)) computes `TensorPrimitives.CosineSimilarity` without a NaN guard. A zero-norm vector in the DB (corrupt blob, model returns a zero vector) produces NaN (0/0):

1. `Comparer<double>` sorts NaN below everything → NaN becomes `heap.Keys[0]` and is never evicted. Once the heap is full, `score > NaN` is `false` for every real score → Top-K silently degenerates to "the first 4×maxResults rows in table order".
2. Second NaN: `while (heap.ContainsKey(key)) key = Math.BitIncrement(key)` — `BitIncrement(NaN)` stays NaN → infinite loop, request thread permanently occupied (threadpool starvation under load).

## Reproduction

1. Upsert a node with `Embedding = new float[768]` (all 0).
2. Run `search_semantic` until the heap fills → ranking matches table order.
3. Add a second zero-vector node, search again → the request never returns.

## Fix

Right after the score computation: `if (double.IsNaN(score)) continue;`. Additionally (defense in depth): reject zero-norm vectors on write (`UpdateNodeEmbeddingAsync`/Upsert) or store them as NULL.

## Acceptance Criteria

- [ ] A zero vector in the DB does not affect the ranking of the remaining hits.
- [ ] Two or more zero vectors do not lead to a hang/timeout; the search terminates normally.
- [ ] Unit test: dataset with ≥2 zero embeddings + regular embeddings → correct Top-K, termination.

## Definition of Done

- Fix + regression test merged; test runs in CI.
- Short note in the CHANGELOG (behavior with degenerate vectors).
