# TICKET-206 – Grounding-Durchsetzung: Zitat-Validierung, Relevanz-Schwelle, History-Fence, Antwortsprache

**Schweregrad-Bezug:** H2, H3, H14, M13 (+M1 Grounding-Review) · **Aufwand:** M · **Risiko:** mittel × niedrig (zu aggressive Schwelle lehnt berechtigte Fragen ab → konfigurierbar, per Eval kalibrieren)

## Kontext
Die gesamte Grounding-Story ist heute eine Prompt-Bitte: Zitate (`[Name @ file:lines]`) werden nie gegen das gelieferte Label-Set validiert (`OllamaSemanticAnalyzer.cs:163-168,184` — Modelltext geht wörtlich raus); es gibt keine Score-Schwelle — Kontext ist Top-10 der letzten Suche, Score verworfen (`app.js:255-257`), das LLM antwortet auch auf Rauschen; frühere Assistant-Antworten landen im vertrauenswürdigen `NUTZERFRAGE`-Slot (`app.js:312-316`) und umgehen den „Kontext ist Daten"-Fence; der Index-Zeit-Injection-Detector (`SuspiciousContentPostProcessor`) ist vom Antwortpfad entkoppelt; Antwortsprache ist hardcoded Deutsch (`:183,194`).

## Akzeptanzkriterien
- [ ] Post-Processing (Streamende bzw. gepuffert): `[… @ …]`-Label per Regex extrahiert, gegen das exakte Label-Set aus `BuildRagPrompt` geprüft; unbekannte Label sichtbar geflaggt („⚠ unbelegte Quelle"), valide als klickbare Node-Links gerendert; zitatlose Absätze markiert (UI-dezent).
- [ ] Konfigurierbare Relevanz-Schwelle (relativ zum Top-Score und absolut) bei der Kontextauswahl; unterschreiten alle Kandidaten sie, antwortet der Server deterministisch „dafür gibt es im Graphen keine Belege" **ohne** LLM-Call. Score/„match strength" pro Node in den Prompt.
- [ ] Chat-Transkript in eigener, als Daten deklarierter Prompt-Sektion; `NUTZERFRAGE` enthält nur die letzte Nutzernachricht.
- [ ] `/api/ask*` prüft Kontext-Node-Ids gegen `security.suspicious-instruction-in-content`-Diagnosen; geflaggte Quellen werden im Prompt annotiert und in der UI als Warnung ausgewiesen.
- [ ] Antwortsprache folgt UI-Locale bzw. Sprache der Frage (Config-Override möglich).
- [ ] Bekannte Fehler-Marker werden vor dem Push in `aiChatHistory` gestrippt.

## Betroffene Bereiche
`OllamaSemanticAnalyzer.cs`, `SearchEndpoints.cs`, `app.js`, Settings.

## Abhängigkeiten
Nach TICKET-205. Kalibrierung der Schwelle und Abnahme über TICKET-201-Metriken (Abstention-Precision/-Recall) und die Injection-Suite.

## Definition of Done
Injection-Suite ≥ 90 % bestanden; Citation-Validity ≥ 0,98; Abstention-Precision ≥ 0,9 auf dem answers-Set; Schwellen-Default nach Kalibrierung aktiviert.
