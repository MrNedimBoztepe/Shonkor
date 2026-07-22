# Loop-State
Ratifiziert: 2026-07-21 | Filter: gesamtes Backlog

| # | Issue | Titel | Prio | Ready | Status |
|---|-------|-------|------|-------|--------|
| 1 | #295 | JS/TS-D Provenance-Tiering (EXTRACTED/INFERRED/AMBIGUOUS + Integritäts-Invariante) | high | ja | DONE (PR #326, gemergt) |
| 2 | #313 | Installable Packaging + default-active Seeding TS-Plugin (Release-Gate) | high | ja | DONE (PR #329, gemergt) |
| 3 | #319 | Plugin-PostProcessors in MCP-Edit-Loop-Scan verdrahten | medium | ja | DONE (PR #331, gemergt) |
| 4 | #323 | Silent Under-Linking bei ID-Parity-Miss sichtbar machen | medium | ja | DONE (PR #333, gemergt) |
| 5 | #303 | BYO-Node-Onboarding (Discovery, Version-Gate, First-Run, Fehler-UX) | medium | ja | DONE (PR #334, gemergt) |
| 6 | #308 | AssemblyPluginLoadResult optional IAsyncDisposable (async Teardown) | medium+arch | ja | IN PROGRESS |
| 7 | #315 | McpEndpoints-Verdrahtung Plugin-Parser-Merge absichern | medium | ja | OPEN |
| 8 | #309 | Plugin-Teardown-Fehler über Host-Logger sichtbar | low | ja | OPEN |
| 9 | #325 | AC#2-CALLS-Disambiguierungstest härten | low | ja | OPEN |
| 10 | #297 | blast_radius Orphan-JSComponent Redirect-Hint | low | ja | OPEN |
| 11 | #310 | AssemblyPluginLoader-Testabdeckung härten | low | ja | OPEN |
| 12 | #174 | Bench per-case Rank-1-Diff über #172-Grenze | medium | ja | OPEN |

## Übersprungen / decision-gated (nicht im Loop-Scope — Kandidaten für /intake bzw. Stakeholder-Entscheidung)
- #312 — in-host-Parser retire: keine formalen ACs + muss nach #313 (Release-Gate-Kette).
- #185 — decision-gated (Produktentscheidung „überhaupt ändern?").
- #207 — decision-gated (Go/No-Go „if accepted").
- #314 — keine testbaren ACs.
- Framework-Epics #304/#300/#301/#302 — nach Packaging-Gate; brauchen Breakdown.
- ~50 low-Follow-ups (blast_radius/capsule/bench/resilience/storage) — trigger-/messungs-gated, opportunistisch.
