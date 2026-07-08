# TICKET-201 – Antwort-Groundedness-Eval wiederherstellen (`--answers`)

**Schweregrad-Bezug:** K1 · **Aufwand:** M · **Risiko:** niedrig × niedrig

## Kontext
Eine Groundedness-Eval (Zitat-Validität, Must-Cite, Abstention) existierte in `Shonkor.Eval` (eingeführt `ccd6c22`) und wurde beim Zusammenlegen in `Shonkor.Bench` (`009b8d7`) ersatzlos gestrichen — die Commit-Message benennt das selbst. Seitdem misst nichts, ob RAG-Antworten dem gelieferten Kontext treu sind. Die verwaisten `src/Shonkor.Eval/bin|obj`-Verzeichnisse suggerieren fälschlich ein existierendes Projekt.

## Akzeptanzkriterien
- [ ] `shonkor-bench --answers bench/golden/answers.json` läuft gegen die produktive `BuildRagPrompt`-Pipeline (gleicher Prompt wie `/api/ask`), mit `temperature=0` und gesetztem `seed`.
- [ ] `answers.json` enthält ≥ 40 Fälle (Startpunkt: altes 7-Fälle-Set aus der Git-Historie von `ccd6c22`), davon ≥ 10 `abstain`-Fälle; Schema: `question`, `contextNodeIds`, `kind`, `mustCite`, optional `mustContain`/`mustNotContain`.
- [ ] Metriken in `metrics.json` + `report.md`: Citation-Validity-Rate, Must-Cite-Recall, Abstention-Recall, Abstention-Precision, Uncited-Paragraph-Rate.
- [ ] `--baseline` gated die vier erstgenannten Metriken (relativer Einbruch > 5 % → Exit 2).
- [ ] `src/Shonkor.Eval/` (bin/obj-Reste) ist gelöscht.

## Betroffene Bereiche
`src/Shonkor.Bench/` (neuer AnswersBenchmark), `bench/golden/`, `OllamaSemanticAnalyzer` (Prompt-Wiederverwendung ggf. als testbare Methode exponieren), Doku.

## Abhängigkeiten
Keine harten. Sinnvoll vor TICKET-205/206, damit deren Effekt messbar ist. Ollama lokal erforderlich.

## Definition of Done
Eval läuft reproduzierbar lokal (zwei Läufe, identische Zahlen), Baseline eingecheckt, Ergebnisse im Report dokumentiert, README-Abschnitt „Benchmark" um die Groundedness-Zeilen ergänzt.
