# BUG-007 — Stale-file cleanup deletes data from unrelated directories via path prefix

**Severity:** High · **Status:** Confirmed · **Area:** Ingestion / GraphIndexScanner · **Data loss**

## Context

Two places compare via `indexedFile.StartsWith(directoryPath, OrdinalIgnoreCase)` without a trailing-separator guard ([GraphIndexScanner.cs:190](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs) in scan cleanup, [:379-380](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs) in `DetectDriftAsync`):

- A scan of `C:\Repo` treats indexed files under `C:\Repo2\…` as "under this directory"; since they are not candidates, they get **deleted from the graph**.
- Conversely, `directoryPath` is never normalized with `Path.GetFullPath` (candidates already are) — a relative/differently-shaped path never matches the comparison and silently disables cleanup (stale nodes survive every re-index).

## Reproduction

DB with files from `C:\Repo` and `C:\Repo2` (e.g. via `reindex_file`); run a full scan over `C:\Repo` → `C:\Repo2` nodes are deleted.

## Fix

Shared helper: `directoryPath = Path.GetFullPath(directoryPath)`, then `EnsureTrailingSeparator(…)` and only then `StartsWith`. Switch both places (line 190 and 379).

## Acceptance Criteria

- [ ] Scanning a directory deletes no nodes from name-prefix siblings (`Brain` vs. `Brainstorm`).
- [ ] A relative/trailing-slash `directoryPath` yields the same cleanup behavior as the canonical path.
- [ ] Unit tests for both edge cases (sibling prefix, non-normalized input).

## Definition of Done

- Fix + tests merged.
