# Project: Graph drift remediation (incremental relink + freshness)

**Status:** Planned · **Owner:** TBD · **Tracked in Shonkor (Brain graph):** see `get_open_threads`

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

**Layer 0 — Freshness as a first-class signal (cheapest, highest immediate value).** New `is_fresh(path)` + a drift report (`stale_files`) using the existing `ContentHash` (on-disk vs stored, plus on-disk-not-in-graph / in-graph-but-deleted). Annotate tool outputs when a resolved symbol lives in a stale file: "(graph may be stale for this file — reindex_file)". Makes drift **visible instead of silent**.

**Layer 1 — make `reindex_file` relink-complete (core fix).** After re-parsing A, run a **scoped relink** that recomputes A's outgoing cross-graph edges. Refactor `CrossTechLinker` to operate on a node subset (A's nodes as the source) while resolving against all definitions. Keep it cheap with a targeted storage query `GetDefinitionsByNamesAsync(names)` (resolve only the names A references) instead of `GetAllNodesAsync`. Fixes drift forms 1 & 2.

**Layer 2 — incremental incoming-edge maintenance via a reverse index.** Maintain `typeName -> {referencing nodes/files}` as a small dedicated table (populated during indexing from `referencedTypes`; the JSON `Metadata` column isn't efficiently queryable). When A's symbol set changes, enqueue **only the files that actually reference it** for a scoped relink — bounding work to the referencers, not the repo. Fixes drift form 3 (incl. removing dangling edges).

**Layer 3 — background reconciliation (eventual consistency).** A background worker (analogous to `SemanticEnrichmentService`) periodically reconciles File hashes vs disk and re-indexes changed/new/deleted files + scoped relink — catching out-of-band edits (git pull, branch switch, external editor). Optional `FileSystemWatcher` for near-real-time local incremental indexing.

**Layer 4 — git-aware bulk reconciliation.** On branch switch/pull, use `git diff --name-only <old> <new>` (or the webhook push payload) to re-index only the changed files + scoped relink of their referencers, instead of a full rescan. The webhook already does incremental indexing; extend it with the scoped relink.

## Phasing
- **P1 (highest ROI, low risk):** Layer 0 (freshness + annotation) + Layer 1 (scoped outgoing relink, `GetDefinitionsByNamesAsync`). Fixes the observed case; makes residual drift non-silent.
- **P2:** reverse index + Layer 2 (incoming maintenance, rename/remove).
- **P3:** background reconciliation + FileSystemWatcher / git-diff (Layers 3+4).

## Trade-offs / risks
- Scoped relink needs a fast name→definition lookup, or it degrades to loading the whole graph — `GetDefinitionsByNamesAsync` is the key.
- The reverse index must stay consistent; it's derivable from `referencedTypes`, so it can be rebuilt or maintained incrementally.
- `CALLS` (call_hierarchy) is the **same problem shape** — keep the machinery generic so the future call linker reuses the scoped-relink / reverse-index path. (Synergy with the call_hierarchy project.)
- Strict vs eventual: the per-reindex relink is strict for the edited file; the background worker is eventual for everything else.

## Definition of done
- After `reindex_file(A)`, `impact_of` / `depends_on` / `find_usages` are correct for symbols **in** A **and** for symbols A references — without a full scan.
- After a rename in A: no dangling edges to the old symbol; references resolve to the new one (for already-indexed referencers).
- `is_fresh` / the drift report accurately reflect on-disk vs graph.
