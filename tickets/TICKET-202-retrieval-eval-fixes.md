# TICKET-202 – Retrieval-Eval reparieren: Zirkularität, hybrid, Coverage-Symmetrie, CI-Gate

**Schweregrad-Bezug:** K1, M1 · **Aufwand:** M · **Risiko:** niedrig × mittel (Zahlen werden ehrlicher = schlechter; README muss nachziehen)

## Kontext
1. `bench/golden/doc-intent.json` ist für den Vektor-Retriever zirkulär: Query = `<summary>`-Text des Ziels (`GoldenSetGenerator.cs:38,50-53`), der via `EmbeddingTextBuilder` (`:40,52`) im Embedding-Dokument steht. P@1 0,88 ist eine Obergrenze für Doc-Comment-Self-Matching.
2. `search_hybrid` (Default-Modus) wird nie gebencht.
3. `RagBaselineBenchmark.cs:79-83` misst Shonkors Coverage am Prä-Budget-Subgraph, die Baseline an gelieferten Chunks — der +21-pp-Claim ist asymmetrisch.
4. Gate-Metrik P@k ist bei 1 Relevanten/k=10 (Max 0,1) mit 0,03-Toleranz wirkungslos (`Program.cs:157-172`); kein CI-Wiring; ohne Ollama werden Vektor-Zeilen still geskippt (`RetrievalBenchmark.cs:44-49`).
5. Der Prefix-A/B, der „within noise" ergab, embeddete Queries mit dem Document-Prefix (`RetrievalBenchmark.cs:68` → kind-loser Overload).

## Akzeptanzkriterien
- [ ] Neues Set `intent-paraphrased.json` (~150 Fälle): LLM-paraphrasierte Queries + automatischer Zirkularitäts-Check (kein Query teilt >4 Inhaltswörter mit dem Embedding-Dokument des Ziels); Stichproben-Review dokumentiert.
- [ ] Sets `agent-queries.json` (≥ 30 echte MCP-Queries, handgelabelt) und `negatives.json` (≥ 20 Fälle ohne Treffer im Graph) angelegt.
- [ ] `search_hybrid` als dritte Retriever-Zeile in `RetrievalBenchmark`.
- [ ] Coverage-Messung prüft den gelieferten Capsule-Text (Node-Header/Signatur-String-Check); neue Metrik Seed-Survival-Rate.
- [ ] Query-Embedding im Benchmark nutzt `EmbeddingKind.Query`; Prefix-A/B auf dem paraphrasierten Set neu gemessen und Ergebnis dokumentiert (TICKET-215/V15-Folgeentscheidung).
- [ ] Gate auf P@1/MRR/Recall@10 relativ (>5 %), P@k aus dem Gate entfernt.
- [ ] CI: PR-Job baut Fixture-DB (Repo indiziert sich selbst) und gated FTS-Zeilen; Nightly-Job (Ollama) gated Vektor/hybrid; fehlendes Ollama im Nightly = harter Fail, kein Silent-Skip.
- [ ] README-Benchmark-Tabellen auf die neuen Zahlen umgestellt, jeweils mit Commit/Datum des Runs; unbelegter „~88 %"-Claim entfernt oder durch gespeicherten Run belegt.

## Betroffene Bereiche
`src/Shonkor.Bench/**`, `bench/golden/`, `.github/workflows/`, README.

## Abhängigkeiten
Keine. Parallel zu TICKET-201.

## Definition of Done
Zwei identische Nightly-Läufe, Baselines eingecheckt, CI grün, README aktualisiert.
