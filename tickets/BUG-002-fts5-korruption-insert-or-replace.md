# BUG-002 — FTS5 index corruption: `INSERT OR REPLACE` + external-content FTS without `recursive_triggers`

**Severity:** Critical · **Status:** Confirmed · **Area:** Storage / Full-text search

## Context

`UpsertNodesAsync` uses `INSERT OR REPLACE` ([SqliteGraphStorageProvider.cs:104](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs)); the FTS5 synchronization hangs off `AFTER INSERT/DELETE/UPDATE` triggers ([SqliteSchema.cs:112-150](../src/Shonkor.Infrastructure/Storage/SqliteSchema.cs)). On REPLACE, SQLite fires the DELETE trigger of the displaced row **only** with `PRAGMA recursive_triggers = ON` — which is not set anywhere. Every re-upsert of an existing node therefore leaves a ghost FTS entry behind (old rowid) and creates a new one.

`NodesFts` is external-content (`content=Nodes`): ghost entries read via the stale rowid — on rowid reuse the old search text matches the **wrong node**; otherwise hits disappear. The count-based rebuild guard only repairs things on the next process start.

## Reproduction

1. Index a file, edit it, `reindex_file`.
2. `SELECT COUNT(*) FROM Nodes` vs. `SELECT COUNT(*) FROM NodesFts` → FTS counter is higher.
3. Full-text search for the old content → returns hits even though the content no longer exists.

## Fix

Replace `INSERT OR REPLACE` with `INSERT … ON CONFLICT(Id) DO UPDATE SET …` (preserves rowid, fires the UPDATE trigger). Alternatively/additionally `PRAGMA recursive_triggers = ON` in `OpenConnectionAsync`. **Note:** The `ON CONFLICT` rework is also the fix for BUG-005 — implement them together.

## Acceptance Criteria

- [ ] After n re-upserts of the same node, `COUNT(Nodes) == COUNT(NodesFts)` holds without a rebuild.
- [ ] Full-text search for old (replaced) content returns no hits anymore; new content is found.
- [ ] Integration test: Index → Edit → Reindex → FTS consistency assertion.

## Definition of Done

- Fix + test merged; existing databases are sanitized once on first start via an FTS rebuild (count guard kicks in) — documented in the CHANGELOG.
