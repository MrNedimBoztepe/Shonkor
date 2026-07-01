# Roadmap — Präzision & Grounding

Priorisiert: **Quick Wins → strukturelle Umbauten**. Jede Phase mit Abhängigkeiten und Rollback. Die ursprünglichen Arbeitstickets (TICKET-001…008) wurden nach vollständiger Umsetzung entfernt; ihr Ergebnis ist im [CHANGELOG](../CHANGELOG.md) und in [results.md](results.md) dokumentiert. Die TICKET-Kennungen unten bleiben als historische Referenz.

> **Umsetzungsstand 2026-06-30:** Phase 0 ✅ · Phase 1 ✅ · Phase 2: 002 ✅, 003 ✅, **004 ✅** (Ambiguitäts-Diagnose + non-lossy Semantik-Modus + **globaler Default auf semantische Auflösung geflippt**, Latenz-Tradeoff gemessen) · Phase 3 ✅. **8 von 8 Tickets vollständig.** Gemessene Werte: [results.md](results.md). Build grün, **140 Tests grün**, Web-Dashboard verifiziert. Semantik-Default: +3,6 s Indexierung (2,9×) für +50 % präzisere Kanten; per `Indexing:SemanticCSharp=false` rückschaltbar.

## Phase 0 — Messbarkeit & Ehrlichkeit (Voraussetzung für alles)
**Ziel:** „präzise" wird eine Zahl; keine falschen Claims mehr.
| Schritt | Ticket | Aufwand | Abhängigkeit |
|---|---|---|---|
| Eval-Harness Ebene 1 + Golden-Set v0 + Baseline | TICKET-001 | M | — |
| Benchmark-Claim relativieren / zurücknehmen | TICKET-007 | S | — |
- **Rollback:** Eval ist additiv (neues Projekt + Datendateien) — bei Problemen einfach nicht in CI verdrahten; kein Produktivcode betroffen. README-Änderung per Revert.
- **Exit-Kriterium:** `shonkor eval` läuft lokal grün, Baseline eingecheckt.

## Phase 1 — Quick Wins am Antwort-/Embedding-Pfad
**Ziel:** Grounding und Retrieval-Hygiene ohne große Umbauten.
| Schritt | Ticket | Aufwand | Abhängigkeit |
|---|---|---|---|
| RAG-Antwort: Zitate + `temperature=0` + saubere Truncation | TICKET-005 | S–M | TICKET-001 (zum Messen) |
| Embedding-Versionierung + Re-Embed + nomic-Prefixe | TICKET-006 | S | — |
| SaaS-`/api/rag/query` Grounding-Preamble | TICKET-005 (Teil) | S | TICKET-005 |
- **Rollback:** Prompt-/Request-Änderungen sind reine String-/Param-Änderungen → per Revert sofort zurück. Embedding-Versionsfeld ist additive Spalte (nullable) → abwärtskompatibel.
- **Exit-Kriterium:** Abstention-Fälle bestehen; Antworten reproduzierbar; keine stillen Dim-Skips mehr.

## Phase 2 — Kern-Präzision: Embedding-Quelle, Kontext-Budget, Graph-Default
**Ziel:** Die drei Kritisch-/High-Findings strukturell beheben.
| Schritt | Ticket | Aufwand | Abhängigkeit |
|---|---|---|---|
| Embeddings auf Code/Signatur statt Summary (+ Re-Embed) | TICKET-002 | M | TICKET-001, TICKET-006 |
| Capsule: Token-Budget + Distanz/Score-Ranking + Hub-Schutz | TICKET-003 | M | TICKET-001 |
| Semantisches C#-Linking als Default + Konfidenzflag | TICKET-004 | M | TICKET-001 |
- **Rollback:** Jeweils hinter Feature-Flag/Config einführen (`Embedding:Source=code|summary`, `Capsule:TokenBudget`, `Indexing:SemanticCSharp`). Bei Regression in der Eval → Flag zurück auf alten Wert, kein Re-Deploy nötig. Re-Embed ist idempotent (Versionsfeld), Alt-Vektoren bleiben bis Ersatz da.
- **Exit-Kriterium:** Precision@10 (semantisch & hybrid) und Faithfulness messbar über Baseline; Token-Budget greift auf Hub-Knoten.

## Phase 3 — Hybrid-Retrieval & Reranking
**Ziel:** FTS + Vektor + Graph zu einer gerankten Pipeline fusionieren.
| Schritt | Ticket | Aufwand | Abhängigkeit |
|---|---|---|---|
| RRF-Fusion FTS∪Vektor + Top-N-Seeds; Cross-Encoder-Rerank optional | TICKET-008 | M | TICKET-002, TICKET-003 |
- **Rollback:** Neuer `mode=hybrid` zusätzlich zu bestehenden Endpunkten; alte Modi bleiben. Reranker hinter Flag. Abschalten = Flag aus.
- **Exit-Kriterium:** Hybrid schlägt beide Einzelmodi in der Eval (sonst nicht ausliefern).

## Abhängigkeitsgraph (Kurz)
```
TICKET-001 (Eval) ──┬─> TICKET-005 (RAG-Grounding)
                    ├─> TICKET-002 (Code-Embedding) ─┐
                    ├─> TICKET-003 (Kontext-Budget) ─┼─> TICKET-008 (Hybrid+Rerank)
                    └─> TICKET-004 (Semantik-Default)┘
TICKET-006 (Embed-Versioning) ──> TICKET-002
TICKET-007 (Benchmark) — unabhängig
```

## Gesamt-Leitplanke
- **Nichts ohne Eval ausliefern:** Jede Phase-1/2/3-Änderung muss in der TICKET-001-Eval ≥ Baseline liegen, sonst Flag aus.
- **Alles hinter Flags:** Ermöglicht inkrementellen Rollout und sofortigen Rollback ohne Datenverlust (Re-Embed idempotent, Alt-Kanten/-Vektoren bleiben bis Ersatz).
