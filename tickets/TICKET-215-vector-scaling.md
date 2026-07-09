# TICKET-215 – Scale Vector Search (Normalization, Zero-Copy, Matrix Cache)

**Severity ref:** H12, M6 (SQL part), M3 (score visibility) · **Effort:** S (Stage 1) / M (Stage 2) · **Risk:** low × low (Stage 1), medium × low (cache invalidation)

## Context
`SearchSemanticAsync` (`SqliteGraphStorageProvider.cs:353-435`) reads **every** embedding blob in the DB per query and allocates a fresh `float[]` per row (`:387-388`) before `TensorPrimitives.CosineSimilarity`. At 100k nodes ≈ 300 MB page I/O + 100k allocations per query; `search_hybrid` requests `limit*2`. The top-K heap is exact — so the `overscanFactor=4` (`:366-367`) buys nothing. No similarity floor; MCP output hides scores (`FindTools.cs:197-202`) — noise hits at cosine ~0.3 look like real ones.

## Acceptance Criteria — Stage 1 (immediate)
- [ ] Vectors are L2-normalized on write (existing data: on re-embed or a one-time migration); scoring via dot product.
- [ ] `MemoryMarshal.Cast<byte, float>(blob)` instead of a per-row copy; `overscanFactor` removed.
- [ ] Configurable similarity floor (default ~0.5 for nomic); filtered hits are noted as "weak hits below the threshold hidden"; MCP output shows the rounded score per row.
- [ ] Subgraph CTE as two UNION branches (`e.SourceId = s.Id` / `e.TargetId = s.Id`) → uses `idx_edges_source`/`idx_edges_target` (may alternatively land in TICKET-213 — not duplicated).

## Acceptance Criteria — Stage 2 (Trigger: > ~20k nodes or latency > 200 ms)
- [ ] In-memory (Id, vector) matrix per project with a generation counter, invalidated on upsert/delete; fallback to DB scan on cache miss.
- [ ] Memory budget documented (float32 ~3 KB/node; optional fp16 storage).
- [ ] Benchmark: query latency on a synthetic 100k graph < 100 ms (before/after in the PR).

## Alternative (deliberately deferred)
`sqlite-vec` (ANN, stays SQLite-only) — evaluate if Stage 2 is not enough; an external vector store breaks the offline promise and is only debatable well beyond 1M nodes.

## Affected Areas
`SqliteGraphStorageProvider.cs`, `OllamaEmbeddingService.cs`/`SemanticEnrichmentService.cs` (normalization on write), `FindTools.cs`, bench.

## Dependencies
Stage 1: none. Stage 2: after TICKET-212 (cache invalidation needs its clean upsert paths). Floor calibration via TICKET-202 `negatives.json`.

## Definition of Done
Stage 1 changes merged, retrieval metrics unchanged (exactness preserved), latency/allocations measured before/after; Stage 2 only on trigger, with its own benchmark.
