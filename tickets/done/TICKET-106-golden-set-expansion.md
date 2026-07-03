# TICKET-106 — Golden-Set vergrößern & diversifizieren
> **STATUS: ✅ DONE (2026-07-02)** — Golden-Set 15→40, 95%-KI im Report, Baseline neu; End-to-End: Semantik Recall@10 0,975 vs FTS 0,250. Build grün, 151 Tests grün.

**Findings:** M2 · **Risiko:** Niedrig × Niedrig · **Aufwand:** M · **Abhängigkeiten:** TICKET-102

## Kontext
Das Golden-Set ist klein (15 Intent-Fälle + 200 Self-Retrieval) und auf shonkor-eigene Namen zugeschnitten (`eval/golden/intent.json`). Punktschätzungen wie „Recall@10 0,93" haben ein breites Konfidenzintervall und sind nicht repräsentativ.

## Akzeptanzkriterien
- [ ] 50–100 kuratierte Fälle über mehrere Repos/Sprachen (C#, JS/TS, PHP, Sitecore-YAML, Markdown) + Cross-Tech + Abstention-Fälle.
- [ ] Fälle aus echten, anonymisierten MCP-Query-Logs gespeist (nicht nur synthetisch).
- [ ] Report weist Konfidenzintervalle / n je Metrik aus.
- [ ] Baseline entsprechend neu erhoben.

## Betroffene Bereiche
`eval/golden/`, `Shonkor.Eval` (CI-Reporting), Doku (results.md-Zahlen relativieren).

## Definition of Done
Metriken sind statistisch belastbarer; results.md nennt n + CI; Baseline aktualisiert.
