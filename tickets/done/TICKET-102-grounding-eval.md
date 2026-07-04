# TICKET-102 — Grounding-Eval: Zitat-Validität, Abstention, Faithfulness
> **STATUS: ✅ DONE (2026-07-02)** — Grounding-Eval (`--answers`): Zitat-Validität 1,00, Must-cite 0,67, Abstention-Recall 0,50 — real gemessen. Build grün, 151 Tests grün.

**Findings:** K2 · **Risiko:** Niedrig × Mittel · **Aufwand:** M · **Abhängigkeiten:** keine

## Kontext
Die RAG-Antwort fordert Zitate `[Name @ file:zeilen]` bei `temperature=0` ([OllamaSemanticAnalyzer.cs:143](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs:143)), aber nichts prüft die Beleg-Treue; `Shonkor.Eval` misst nur Retrieval. „Grounded" ist damit unbelegt.

## Akzeptanzkriterien
- [ ] **Zitat-Validität (deterministisch):** jede Zitat-Referenz in der Antwort muss auf einen tatsächlich übergebenen Kontextknoten zeigen; Metriken: Anteil valider Zitate, Anteil unbelegter Sätze.
- [ ] **Abstention-Recall:** Golden-Fälle mit `is_answerable=false` → Anteil korrekter „nicht belegt"-Antworten.
- [ ] **Optionaler LLM-Judge** (lokaler Ollama, hinter Flag) für Faithfulness/Answer-Relevance.
- [ ] Ergebnisse in `eval/`-Report + Baseline; Regression über Schwelle markiert das PR.
- [ ] Erweitertes Golden-Schema (`context_node_ids`, `answer_must_cite`, `is_answerable`).

## Betroffene Bereiche
`src/Shonkor.Eval` (neue Ebene-2-Metriken), `eval/golden/`, `eval/baseline-metrics.json`, CI.

## Definition of Done
Zitat-Validität + Abstention-Recall haben eine eingecheckte Baseline und laufen reproduzierbar; ein bewusst eingebauter Grounding-Regress wird erkannt.
