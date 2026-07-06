# Eval-Plan — Präzisions- & Grounding-Evaluation (Stand Review #2)

Ziel unverändert: „präzise/grounded" von Behauptungen zu Zahlen machen, die bei jeder Änderung neu berechnet werden. Offline, deterministisch, im shonkor-Stil.

## Stand
- **Ebene 1 (Retrieval) ist gebaut** — `Shonkor.Eval`: Precision@k / Recall@k / MRR für `graph` (FTS), `semantic` und `hybrid`; Golden-Set unter `eval/`; Baseline-Regressions-Gate (`--baseline`, Exit 2 bei Drop > 3 pp). Reproduzierbar: `dotnet run --project src/Shonkor.Eval -- shonkor.db [...]`.
- **Ebene 2 (Antwort/Grounding) fehlt noch** — genau die Lücke, die Finding **K2** kritisch macht. Das ist der nächste Ausbau (siehe unten).

## Ebene 1 — Retrieval (deterministisch, kein LLM) — vorhanden
- Metriken je Modus (k = 5, 10). Golden-Fälle: `{query, mode, expected node-ids}`; Auto-Bootstrap (Self-Retrieval) + kuratiertes Intent-Set (`eval/golden/intent.json`).
- **Offene Schärfung (V7):** Set von 15 auf 50–100 Fälle über mehrere Repos/Sprachen; Konfidenzintervalle ausweisen (aktuell zu klein für belastbare Punktschätzung).
- **Wichtig für K1:** Die Eval muss gegen einen Graphen **mit erzeugten Embeddings** laufen, sonst misst `semantic`/`hybrid` nichts. Das ist heute nur im web-angereicherten Zustand gegeben — der Eval-Lauf sollte das explizit prüfen und andernfalls „semantic skipped: no embeddings" melden (tut er).

## Ebene 2 — Antwort-Grounding (NEU aufzubauen, K2)
Drei Bausteine, aufsteigend nach Kosten:

1. **Zitat-Validität (deterministisch, kein LLM) — zuerst.** Jede `[Name @ file:zeilen]`-Referenz in der Antwort muss auf einen der tatsächlich übergebenen Kontextknoten zeigen. Metrik: Anteil valider Zitate + Anteil unbelegter Aussagen (Sätze ohne Zitat). Fängt „schön aussehendes Zitat auf nicht-geliefertem Knoten" billig ab.
2. **Abstention-Korrektheit.** Golden-Fälle mit `is_answerable=false` (Frage, deren Antwort nicht im Graph steht). Erwartet: „nicht belegt". Metrik: Abstention-Recall. Direkter Test der Anti-Halluzinations-Zusage.
3. **Faithfulness/Answer-Relevance (optionaler LLM-Judge).** Lokaler Ollama-Judge hinter Flag bewertet, ob die Aussagen durch die zitierten Knoten gedeckt sind und ob die Antwort die Frage trifft. Nur optional, weil nicht-deterministisch.

**Fall-Schema (erweitert):**
```json
{
  "id": "answer-capsule-purpose",
  "query": "Was macht ContextCapsuleSynthesizer?",
  "mode": "ask",
  "context_node_ids": ["…::ContextCapsuleSynthesizer"],
  "answer_must_cite": ["ContextCapsuleSynthesizer"],
  "is_answerable": true
}
```

## Regressionen erkennen
- `eval/baseline-metrics.json` (Retrieval) existiert; um Ebene-2-Kennzahlen erweitern (Zitat-Validität, Abstention-Recall).
- CI-Job (nicht-blockierend) rechnet beide Ebenen gegen die Branch-DB; Drop über Schwelle markiert das PR.
- **Jede** Retrieval-/Grounding-Änderung (V1, V3, V5, V6) wird gegen dieselbe Eval gemessen — sonst bleibt der Effekt Behauptung.

## Was zuerst
1. Zitat-Validitäts-Check + Abstention-Fälle (Ebene 2.1/2.2) — ~1,5 Tage, deterministisch. Schließt K2 zum größten Teil.
2. Golden-Set-Erweiterung (V7) — ~1 Tag.
3. Optionaler LLM-Judge (Ebene 2.3) — ~1 Tag, hinter Flag.
