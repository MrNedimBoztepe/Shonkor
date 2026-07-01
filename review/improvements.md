# Verbesserungen — shonkor

Pro Vorschlag: **Problem → Lösung → ggf. Alternative + Empfehlung → Nutzen → Risiko (Wahrscheinlichkeit × Auswirkung) → Aufwand (S/M/L)**.
Reihenfolge nach Präzisions-Hebel. IDs referenzieren Findings aus [shonkor-review.md](shonkor-review.md).

---

## V1 — Schlanke Präzisions-Eval einführen (K1)
- **Problem:** Keine Messung von Retrieval-/Antwortpräzision; jede Optimierung ist Blindflug.
- **Lösung:** Golden-Set (30–50 Query→Erwartung-Paare) + ein `Shonkor.Eval`-Runner, der Precision@k/Recall@k für Retrieval und Faithfulness/Answer-Relevance für die RAG-Antwort misst. Details in [eval-plan.md](eval-plan.md). In CI als nicht-blockierender Report, lokal als `shonkor eval`.
- **Alternative:** Externes Framework (RAGAS/promptfoo) statt Eigenbau. **Empfehlung:** Eigenbau-Retrieval-Eval (deterministisch, offline, kein Python-Stack) + optional RAGAS nur für die LLM-Faithfulness-Teilmetrik. Begründung: shonkors USP ist Offline/Deterministik — ein Python-LLM-Judge widerspräche dem für den Retrieval-Teil.
- **Nutzen:** Macht „präzise" falsifizierbar; alle weiteren Tickets werden verifizierbar; Regressionsschutz.
- **Risiko:** Niedrig × Mittel — Golden-Set kann anfangs zu klein/biased sein. Mitigierung: mit echten Agent-Queries auffüllen.
- **Aufwand:** **M**

## V2 — Embeddings auf Code/Signatur statt auf das LLM-Summary (K2)
- **Problem:** Vektor wird aus einem deutschen 1-Satz-Summary erzeugt → lossy, sprach-/domänen-fremd, garbage-in.
- **Lösung:** Embedding-Input = strukturiertes Code-Dokument: `Name + Signatur + Identifier-Pfad + (gekürzter) Body`, optional **plus** Summary als Zusatzfeld. Re-Embed der bestehenden Knoten. Query unverändert roher Text.
- **Alternative A:** Summary **und** Code getrennt einbetten, beide Scores fusionieren. **Alternative B:** Beim Embedding die `nomic`-Prefixe setzen (`search_document:`/`search_query:`, siehe V6). **Empfehlung:** Code-basiertes Embedding (A als späterer Ausbau). Begründung: bei Code-Suche dominieren Identifier/Signaturen die Relevanz; Summary allein verliert genau diese.
- **Nutzen:** Direkter Sprung in semantischer Trefferqualität; behebt EN-Query-/DE-Doc-Mismatch.
- **Risiko:** Mittel × Mittel — größere Embedding-Inputs = mehr Enrichment-Zeit; Code-Token können das Embedding-Kontextfenster sprengen (chunking nötig).
- **Aufwand:** **M**

## V3 — Kontext-Budget + Relevanz-Ranking im Capsule-Synthesizer (K3)
- **Problem:** Volle Quelltexte der ganzen 2-Hop-Nachbarschaft, ungewichtet → Token-Explosion + Rauschen.
- **Lösung:** (1) Hartes Token-Budget (z. B. 4k, konfigurierbar). (2) Knoten nach Distanz-zu-Seed + Retrieval-Score gewichten; Seeds zuerst, voll; entferntere Knoten nur als Signatur/Summary; Rest droppen. (3) Hub-Schutz: Knoten mit Grad > N nicht blind expandieren. (4) Im Capsule die Seeds klar als „primär" markieren.
- **Alternative:** Statt graderzentrierter Heuristik einen echten Reranker (Cross-Encoder) über die expandierten Knoten laufen lassen und Top-N ins Budget packen. **Empfehlung:** Erst Budget+Distanz-Gewichtung (billig, deterministisch, sofort), Reranker als V5.
- **Nutzen:** Erfüllt das Token-Versprechen real; weniger „lost in the middle"; präzisere Antworten.
- **Risiko:** Mittel × Hoch (positiv) — falsches Pruning könnte relevante Knoten kappen. Mitigierung: über V1-Eval messen.
- **Aufwand:** **M**

## V4 — Semantisches C#-Linking zum Default machen (oder Default-Kanten als „unsicher" flaggen) (H1)
- **Problem:** Namensbasierte `REFERENCES_TYPE`-Kanten erzeugen False Positives in Impact-/Rename-Tools.
- **Lösung:** `SHONKOR_SEMANTIC_CSHARP` standardmäßig **an**, wo eine Compilation verfügbar ist; sonst die namensaufgelösten Kanten mit `resolution=name-based`-Property markieren, und Tools wie `rename_plan`/`blast_radius` geben das als Konfidenz aus.
- **Alternative:** Default lassen, aber in jeder Tool-Antwort, die auf solchen Kanten beruht, eine Warnung mitsenden. **Empfehlung:** Semantik-Default an + Konfidenzflag. Begründung: das „100 % präzise"-Versprechen ist sonst im Default schlicht falsch.
- **Nutzen:** Macht das Kern-USP (präzise Impact-Analyse) im Default wahr.
- **Risiko:** Mittel × Mittel — Semantikmodus ist langsamer/speicherhungriger beim Indexieren; bei nicht-kompilierbaren Repos Fallback nötig.
- **Aufwand:** **M**

## V5 — Reranking-Stufe vor der Kontext-Assemblierung (H3, K3)
- **Problem:** Kandidaten gehen ungerankt in die Graph-Expansion; keine Fusion von FTS + Vektor.
- **Lösung:** Hybrid-Kandidaten (FTS-BM25 ∪ Vektor) via **Reciprocal Rank Fusion** mergen, dann optional Cross-Encoder-Rerank (z. B. via Ollama-Rerank-Modell) auf die Top-N. Erst die gerankten Top-N als Seeds in die Expansion.
- **Alternative:** Nur RRF ohne Cross-Encoder (billiger, deterministischer). **Empfehlung:** Mit RRF starten (deterministisch, offline-konform), Cross-Encoder optional hinter Flag. Begründung: Determinismus ist ein erklärtes shonkor-Ziel.
- **Nutzen:** Höhere Seed-Präzision → bessere Capsule → bessere Antwort. Schließt die Hybrid-Lücke gegen das README.
- **Risiko:** Niedrig × Mittel — RRF-Gewichte müssen über V1 kalibriert werden.
- **Aufwand:** **M**

## V6 — RAG-Antwort: Zitate, Determinismus, saubere Truncation (H2)
- **Problem:** Keine Quellenangaben, `temperature` unbestimmt, harte Zeichen-Truncation.
- **Lösung:** (1) Prompt verlangt pro Aussage einen Verweis `[Name @ file:start-end]`; Knoten im Kontext mit genau diesem Label rendern. (2) `options.temperature=0` (+ `seed`) im Request. (3) Truncation an Zeilen-/Symbolgrenzen statt Zeichen; falls gekürzt, explizit markieren. (4) Optionaler Faithfulness-Self-Check als zweiter, billiger Pass.
- **Alternative:** Strukturierte Ausgabe (JSON mit `claims[]` + `evidence_node_ids[]`) statt Freitext-Zitate. **Empfehlung:** Inline-Zitate jetzt, strukturierte Claims später (an V1-Faithfulness koppeln).
- **Nutzen:** Rückführbarkeit jeder Aussage; reproduzierbare Antworten; weniger Halluzination.
- **Risiko:** Niedrig × Mittel — kleines lokales Modell zitiert evtl. inkonsistent; über Eval messen.
- **Aufwand:** **S–M**

## V7 — Embedding-Versionierung + Re-Embed-Trigger; nomic-Prefixe (M1)
- **Problem:** Modellwechsel macht Alt-Index still teilunsichtbar; keine Prefixe.
- **Lösung:** Pro Embedding `model`+`dim` persistieren; bei Mismatch nicht still skippen, sondern als „re-embed pending" markieren und Worker neu einbetten. `search_document:`/`search_query:`-Prefixe setzen.
- **Alternative:** Index bei Modellwechsel komplett neu bauen (einfacher, teurer). **Empfehlung:** Versionsfeld + selektives Re-Embed.
- **Nutzen:** Kein stiller Recall-Verlust; bessere Retrieval-Qualität durch Prefixe.
- **Risiko:** Niedrig × Mittel.
- **Aufwand:** **S**

## V8 — Benchmark ehrlich machen (H4)
- **Problem:** Strohmann-Vergleich (ganze Datei vs. Summary), misst Volumen statt Korrektheit.
- **Lösung:** Vergleich auf den **real ausgelieferten Capsule-Pfad** umstellen (chunked Baseline-RAG vs. shonkor-Capsule) und neben Token **Antwort-Korrektheit** aus V1 ausweisen. README-Zahlen entsprechend relativieren/neu erheben.
- **Alternative:** Benchmark-Claim aus dem README entfernen, bis V1 belastbare Zahlen liefert. **Empfehlung:** Claim zurücknehmen, bis gemessen; dann mit Korrektheit + Token gemeinsam ausweisen.
- **Nutzen:** Glaubwürdigkeit; verhindert Erwartungs-/Reputationsschaden.
- **Risiko:** Niedrig × Niedrig.
- **Aufwand:** **S**

## V9 — SaaS-`/api/rag/query` mit Grounding-Gerüst (M3)
- **Problem:** Liefert Kontext ohne Anleitung/Zitierpflicht an externe LLMs.
- **Lösung:** Antwort um ein kompaktes System-Preamble ergänzen („antworte ausschließlich aus diesem Kontext, zitiere `[file:line]`, sonst ‚nicht im Graph belegt'") und Knoten mit Zitat-Labels rendern (teilt Rendering mit V6).
- **Nutzen:** Präzisionsvorteil reicht bis zum externen Agenten.
- **Risiko:** Niedrig × Niedrig.
- **Aufwand:** **S**

---

### Aufwand-/Hebel-Übersicht
| ID | Finding | Hebel auf Präzision | Aufwand |
|----|---------|---------------------|---------|
| V1 | K1 | Ermöglicht alles andere | M |
| V2 | K2 | Hoch | M |
| V3 | K3 | Hoch | M |
| V4 | H1 | Hoch (Graph-Kern) | M |
| V5 | H3 | Mittel-Hoch | M |
| V6 | H2 | Mittel-Hoch | S–M |
| V7 | M1 | Mittel | S |
| V8 | H4 | Glaubwürdigkeit | S |
| V9 | M3 | Mittel | S |
