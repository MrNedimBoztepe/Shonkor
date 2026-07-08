# Shonkor – Priorisierte Roadmap

Vier Phasen, jeweils mit Abhängigkeiten und Rollback-Strategie. Prinzip: **Phase 0 misst, bevor Phase 1–3 verändern** — jeder Fix bekommt ein Vorher/Nachher. Tickets in `tickets/`, Details in [improvements.md](improvements.md).

```
Phase 0 (Messen)  ──►  Phase 1 (Quick Wins)  ──►  Phase 2 (Struktur)  ──►  Phase 3 (Skalierung/Optional)
TICKET-201, 202        203, 204, 205, 206,        211, 212, 213           215, SDK-Frage, Reranking-Frage
                       207, 208, 209, 210, 214
```

---

## Phase 0 — Eval-Fundament (zuerst; ~1 Woche)

| Ticket | Inhalt | Warum zuerst |
|---|---|---|
| TICKET-201 | `--answers`-Groundedness-Eval wiederherstellen | Ohne sie ist jeder Grounding-Fix unbelegbar |
| TICKET-202 | Golden-Set entzirkularisieren, hybrid benchen, Coverage-Symmetrie, Gate auf P@1/MRR, CI-Wiring | Baseline für alle Phase-1/2-Deltas |

**Abhängigkeiten:** keine. **Rollback:** trivial — reine Messinfrastruktur, berührt keinen Produktionspfad. Einziger irreversibler Effekt: ehrlichere Zahlen im README (gewollt).

## Phase 1 — Quick Wins (parallelisierbar; ~2–3 Wochen)

Alle Punkte sind klein, lokal und unabhängig voneinander; Reihenfolge innerhalb der Phase nach Präzisions-Hebel:

| Ticket | Inhalt | Aufwand |
|---|---|---|
| TICKET-203 | `ON CONFLICT DO UPDATE`-Upsert + FTS-Query-Sanitizer (K3/H1) | S |
| TICKET-205 | Prompt-Budget + `num_ctx` + Truncation-Detection (K4) | S |
| TICKET-204 | Volle Methoden-Bodies + `signature` (K2/M9) | S |
| TICKET-207 | Provenance-Integrität (K5/M5) | S |
| TICKET-208 | Zeilennummern-Normalisierung (H7) | S |
| TICKET-206 | Zitat-Validierung + Relevanz-Schwelle + History-Fence (H2/H3/H14) | M |
| TICKET-214 | `generate_capsule` auf Budget-Synthesizer (H10) | S |
| TICKET-209 | MCP-Sicherheit: Containment, set_project, Bypass, record (H8/H9/M11/M12) | M |
| TICKET-210 | MCP-Protokoll: isError, ping, Clamps, Retry-Hygiene (H13/M7/M10) | M |

**Abhängigkeiten:** 204, 207, 208 erfordern anschließend einen **Voll-Re-Index** der bestehenden Datenbanken (einmalig; 208 idealerweise mit `SchemeVersion`-Bump bündeln, dann erzwingt der vorhandene Mechanismus den Reparse automatisch). 206 sollte nach 205 landen (Validierung setzt stabilen Prompt voraus).
**Rollback:** Jedes Ticket ist ein eigener PR mit eigenem Feature-Verhalten; 203 ist die einzige zentrale Semantik-Änderung — Rollback = Revert des Upsert-Statements (FTS-Rebuild beim nächsten Start heilt Reste). 206-Schwelle als Config mit Default „aus" ausrollen, per Eval kalibrieren, dann Default „an".

**Gate am Phasenende:** Phase-0-Suite erneut laufen lassen; erwartet werden messbare Verbesserungen bei Must-Cite-Recall (205/206), NL-Retrieval (204) und Citation-Genauigkeit (208). Regression irgendwo → betreffendes Ticket zurückrollen.

## Phase 2 — Strukturelle Umbauten (sequenziell; ~4–6 Wochen)

| Ticket | Inhalt | Abhängigkeit |
|---|---|---|
| TICKET-211 | Markdown-Sektions-Chunking + Summary-in-FTS + Concept-Embeddings (H6/M8) | Eval Phase 0 (Doku-Fälle ins Golden-Set) |
| TICKET-212 | Embedding-Lifecycle: per-Node-Hash-Carry-over, CLI-Filter, stdio-Re-Embed, Race-Guard, Queue-Attempts, Ein-Transaktions-Replace, Plugin-File-Node-Verbot (H11/M1–M3) | 203 (Upsert-Semantik zuerst stabil) |
| TICKET-213 | Kanten-Kanonisierung: implementations_of, Phantom-Hubs, JS-Import-Resolution, Id-Schema (Arity/Ordinal/Partials), Traversal-Filter (H4/H5/M4/M6/M15) | 208 (bündelt den zweiten SchemeVersion-Bump — **beide Id-relevanten Änderungen in einem Bump**, sonst zwei Voll-Reparses beim Nutzer) |

**Rollback:** 212 hinter Feature-Flag (`Indexing:PerNodeHashCarryover`) — bei Fehlverhalten Flag aus = heutiges Wipe-Verhalten (korrekt, nur teuer). 213 ist per Definition nicht heiß zurückrollbar (Id-Schema); Absicherung stattdessen: Graph-Diff-Test vor Merge (Voll-Index vorher/nachher auf Shonkor selbst; erwartete Kanten-Deltas explizit auflisten) + die Phase-0-Suite. 211 rein additiv, Rollback = Revert + FTS-Rebuild.

**Gate:** Seed-Survival, `implementations_of`-Recall (neue Golden-Fälle!), Edit-Loop-Szenariotest (Edit → reindex_file → search_semantic findet den neuen Code).

## Phase 3 — Skalierung & bewusste Entscheidungen (bei Bedarf)

| Thema | Trigger | Inhalt |
|---|---|---|
| TICKET-215 | Graphen > ~20k Knoten oder Latenz-Beschwerden | Vektor: Normalisierung + MemoryMarshal sofort, Matrix-Cache, ggf. sqlite-vec; CTE auf UNION-Branches |
| MCP-C#-SDK / Streamable HTTP | Remote-MCP wird strategisch (mehrere externe Clients) | Ersetzt handgerollten Handler + löst Session-Problem strukturell; sonst nicht nötig |
| Cross-Encoder-Reranking | Erst wenn Phase-0-Eval nach Phase 1/2 noch Präzisionslücken im Top-K zeigt | Messbar einführen, nicht auf Verdacht |
| nomic-Prefix-Umstellung | Ergebnis des korrigierten A/B (Teil von 202) | Falls Gewinn: Prefix-Paar in Modell-Stempel, gesteuertes Re-Embed |

**Rollback:** alles hier ist opt-in bzw. hinter Config; Vektor-Cache degradiert bei Bugs auf den heutigen Brute-Force-Pfad.

---

## Bewusst verschoben / abgelehnt

- **Graph-Store-Wechsel (Neo4j o. ä.):** kein gemessener Bedarf; SQLite-CTE ist korrekt und offline. Abgelehnt.
- **Externer Vektorstore:** bricht das Offline-Versprechen; erst weit jenseits 1M Knoten diskutieren. Abgelehnt.
- **LLM-Judge-Faithfulness als CI-Gate:** zu flakey auf lokalen Modellen; nur als Reporting-Zeile (eval-plan §1C). Verschoben.
- **Konsolidierung der MCP-Tool-Palette** (31 Tools, Überlappungen `locate`/`search_graph`, `edit_plan`/`rename_plan`): nice-to-have, aber die Beschreibungen differenzieren gut; erst angehen, wenn Telemetrie Fehlgriffe der Agenten zeigt. Verschoben.
