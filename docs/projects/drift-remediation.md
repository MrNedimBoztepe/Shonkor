# Project: Graph drift remediation (incremental relink + freshness)

**Status:** ✅ COMPLETE — Layers 0–4 (freshness, scoped outgoing relink, reverse-index incoming maintenance, background + git-aware reconciliation) plus incremental semantic relink for reconcile batches. Residual: interactive single-file `reindex_file` defers semantic edges to the next reconcile/full-scan (deliberate perf choice). · **Tracked in Shonkor (Brain graph):** see `get_open_threads`

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

**Layer 3 — background reconciliation (eventual consistency). ✅ DONE.** `GraphIndexScanner.ReconcileDriftAsync` = `DetectDriftAsync` (hashes vs graph) then surgically re-indexes only the changed/new/deleted files via `ScanFileAsync` (Layer 1+2 scoped relink), not a whole-tree rescan. A `DriftReconciliationService : BackgroundService` (Shonkor.Web) drives it periodically, catching out-of-band edits (git pull, branch switch, external editor). Opt-in via `Drift:ReconcileIntervalSeconds > 0` (default 0/off, since it hashes candidates each cycle); loads plugins to match the indexer's parser set; skips a project whose scan is already running (`TryBeginScan`). A `FileSystemWatcher` for near-real-time triggering is a possible future refinement.

**Layer 4 — git-aware / explicit-set reconciliation. ✅ DONE.** `GraphIndexScanner.ReconcilePathsAsync(rootDirectory, paths)` re-indexes a KNOWN changed set (from `git diff --name-only` or a webhook push payload) surgically via `ScanFileAsync` — no whole-tree hashing. The GitHub push webhook now extracts `commits[].added/modified/removed` (`ExtractChangedFiles`) and reconciles exactly those, falling back to a full incremental scan when the payload names none.

**Incremental SEMANTIC relink. ✅ DONE (for reconcile batches).** The reconcile paths now refresh exact semantic edges (`CALLS`/`REFERENCES_TYPE`/`IMPLEMENTS`/`EXTENDS`) incrementally. After the per-file `ScanFileAsync` loop, `ReconcilePathsAsync` (in semantic mode) builds **one** compilation for the batch (`SemanticCsharpLinker.BuildCompilationForDirectoryAsync`) and calls `RelinkFilesAsync` for the changed files **plus their type-referencers** (old + new def names, via the reverse index) — clearing those files' outgoing semantic edges and re-emitting only their trees. So a branch switch / pull / push refreshes semantic edges at one-compilation-per-batch cost (not per file, not a whole rescan), and rename/remove danglers from referencers are cleared. `ReconcileDriftAsync` inherits this (it calls `ReconcilePathsAsync`); the `DriftReconciliationService` and the push webhook both pass the semantic flag.

**Residual (minor):** the *interactive* single-file `reindex_file` (`ScanFileAsync` direct) deliberately stays fast — it refreshes structure + name-mode edges immediately but defers semantic edges to the next reconcile (background/webhook/git) or full scan, since a per-keystroke compilation would be too heavy. Incoming danglers from a referencer that uses a renamed symbol *without naming its type* (e.g. via inheritance/extension methods) are caught on the next full scan.

## Phasing
- **P1 (highest ROI, low risk): ✅ DONE.** Layer 0 (freshness tools `is_fresh`/`stale_files`) + Layer 1 (scoped outgoing `REFERENCES_TYPE` relink via `GetDefinitionsByNamesAsync`/`GetNodesByFilePathAsync`, wired into `ScanFileAsync`). Fixes the observed case; makes residual drift non-silent.
- **P2 (reverse index + incoming maintenance): ✅ DONE.** `TypeReferences` reverse-index table + Layer 2 rename/remove/add reconciliation in `ScanFileAsync`, bounded to referencers.
- **P3 (background + git-aware reconciliation + incremental semantic relink): ✅ DONE.** Layer 3 (`ReconcileDriftAsync` + `DriftReconciliationService`), Layer 4 (`ReconcilePathsAsync` + push-webhook wiring), and the incremental semantic relink (one compilation per reconcile batch, scoped to changed files + referencers). Project complete.

## Trade-offs / risks
- Scoped relink needs a fast name→definition lookup, or it degrades to loading the whole graph — `GetDefinitionsByNamesAsync` is the key.
- The reverse index must stay consistent; it's derivable from `referencedTypes`, so it can be rebuilt or maintained incrementally.
- `CALLS` (call_hierarchy) is the **same problem shape** — keep the machinery generic so the future call linker reuses the scoped-relink / reverse-index path. (Synergy with the call_hierarchy project.)
- Strict vs eventual: the per-reindex relink is strict for the edited file; the background worker is eventual for everything else.

## Definition of done
- ✅ After `reindex_file(A)`, `impact_of` / `depends_on` / `find_usages` are correct for symbols **in** A **and** for symbols A references — without a full scan (Layer 1).
- ✅ After a rename/remove in A: no dangling edges to the old symbol; and an added definition resolves referencers' previously-unresolved edges — bounded to the referencers via the reverse index (Layer 2). *(Semantic-mode resolution remains a full-scan/compilation concern — a P3 item.)*
- ✅ `is_fresh` / the drift report (`stale_files`) accurately reflect on-disk vs graph (Layer 0).
