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

---

# Roadmap #2 — Restarbeit nach der Umsetzung (Stand 2026-07-02)

Der zweite Review ([shonkor-review.md](shonkor-review.md)) zeigt: die Verbesserungen sind gebaut, aber teils **an den falschen Enden verdrahtet**. Diese Roadmap bringt sie auf den Hauptpfad und macht Grounding messbar.

> **Umsetzungsstand 2026-07-02: alle 7 Tickets (TICKET-101…107) vollständig umgesetzt** (erledigte Tickets: [`tickets/done/`](../tickets/done/)). Build grün (0 Warnungen), **151 Tests grün**, End-to-End verifiziert. Reale Zahlen: [results.md §5b](results.md). Kern: Semantik/Hybrid erreichen jetzt CLI + MCP (`index --embed`, MCP-Embedding-Service, `search_hybrid`-Tool), Grounding ist gemessen (`--answers`), Antworten streamen, Embeddings decken Kopf+Fuß ab, Golden-Set + KI erweitert, Injection-Härtung aktiv. Nebenbefund korrigiert: der frühere „0 % Embeddings"-Befund war ein Messfehler (`ReadNode` lädt die BLOB-Spalte nicht) — jetzt per Direkt-SQL gezählt.

## Phase A — Retrieval-Gewinn auf den Hauptpfad (höchste Priorität)
| Schritt | Ticket | Aufwand | Abhängigkeit |
|---|---|---|---|
| Embeddings im CLI-Index (opt-in) + EmbeddingService in den stdio-MCP-Server | TICKET-101 | M | — |
| `search_hybrid` als MCP-Tool + Dashboard-Anbindung | TICKET-103 | S–M | 101 |
- **Rollback:** beides additiv/flag-gesteuert — Embedding-Schritt ist opt-in (`--embed`), MCP-Embedding nur bei erreichbarem Backend; UI-Toggle bleibt abwählbar. Kein Datenverlust.
- **Exit:** `search_semantic`/`search_hybrid` sind im Agenten-Pfad nutzbar, wenn Embeddings vorhanden; sonst sauberer FTS-Fallback.

## Phase B — Grounding messbar machen
| Schritt | Ticket | Aufwand | Abhängigkeit |
|---|---|---|---|
| Faithfulness/Abstention/Zitat-Validitäts-Eval (Ebene 2) | TICKET-102 | M | — |
| Golden-Set vergrößern/diversifizieren | TICKET-106 | M | 102 |
- **Rollback:** rein additive Eval + Datendateien; nicht-blockierend in CI.
- **Exit:** Zitat-Validität + Abstention-Recall haben eine Baseline; Regression sichtbar.

## Phase C — Antwort-UX & Präzisionsdetails
| Schritt | Ticket | Aufwand | Abhängigkeit |
|---|---|---|---|
| LLM-Antwort streamen (Dashboard) | TICKET-104 | M | — |
| Semantisches Chunking für große Symbole | TICKET-105 | M | 101, 102 |
| Prompt-Injection-Rahmung im RAG-Kontext | TICKET-107 | S | — |
- **Rollback:** Streaming hinter Flag; Chunking über Eval abgesichert; Rahmung ist reine Prompt-Änderung.

## Abhängigkeitsgraph (Runde 2)
```
TICKET-101 (Embeddings/MCP) ──┬─> TICKET-103 (Hybrid überall)
                              └─> TICKET-105 (Chunking)
TICKET-102 (Grounding-Eval) ──┬─> TICKET-106 (Golden-Set)
                              └─> TICKET-105 (Chunking, zum Messen)
TICKET-104 (Streaming) — unabhängig
TICKET-107 (Injection-Rahmung) — unabhängig
```

## Leitplanke (unverändert)
- Nichts ohne Eval ausliefern; alles hinter Flags/opt-in; semantische Zahlen nur mit dem Zusatz „erfordert erzeugte Embeddings" kommunizieren.
