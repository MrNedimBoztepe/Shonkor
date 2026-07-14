# #135 — orphaned concept nodes halved semantic P@1; pruning recovers it

## The mechanism (Phase 0, verified at the code)

`Concept` nodes are created by the enrichment worker with a **RELATES_TO edge from a code node**
(`SqliteGraphStorageProvider.UpdateNodeSemanticDataAsync`). But concepts carry **no `FilePath`**, and every
cleanup path is path-based:

- `ClearFileForReindexAsync` / `DeleteNodesByFilePathsAsync` delete `WHERE FilePath = …` and then the edges
  touching those nodes. The code node and its RELATES_TO edge go; **the concept node survives** (no FilePath
  matches).

So whenever the code that referenced a concept is re-indexed or deleted, the concept is **orphaned** (0
incoming RELATES_TO) and lingers forever. Orphans are embedded (the `--embed` pass / concept-embedding step),
so they keep ranking in semantic search despite meaning nothing.

## Quantified on the working DB

The long-lived `shonkor.db` (many CLI re-indexes across this session's tickets):

```
concept nodes:                            1499
ORPHAN concepts (no incoming RELATES_TO): 1499   ← every single one
near-duplicate name groups:                  3   (e.g. "CancellationToken" / "Cancellation Token")
```

All 1499 were orphans — CLI `shonkor index` clears code edges but never re-links concepts (that's the web
enrichment worker), so a re-index without a following enrichment orphans the whole concept set.

## Before / after pruning (33-case `agent-queries.json`, same DB)

| Metric | before (1499 orphans) | after prune (0 orphans) |
|---|---:|---:|
| **Semantic P@1** | 0.242 | **0.485** |
| Semantic MRR | 0.402 | **0.570** |
| Hybrid P@1 | 0.273 | **0.485** |
| Hybrid MRR | 0.446 | **0.591** |
| doc-sections Recall@10 | 1.000 | **1.000** (preserved) |

Pruning **doubles** semantic P@1 (0.242 → 0.485) — well outside the ±0.15 confidence interval, and exactly
matching the degradation the ticket observed. The long-lived DB now matches a fresh index (semantic P@1
0.485), satisfying the "no degradation from accumulation" acceptance criterion.

## The fix

1. **`PruneOrphanConceptsAsync`** (storage): `DELETE FROM Nodes WHERE Type='Concept' AND NOT EXISTS (a
   RELATES_TO edge into it)`.
2. **Called from the enrichment worker** when a project has **nothing pending** — i.e. all current code is
   enriched, so a still-orphaned concept belongs to deleted/changed code and is truly stale. Deliberately not
   pruned mid-reindex: a concept a still-pending node would re-link must not be mistaken for stale.
3. **Normalized concept id** (`ConceptId`): lowercase + strip non-alphanumeric, so near-duplicate phrasings
   ("Command-Line Interface" / "Command Line Interface") collapse to one id and dedup via `INSERT OR IGNORE`.

## Scope note

The prune is tied to the **enrichment cycle** (which knows the current concept set), not the raw CLI index.
A CLI-only workflow that re-indexes without ever running the web enrichment worker will still accumulate
orphans until enrichment runs — the correct remedy there is to run enrichment (re-link current concepts,
prune the rest), not to have a bare index silently delete concepts it can't re-create. Filed as a
consideration in the PR.
