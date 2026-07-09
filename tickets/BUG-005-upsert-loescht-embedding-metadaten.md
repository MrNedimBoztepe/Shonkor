# BUG-005 — `INSERT OR REPLACE` wipes embedding versioning and enrichment on every re-upsert

**Severity:** High · **Status:** Confirmed · **Area:** Storage / Enrichment

## Context

The REPLACE column list in `UpsertNodesAsync` ([SqliteGraphStorageProvider.cs:104-105](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs)) contains neither `EmbeddingDim` nor `EmbeddingModel` → both become NULL on every upsert. `MarkStaleEmbeddingsForReembedAsync` (lines 1509-1517) deliberately skips `EmbeddingDim IS NULL` → surviving embeddings are invisible to the stale detector; a dimension-preserving model switch silently mixes vectors from two models. Additionally: `NeedsSemanticAnalysis = 1` is hardcoded (line 148), and `Summary`/`Embedding` are overwritten with the (usually empty) values of the incoming node → every re-index throws away paid-for LLM enrichment.

Aggravating factor: `POST /api/interactions/status` ([StatsEndpoints.cs:96-109](../src/Shonkor.Web/Endpoints/StatsEndpoints.cs)) loads a node (the mapper reads no embedding, [SqliteRowMapper.cs:51-63](../src/Shonkor.Infrastructure/Storage/SqliteRowMapper.cs)) and upserts it back → destroys the embedding; accepts arbitrary node IDs.

## Reproduction

1. Create a node with an embedding + `EmbeddingDim/Model`; re-index the file unchanged → `EmbeddingDim/Model` are NULL, `NeedsSemanticAnalysis = 1`.
2. `POST /api/interactions/status` on an arbitrary node ID → `Embedding` is NULL.

## Fix

`INSERT … ON CONFLICT(Id) DO UPDATE SET …` with COALESCE semantics: only overwrite `Summary`/`Embedding`/`EmbeddingDim`/`EmbeddingModel` when the incoming node provides values; set `NeedsSemanticAnalysis = 1` only when `ContentHash` changed. Switch the `interactions/status` endpoint to a targeted `UPDATE Nodes SET Metadata = …` instead of a full-node roundtrip. (Same change as BUG-002 — implement together.)

## Acceptance Criteria

- [ ] Re-indexing an unchanged file preserves Summary, Embedding, Dim, Model and does not set `NeedsSemanticAnalysis`.
- [ ] Re-indexing a changed file invalidates as before.
- [ ] `MarkStaleEmbeddingsForReembedAsync` detects a model switch even after intervening upserts.
- [ ] A node's status update leaves its embedding untouched.

## Definition of Done

- Fix + tests merged; enrichment cost behavior (no re-queue of unchanged nodes) noted in the CHANGELOG.
