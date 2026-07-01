# Eval-Plan — schlanke, wiederholbare Präzisions-Evaluation

**Ziel:** „präzise" von einer Behauptung zu einer Zahl machen, die bei jeder Änderung neu berechnet wird. Offline, deterministisch, im shonkor-Stil (kein Cloud-LLM-Zwang).

## Zwei Ebenen messen

### Ebene 1 — Retrieval (deterministisch, kein LLM)
Misst, ob die *richtigen* Knoten geholt werden — das Kern-USP.
- **Metriken:** Precision@k, Recall@k, MRR (k = 5, 10). Getrennt für `search_graph` (FTS), `search_semantic` (Vektor) und den Hybrid-/Fusionspfad (V5).
- **Warum kein LLM:** Erwartung sind konkrete Knoten-IDs/Symbole → exakt vergleichbar, reproduzierbar, schnell, CI-tauglich.

### Ebene 2 — Antwort (RAG-Grounding, optionaler LLM-Judge)
Misst, ob die generierte Antwort durch den Kontext gedeckt ist.
- **Metriken:**
  - **Faithfulness/Groundedness:** Anteil der Aussagen, die durch Kontextknoten belegt sind.
  - **Answer-Relevance:** Beantwortet die Antwort die Frage?
  - **Abstention-Korrektheit:** Sagt das System bei nicht-belegbarer Frage korrekt „weiß ich nicht"? (eigene Negativ-Fälle, siehe unten)
- **Judge:** Für lokalen Offline-Lauf ein deterministischer Heuristik-Check (Zitat-Labels aus V6 vorhanden? Verweisen sie auf gelieferte Knoten?) + optional ein LLM-Judge (RAGAS oder lokaler Ollama-Judge) hinter Flag.

## Golden-Set aufbauen
- **Umfang Start:** 30–50 Fälle, je Sprache/Parser-Familie abgedeckt (C#, JS/TS, PHP, Sitecore-YAML, Markdown) + Cross-Tech-Fälle.
- **Quelle:** shonkors **eigene** Repo-DB (`shonkor.db`) als erstes Korpus — bekannt, stabil, versioniert.
- **Fall-Schema** (`eval/golden/*.json`):
  ```json
  {
    "id": "csharp-impact-roslynastparser",
    "query": "Welche Typen referenzieren RoslynAstParser?",
    "mode": "search_graph|search_semantic|hybrid|ask",
    "expected_node_ids": ["...", "..."],
    "expected_must_contain": ["REFERENCES_TYPE"],
    "answer_must_cite": ["RoslynAstParser"],
    "is_answerable": true
  }
  ```
- **Negativ-/Abstention-Fälle:** Fragen, deren Antwort **nicht** im Graph steht (`is_answerable=false`) → erwartet wird eine „nicht belegt"-Antwort. Direkt der Test für H2-Grounding.
- **Kuratierung:** Erwartungen einmalig manuell aus dem Graph belegen (mit `find_usages`/`call_hierarchy`), per Review fixieren. Set wächst aus realen Agent-Queries (aus MCP-Logs anonymisiert).

## Runner & Integration
- **Projekt:** `src/Shonkor.Eval` (Konsole), nutzt `IGraphStorageProvider` direkt (wie `Shonkor.Benchmarks`).
- **CLI:** `shonkor eval --set eval/golden --k 10 [--judge ollama|heuristic|none]`.
- **Output:** JSON + Markdown-Report mit Aggregaten und **per-Fall-Deltas**; Exit-Code ≠ 0 nur bei Regression über Schwelle (für CI optional blockierend).

## Regressionen erkennen
- **Baseline einchecken:** `eval/baseline.json` mit aktuellen Werten pro Metrik+Modus.
- **Regel:** CI-Job (nicht-blockierend zuerst) rechnet Eval gegen die Branch-DB und vergleicht mit Baseline; Drop > x % (z. B. Precision@10 −3 pp) markiert das PR.
- **Bindung an Änderungen:** V2 (Code-Embedding), V3 (Budget/Ranking), V4 (Semantik-Default), V5 (Fusion) werden **jeweils gegen dieselbe Eval** gemessen — so wird jede Optimierung als Zahl belegt statt behauptet.

## Was zuerst
1. Golden-Set v0 (20 Fälle, nur C# + Retrieval-Ebene) — 0,5 Tag.
2. Runner für Ebene 1 + Baseline — 1 Tag.
3. Abstention-/Faithfulness-Fälle + Heuristik-Judge für Ebene 2 — 1 Tag.
4. CI-Hook (nicht-blockierend) — 0,5 Tag.

> **Hinweis zur Laufzeit-Präzision:** Aussagen über die *tatsächliche* Antwortqualität sind erst nach Schritt 1–3 belastbar. Bis dahin bleiben die Findings K2/K3/H1/H2 strukturell begründet (Code-Beleg), aber zahlenmäßig unquantifiziert — genau das schließt diese Eval.
