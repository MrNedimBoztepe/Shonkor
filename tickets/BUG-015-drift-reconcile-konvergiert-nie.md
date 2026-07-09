# BUG-015 — Drift reconcile never converges: excluded files are resurrected; new binary/oversized files loop

**Severity:** High · **Status:** Confirmed · **Area:** Ingestion / drift reconciler

## Context

Two independent root causes, same effect (`DriftReport.IsClean` never becomes `true`, the background reconciler works endlessly):

**(a) Excluded files are re-indexed instead of removed.** `DetectDriftAsync` classifies an indexed file that is present on disk but now exclude-matched as `Deleted`; `ReconcileDriftAsync` sends `drift.Deleted` through `ScanFileAsync` ([GraphIndexScanner.cs:395-408](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs)), which does not know exclude patterns and only deletes on "no parser / file missing / too large / binary" (lines 572-585). The file is **re-indexed**, the next drift pass reports it as `Deleted` again — explicitly excluded content stays permanently in the graph and is reprocessed every cycle.

**(b) New binary/oversized files loop.** In `DetectDriftAsync` the size/binary check only applies in the `changed` branch ([GraphIndexScanner.cs:345-361](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs)) — a new >5 MB or binary file with a parseable extension ends up in `New`, `ScanFileAsync` discards it, next pass: `New` again.

## Reproduction

(a) Index a folder, then set an exclude pattern for it, run `ReconcileDriftAsync` twice → both runs process the same files, the graph still contains them. (b) Create a 6 MB `.md` file → every drift pass reports it as `New`.

## Fix

(a) Handle `drift.Deleted` directly via `DeleteByFilePathAsync` + `MaintainReferencersAsync`, or pass exclude patterns through to `ScanFileAsync` and treat "excluded" like "no parser". (b) Apply the size/binary filter to the `added` branch as well.

## Acceptance Criteria

- [ ] After excluding an indexed folder: the first reconcile removes the nodes, the second reconcile is clean.
- [ ] A new binary/oversized file appears in no drift report (or once with an explicit skip status).
- [ ] `IsClean` reaches `true` in both scenarios.

## DoD

- Fix + tests merged.
