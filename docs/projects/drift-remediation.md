# Project: Graph drift remediation (incremental relink + freshness)

**Status:** P1 + P2 done — Layer 0 (freshness), Layer 1 (scoped outgoing relink), Layer 2 (reverse index + incoming-edge maintenance). P3 planned. · **Tracked in Shonkor (Brain graph):** see `get_open_threads`

## Problem
Cross-graph edges (`REFERENCES_TYPE`, `BINDS_TO`, `CONTROLLER_OF`, `QUERIES_TEMPLATE`, `BELONGS_TO_MODULE`, future `CALLS`) are **not** produced by the parser but by a **whole-graph post-scan linker** (`CrossTechLinker`) that resolves each node's stored `referencedTypes` property against the definition nodes. **`reindex_file` skips this linker.** Hence drift in three forms:

1. **Stale symbols** — a changed/new file was never re-indexed → its symbols are missing (observed live: `GraphPathFinder` "not found").
2. **Missing outgoing edges** — after `reindex_file(A)`, A has **no** `REFERENCES_TYPE` edges (the linker didn't run) until the next full scan.
3. **Stale incoming edges** — on a rename/remove in A, references from other files dangle (preserved by `ClearFileForReindexAsync`, but now pointing at a non-existent node).

## Goals
- Graph matches the working tree after **any** edit, **without** a full rescan per edit.
- Bounded, predictable cost per edit (sub-second, edit-loop-friendly).
- An honest **freshness** signal — the AI knows if a node/file is stale.
- **No silent wrong answers** (e.g. `impact_of` returning 1 instead of 39).

## Solution — layered

**Layer 0 — Freshness as a first-class signal (cheapest, highest immediate value). ✅ DONE.** `GraphIndexScanner.CheckFreshnessAsync(path)` → `Fresh`/`Stale`/`Untracked`/`Deleted` and `DetectDriftAsync(dir)` → `DriftReport(Changed, New, Deleted)`, both using the existing `ContentHash` (on-disk vs stored). Surfaced as the MCP tools **`is_fresh`** and **`stale_files`**. Makes drift **visible instead of silent**. (Follow-up: auto-annotate every read tool's output when the resolved symbol lives in a stale file — currently the two explicit tools cover it.)

**Layer 1 — make `reindex_file` relink-complete (core fix). ✅ DONE (name-mode).** After re-parsing A, `CrossTechLinker.RelinkFileReferenceTypesAsync` recomputes **A's outgoing `REFERENCES_TYPE` edges** against all definitions, using the targeted `GetDefinitionsByNamesAsync(names)` (resolve only the names A references) + `GetNodesByFilePathAsync(A)` instead of `GetAllNodesAsync`. Wired into `ScanFileAsync`. Fixes drift forms 1 & 2 for the default (name-based) resolution. **Open:** semantic mode (`Indexing:SemanticCSharp`) still relinks `REFERENCES_TYPE`/`CALLS` only on a full scan — incremental semantic relink (reuse the compilation, swap one tree) is a later layer; and the other cross-tech edges (`BINDS_TO`/`CONTROLLER_OF`/`QUERIES_TEMPLATE`/`BELONGS_TO_MODULE`) still refresh on a full scan.

**Layer 2 — incremental incoming-edge maintenance via a reverse index. ✅ DONE (name-mode).** A dedicated `TypeReferences(TypeName, NodeId, FilePath)` table maps `typeName -> referencing nodes/files`, maintained inside `UpsertNodesAsync` (from `referencedTypes`) and the delete paths (the JSON `Metadata` column isn't efficiently queryable). On `reindex_file(A)`, `ScanFileAsync` diffs A's definition names before/after (`DefinitionNames`, symmetric difference) and, for every changed name, relinks **only the files that reference it** (`GetReferencingFilePathsAsync` → `RelinkFileReferenceTypesAsync`, now idempotent: it clears the referencer's outgoing `REFERENCES_TYPE` first). This **removes now-dangling edges** on a rename/remove and **creates newly-resolvable edges** when a referenced type is added — bounded to the referencers, not the repo. Fixes drift form 3. (Semantic mode and non-`REFERENCES_TYPE` cross-tech edges still reconcile on a full scan.)

**Layer 3 — background reconciliation (eventual consistency).** A background worker (analogous to `SemanticEnrichmentService`) periodically reconciles File hashes vs disk and re-indexes changed/new/deleted files + scoped relink — catching out-of-band edits (git pull, branch switch, external editor). Optional `FileSystemWatcher` for near-real-time local incremental indexing.

**Layer 4 — git-aware bulk reconciliation.** On branch switch/pull, use `git diff --name-only <old> <new>` (or the webhook push payload) to re-index only the changed files + scoped relink of their referencers, instead of a full rescan. The webhook already does incremental indexing; extend it with the scoped relink.

## Phasing
- **P1 (highest ROI, low risk): ✅ DONE.** Layer 0 (freshness tools `is_fresh`/`stale_files`) + Layer 1 (scoped outgoing `REFERENCES_TYPE` relink via `GetDefinitionsByNamesAsync`/`GetNodesByFilePathAsync`, wired into `ScanFileAsync`). Fixes the observed case; makes residual drift non-silent.
- **P2 (reverse index + incoming maintenance): ✅ DONE.** `TypeReferences` reverse-index table + Layer 2 rename/remove/add reconciliation in `ScanFileAsync`, bounded to referencers.
- **P3:** background reconciliation + FileSystemWatcher / git-diff (Layers 3+4).

## Trade-offs / risks
- Scoped relink needs a fast name→definition lookup, or it degrades to loading the whole graph — `GetDefinitionsByNamesAsync` is the key.
- The reverse index must stay consistent; it's derivable from `referencedTypes`, so it can be rebuilt or maintained incrementally.
- `CALLS` (call_hierarchy) is the **same problem shape** — keep the machinery generic so the future call linker reuses the scoped-relink / reverse-index path. (Synergy with the call_hierarchy project.)
- Strict vs eventual: the per-reindex relink is strict for the edited file; the background worker is eventual for everything else.

## Definition of done
- ✅ After `reindex_file(A)`, `impact_of` / `depends_on` / `find_usages` are correct for symbols **in** A **and** for symbols A references — without a full scan (Layer 1).
- ✅ After a rename/remove in A: no dangling edges to the old symbol; and an added definition resolves referencers' previously-unresolved edges — bounded to the referencers via the reverse index (Layer 2). *(Semantic-mode resolution remains a full-scan/compilation concern — a P3 item.)*
- ✅ `is_fresh` / the drift report (`stale_files`) accurately reflect on-disk vs graph (Layer 0).
