# TICKET-205 – Gesamt-Prompt-Budget + `num_ctx` + Truncation-Detection im RAG-Pfad

**Schweregrad-Bezug:** K4 · **Aufwand:** S · **Risiko:** niedrig × niedrig

## Kontext
`BuildRagPrompt` (`OllamaSemanticAnalyzer.cs:158-196`) kappt nur pro Knoten (2.000 Zeichen), ohne Gesamtbudget; die UI sendet bis zu 10 Knoten + 6 Chat-Turns. `num_ctx` wird nie gesetzt (Ollama-Default oft 2048–4096 Tokens). Bei Überlauf trunkiert Ollama still und wirft zuerst den Instruktionsblock (Grounding, Abstention, Zitierpflicht, Injection-Fence) am Prompt-Anfang weg — das Modell generiert dann frei über rohem Code, genau im kontextreichen Fall.

## Akzeptanzkriterien
- [ ] `options.num_ctx` wird pro Modell aus Config gesetzt (Default z. B. 8192, dokumentiert).
- [ ] Prompt-Assembly schätzt Tokens (chars/3,5) und reduziert Knotenzahl bzw. Per-Node-Budget, bis Schätzung + Antwort-Reserve < `num_ctx`.
- [ ] Nach dem Call wird `prompt_eval_count` aus der Ollama-Antwort mit der Schätzung verglichen; erkannte Truncation wird geloggt und als Warnung in der Antwort-Metadata an die UI gegeben.
- [ ] Instruktionsblock (Regeln + Abstention + Zitierpflicht) steht am Prompt-**Ende**, nach dem Kontext.
- [ ] Antwort-Metadata enthält `nodesUsed` inkl. `truncated`-Flag pro Knoten (schließt M2 des Grounding-Reviews mit); UI zeigt „Kontext: N Knoten, M gekürzt".
- [ ] `/api/ask` cappt und dedupliziert `NodeIds` (z. B. ≤ 20).

## Betroffene Bereiche
`OllamaSemanticAnalyzer.cs` (blocking + streaming), `SearchEndpoints.cs`, `app.js` (Metadata-Anzeige), Config/Settings-Tab.

## Abhängigkeiten
Vor TICKET-206 (Zitat-Validierung setzt stabilen, nicht trunkierten Prompt voraus). Effektmessung via TICKET-201.

## Definition of Done
Test mit künstlich großem Kontext: Truncation wird erkannt und gemeldet statt still verschluckt; Groundedness-Metriken (TICKET-201) vorher/nachher dokumentiert.
