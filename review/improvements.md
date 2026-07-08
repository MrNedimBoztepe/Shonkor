# Shonkor – Verbesserungsvorschläge

Format je Vorschlag: **Problem → Lösung → ggf. Alternative → Nutzen → Risiko (Wahrscheinlichkeit × Auswirkung) → Aufwand (S/M/L)**. Nummerierung V1…V15, sortiert nach Hebel auf Antwort-Präzision. Referenzen: Findings in [shonkor-review.md](shonkor-review.md), Tickets in `tickets/`.

---

## V1 — Antwort-Groundedness-Eval wiederherstellen (K1) → TICKET-201

**Problem:** Nichts misst, ob Antworten dem Kontext treu sind; die frühere Eval (Zitat-Validität, Abstention) wurde in `009b8d7` gestrichen.
**Lösung:** `--answers`-Modus in `Shonkor.Bench` reaktivieren: Golden-Set aus (Frage, Kontext-Node-Ids, erwartetes Verhalten: answerable/abstain, must-cite-Ids); Metriken: Citation-Validity-Rate (Labels ⊆ geliefertes Set), Must-Cite-Recall, Abstention-Precision/-Recall. Details: [eval-plan.md](eval-plan.md).
**Alternative:** Externes Framework (RAGAS/DeepEval via Python-Sidecar) — mehr Metriken (Faithfulness per LLM-Judge) out of the box, aber Fremd-Stack, Ollama-Anbindung fummelig, bricht die „ein Harness, ein Report"-Linie von `Shonkor.Bench`. **Empfehlung:** eigenes schlankes `--answers` zuerst (die Infrastruktur existierte schon); LLM-as-Judge-Faithfulness später optional obendrauf.
**Nutzen:** Macht „präzise" von Behauptung zu Messgröße; Voraussetzung für alle Grounding-Änderungen (V4–V6) — ohne Eval sind deren Effekte unbelegbar.
**Risiko:** niedrig × niedrig (reine Messinfrastruktur; einziges Risiko: schlecht kuratierte Fälle messen das Falsche — durch Review der Fälle abfangbar).
**Aufwand:** M

## V2 — Retrieval-Benchmark entzirkularisieren + `search_hybrid` benchen + Coverage symmetrisch messen (K1) → TICKET-202

**Problem:** doc-intent-Queries sind Fast-Substrings der Embedding-Dokumente; hybrid (der Default!) ungemessen; +21 pp aus asymmetrischer Coverage; Gate auf sinnlosem P@k.
**Lösung:** (a) Queries per LLM paraphrasieren (lexikalische Überlappung brechen), zusätzlich ~30 handgesammelte echte Agent-Queries aus MCP-Logs, inkl. Negativ-Fällen („gibt es hier Payment-Code?" → erwartete Antwort: nein); (b) `search_hybrid` als dritte Retriever-Zeile; (c) `RagBaselineBenchmark`: Coverage gegen den **Capsule-Text** prüfen (Node-Header sind zitierbar — String-Check), nicht gegen den Prä-Budget-Subgraph; (d) Gate auf P@1/MRR/Recall@10 relativ statt P@k absolut.
**Nutzen:** Erst danach sind die README-Zahlen belastbar; hybrid-Regressionen (z. B. durch H1-Fix) werden sichtbar.
**Risiko:** niedrig × mittel (paraphrasierte Zahlen werden sichtbar schlechter ausfallen als 0,88 — das ist Ehrlichkeit, kein Schaden; README muss nachziehen).
**Aufwand:** M

## V3 — Upsert auf `ON CONFLICT DO UPDATE` umstellen + FTS-Query-Sanitizing (K3, H1) → TICKET-203

**Problem:** REPLACE korrumpiert den FTS-Index still; normale Code-Queries werfen FTS-Syntaxfehler und degradieren zu unsortiertem LIKE.
**Lösung:** (a) `INSERT … ON CONFLICT(Id) DO UPDATE` (feuert UPDATE-Trigger, erhält rowid; zusätzlich `NeedsSemanticAnalysis`/`Embedding` nur bei Content-Änderung zurücksetzen); (b) Query-Sanitizer: Whitespace-Tokens in doppelte Quotes (mit `""`-Escaping), optional Prefix-`*`; LIKE nur noch als echter letzter Fallback mit `ESCAPE` und definierter Ordnung; (c) Fallback-Fall an `search_hybrid` signalisieren, damit RRF die Scheinränge abwertet.
**Alternative zu (a):** nur `PRAGMA recursive_triggers=ON` — eine Zeile, behebt die Korruption, lässt aber rowid-Churn und das Embedding-Wipe-Verhalten bestehen. **Empfehlung:** ON CONFLICT (strikt besser); das Pragma zusätzlich als Gürtel-und-Hosenträger schadet nicht.
**Nutzen:** Beseitigt die größte stille Korrektheits-Zeitbombe; BM25-Ranking gilt wieder für die häufigsten Query-Formen.
**Risiko:** niedrig × mittel (Upsert-Semantik-Änderung ist zentral — durch vorhandene Storage-Tests + einen neuen FTS-Konsistenztest abdecken).
**Aufwand:** S

## V4 — Prompt-Token-Budget + `num_ctx` + Instruktionsposition (K4) → TICKET-205

**Problem:** Stille Ollama-Truncation wirft zuerst die Grounding-Regeln weg.
**Lösung:** `options.num_ctx` pro Modell konfigurieren; Prompt-Tokens schätzen (chars/3,5 reicht) und Knotenzahl/Per-Node-Budget passend schrumpfen; nach dem Call `prompt_eval_count` gegen Schätzung prüfen und Truncation als Warnung an UI/Log melden; Instruktionsblock ans Prompt-**Ende** verschieben (Tail-Retention).
**Nutzen:** Grounding-Regeln überleben garantiert; stille Degradation wird sichtbar.
**Risiko:** niedrig × niedrig.
**Aufwand:** S

## V5 — Zitat-Validierung + Relevanz-Schwelle + History-Fence (H2, H3, H14, M13) → TICKET-206

**Problem:** Zitate sind unvalidierter Modelltext; schwaches Retrieval führt trotzdem zur Antwort; Chat-History umgeht den Injection-Fence; Antwortsprache hardcoded.
**Lösung:** (a) Antwort puffern bzw. am Streamende nachverarbeiten: `[… @ …]`-Label per Regex extrahieren, gegen das exakte Label-Set aus `BuildRagPrompt` prüfen, unbekannte sichtbar flaggen, valide als Node-Links rendern, zitatlose Absätze markieren; (b) Score-Schwelle vor Kontextauswahl (relativ oder absolut, konfigurierbar), darunter deterministisches „dafür gibt es im Graphen keine Belege" **ohne** LLM-Call; Retrieval-Stärke pro Node in den Prompt; (c) Transkript in eigene, als Daten deklarierte Prompt-Sektion, `NUTZERFRAGE` = nur letzte Nachricht; (d) Antwortsprache aus UI-Locale bzw. Frage.
**Alternative zu (a):** Claim-Level-Entailment (NLI/LLM-Judge pro Satz) — deutlich stärkere Garantie, aber teuer, langsam, auf lokalen Modellen unzuverlässig. **Empfehlung:** Label-Set-Validierung jetzt (fängt die schlimmste Klasse: zitat-gewaschene Halluzination); Entailment später als Eval-Metrik (V1), nicht als Laufzeit-Gate.
**Nutzen:** Die drei größten Enforcement-Lücken des Antwortpfads geschlossen; H14 schließt einen realen Injection-Kanal.
**Risiko:** mittel × niedrig (Schwelle zu aggressiv → berechtigte Fragen abgelehnt; konfigurierbar machen + per V1-Eval kalibrieren).
**Aufwand:** M

## V6 — Provenance-Integrität durchsetzen (K5, M5) → TICKET-207

**Problem:** Linker-Fallback und LLM-Kanten claimen Extracted; Regex-Parser ohne Override; `INSERT OR IGNORE` friert Provenance ein; `Properties` werden nie persistiert.
**Lösung:** Provenance ins Edge-Tupel des Linkers (Fallback: `Inferred` bei eindeutig, `Ambiguous` bei mehreren — wie `CrossTechLinker` es vormacht); `RELATES_TO`-Insert explizit `Inferred`; `DefaultProvenance`-Override in `GraphQLParser`/`SitecoreXmCloudPlugin`; Edge-Upsert `ON CONFLICT … DO UPDATE SET Provenance = MIN(excluded.Provenance, Provenance)` (exakte Auflösung darf Trust upgraden); Storage-Guard-Test: kein Schreibpfad persistiert Extracted außer deterministischen Parsern/Linkern. `GraphEdge.Properties` entweder als JSON-Spalte persistieren oder aus dem Modell entfernen (die Lüge beenden) — Empfehlung: persistieren, die Plugin-Metadaten (confidence, placeholder) sind für Ranking nützlich.
**Nutzen:** Das Vertrauensmodell — Shonkors erklärtes Alleinstellungsmerkmal — stimmt wieder mit der Realität überein; `provenance=extracted`-Filter liefern echte Compiler-Fakten.
**Risiko:** niedrig × niedrig (bestehende Graphen behalten falsche Stempel bis zum Re-Scan — einmaliger `--reindex` nach Upgrade dokumentieren).
**Aufwand:** S

## V7 — Volle Methoden-Bodies + `signature`-Property (K2, M9) → TICKET-204

**Problem:** 500-Zeichen-Kappung amputiert das Kernkorpus; `signature` wird gelesen, aber nie geschrieben; Klassen ohne Content.
**Lösung:** Volle Bodies auf Method/Constructor speichern (Bounding gehört in `EmbeddingTextBuilder`, nicht in den Parser); `EndLine` setzen; `get_source` auf Datei-Slice fallen lassen, wenn Content mit Marker endet; Parser emittieren `signature` (Modifier + Name + Parameterliste); Klassen-Knoten bekommen Member-Signatur-Skelett als Content.
**Alternative:** Cap nur anheben (z. B. 10k) — kleinerer DB-Footprint, aber dasselbe Problem eine Größenordnung später und `get_source` bleibt lückenhaft. **Empfehlung:** voll speichern; SQLite kommt mit Quellcode-Volumina problemlos klar (das File-Node speichert heute schon bis 100k).
**Nutzen:** FTS/Embeddings/get_source sehen erstmals ganze Methoden; das Head+Tail-Design von TICKET-105 wird für C# überhaupt erst wirksam.
**Risiko:** niedrig × niedrig (DB wächst; Re-Index nötig).
**Aufwand:** S

## V8 — Zeilennummern normalisieren (H7) → TICKET-208

**Problem:** C# 0-basiert, Plugins 1-basiert, Doku sagt 1-basiert — Zitate off-by-one, Slices falsch.
**Lösung:** Parser-seitig `+1` (Roslyn), `TryReadSourceSlice` und `CSharpDiagnostics` konsistent umstellen, Konventionstest über alle Parser (jeder Parser-Testfall prüft: Zeile 1 = erste Zeile).
**Nutzen:** Jede Zitatangabe des Systems stimmt — Grundvoraussetzung für „grounded mit Quellenverweis".
**Risiko:** mittel × niedrig (Off-by-one-Fixes erzeugen gern neue Off-by-ones — der Konventionstest ist Pflichtteil).
**Aufwand:** S

## V9 — Markdown-Sektions-Chunking + Summary in FTS (H6, M8) → TICKET-211

**Problem:** Doku ist nur file-granular retrievbar; Sektionen ohne Content/Zeilen; `Summary` nicht in FTS; Concepts nie embedded.
**Lösung:** Content zwischen Header-Matches in die Sektion (mit Start/EndLine aus Match-Offsets), Code-Fences/Tabellen intakt lassen, Übergroße an Absatzgrenzen splitten; `Summary` als FTS-Spalte (Rebuild + Trigger); Concept-Namen embedden.
**Nutzen:** Doku-Fragen („wie konfiguriere ich X?") treffen die richtige Sektion mit zitierbarem Zeilenbereich statt Datei-Head.
**Risiko:** niedrig × niedrig (FTS-Rebuild einmalig).
**Aufwand:** M

## V10 — Embedding-Lifecycle im Edit-Loop reparieren (H11, M1–M3) → TICKET-212

**Problem:** Datei-Edit wirft alle Node-Embeddings der Datei weg; stdio-MCP re-embedded nie; CLI re-embedded alles; Enrichment-Race und -Verklemmung; fehlende Transaktions-Atomarität; Plugin-File-Node-Konflikte.
**Lösung:** per-Node-Content-Hash: Summary/Embedding unveränderte Geschwister überleben den Re-Parse; CLI-Embed-Pass filtert `Embedding IS NULL` (bzw. Hash-Änderung); nach `reindex_file` synchron die frischen Knoten embedden (der MCP-Host hat den Embedding-Service bereits); `UpdateNodeSemanticDataAsync` mit Content-Fingerprint-Guard; `AnalysisAttempts`-Spalte + Parken nach N Fehlversuchen; `ReplaceFileGraphAsync(nodes, edges)` in **einer** Transaktion (Hash zuletzt); Parser dürfen keine `Type="File"`-Knoten emittieren (Scanner filtert, Docker/Python-Plugins fixen).
**Nutzen:** Der agentische Edit-Loop — Shonkors Hauptszenario — hält den semantischen Index aktuell statt ihn zu zerlegen; CLI-Embed-Kosten sinken um Größenordnungen.
**Risiko:** mittel × mittel (Carry-over-Logik ist die invasivste Änderung dieser Liste; braucht gezielte Tests: Edit einer Methode → nur deren Embedding invalidiert).
**Aufwand:** L

## V11 — Kanten-Kanonisierung: implementations_of, Phantom-Hubs, JS-Imports, Id-Schema (H4, H5, M4, M15) → TICKET-213

**Problem:** Zwei IMPLEMENTS-Repräsentationen, Relink zerstört die vom Tool genutzte; Phantom-Name-Hubs verschmutzen Traversal; JS-Import-Kanten verbinden nie; Id-Kollisionen/-Churn.
**Lösung:** Id-Target als kanonische Form, `implementations_of` löst erst den Interface-Knoten auf; Parser-Basistyp-Kanten auf Simple-Name reduzieren und `Inferred` taggen (in Semantic-Mode ganz unterdrücken); CTE expandiert nicht über nicht-existente Endpunkte (JOIN im rekursiven Schritt) und bekommt optionalen Relationship-/Provenance-Filter + Fan-out-Cap; JS-Import-Resolution mit Extension-/Index-Probing, Package-Imports namespacen (`npm:react`); Typ-Ids mit Generics-Arity, Overload-Ordinal statt Span, Partial-Kanonisierung; XmCloud-Komponenten file-keyed.
**Alternative (Teilaspekt Hubs):** nur Blocklist bekannter Hub-Relationen im Traversal — billiger, aber kuriert Symptome; Phantom-Endpunkte blieben. **Empfehlung:** Kanonisierung; die Blocklist (M6) zusätzlich als konfigurierbarer Filter.
**Nutzen:** Multi-Hop-Präzision — das Argument für Graph statt Vektor — wird real: `find_path`/`get_subgraph` liefern strukturelle statt zufälliger Nachbarschaften; JS/TS-Seite des Cross-Tech-Versprechens funktioniert erstmals.
**Risiko:** mittel × mittel (Id-Schema-Änderung = `SchemeVersion`-Bump + Voll-Reparse; genau dafür existiert der Mechanismus).
**Aufwand:** L

## V12 — MCP-Härtung: Session, Containment, Protokoll, Clamps (H8, H9, H13, M7, M11, M12) → TICKET-209/210

**Problem:** `set_project` false-success über HTTP; Path Traversal; Tool-Fehler als Protokollfehler; ping fehlt; unbounded Outputs; AllowLocalBypass; record-Injection-Kanal.
**Lösung:** (a) Session-Store per `Mcp-Session-Id` **oder** ehrlicher Fehler „über Relay nicht unterstützt — nutze X-Project-Name"; (b) gemeinsames `ResolveContainedPath(raw, basePath)` mit `GetFullPath`+StartsWith-Check in allen fünf Datei-Tools; (c) `isError:true`-Results, `ping`, `-32700`, Protokollversion validieren; (d) Clamps: limit≤100, hops≤5, maxHops≤10, Default-Output-Cap 20–40 KB für get_source/get_subgraph-verbose/generate_capsule; (e) Bypass nur mit Flag **und** `IsDevelopment()`; (f) record: Längen-Cap, `connectedNodeIds` gegen Existenz prüfen, Content als Daten fencen.
**Alternative zu (a):** Umstieg auf das offizielle MCP-C#-SDK mit Streamable-HTTP — löst Session + Protokoll-Conformance strukturell, aber Migrationsaufwand L und der handgerollte Server ist ansonsten solide. **Empfehlung:** jetzt (a)–(f) punktuell; SDK-Umstieg als bewusste spätere Entscheidung, falls Remote-MCP strategisch wird.
**Nutzen:** Beseitigt Confident-Lie-Semantik, den größten lokalen Sicherheitsvektor und die Client-Kompatibilitätsprobleme in einem Zug.
**Risiko:** niedrig × mittel (Containment kann legitime Out-of-Root-Workflows brechen — Fehlermeldung nennt den Root; Opt-out-Config falls nötig).
**Aufwand:** M

## V13 — Resilienz-/Performance-Feinschliff (M10, M14, Niedrig-Sammel) → im Zuge von TICKET-210/215

**Problem:** Retry auf Cancellation/4xx, 3×-Full-Generation-Retry, Webhook-CT-Capture, GetAllNodes auf Request-Pfaden, architecture-N+1.
**Lösung:** `when (ex is not OperationCanceledException …)`-Filter; nur transiente Fehler retryen (oder `Microsoft.Extensions.Http.Resilience` auf die typed Clients); Blocking-RAG max. 1 Retry nur bei Connect-Fehlern + kleiner Answer-Cache `hash(model+prompt)`; Webhook-Task mit `ApplicationStopping`-Token bzw. Queue-BackgroundService; Insights/Stats auf Projektionsqueries; `architecture` mit Batch-Edge-Query.
**Nutzen:** Latenz/Kosten runter, Push-Indexierung zuverlässig, Dashboard skaliert.
**Risiko:** niedrig × niedrig. **Aufwand:** M (verteilt, unabhängig parallelisierbar)

## V14 — Vektor-Skalierung (H12, M6-SQL) → TICKET-215

**Problem:** Brute-Force-Blob-Scan pro Query; CTE-OR-Join ohne Indexnutzung.
**Lösung (inkrementell):** (1) L2-Normalisierung beim Schreiben + Dot-Product; `MemoryMarshal.Cast` statt per-Row-Alloc; Overscan-Faktor streichen (Heap ist exakt); (2) In-Memory-Matrix-Cache mit Generation-Counter (~300 MB bei 100k×768 float32, fp16 halbiert); (3) CTE als zwei UNION-Branches (nutzt `idx_edges_source`/`_target`).
**Alternative:** `sqlite-vec` (bleibt in der SQLite-Linie, ANN optional) oder externer Vektorstore (Qdrant) — letzterer bricht „100% offline & self-contained" und lohnt erst weit jenseits 1M Knoten. **Empfehlung:** Stufe 1+3 sofort (klein), Stufe 2 bei >20k Knoten, `sqlite-vec` als Beobachtungsposten — der Status quo (exakte Suche, SQLite-only) ist bei der aktuellen Zielgröße richtig.
**Nutzen:** Query-Latenz bleibt bei wachsenden Graphen zweistellig ms; Speicher-Churn weg.
**Risiko:** niedrig × niedrig (Stufe 1/3), mittel × niedrig (Cache-Invalidierung, Stufe 2). **Aufwand:** S (1+3) / M (2)

## V15 — nomic-Prefixe korrekt A/B-testen (M1) → Teil von TICKET-202

**Problem:** Prefixe default aus, begründet mit einer Messung, die Queries mit dem Document-Prefix embeddete; Prefix-Änderung triggert kein Re-Embed.
**Lösung:** Benchmark auf `EmbeddingKind.Query` fixen, auf dem paraphrasierten NL-Set (V2) neu messen; effektives Prefix-Paar in den `EmbeddingModel`-Stempel aufnehmen (`nomic-embed-text|dp=…|qp=…`), damit die vorhandene Reconcile-Logik Prefix-Wechsel erkennt.
**Nutzen:** nomic ist explizit mit Task-Prefixen trainiert — plausibel mehrere Punkte NL-Retrieval-Präzision, quasi gratis.
**Risiko:** niedrig × niedrig (falls Messung „aus" bestätigt: Status quo behalten, aber diesmal belegt). **Aufwand:** S

---

### Bewusst NICHT empfohlen

- **Wechsel auf reine Hybrid-Vektor+BM25-Suche ohne Graph:** Die Bench-Daten (trotz K1-Schwächen) und die Architektur sprechen dafür, dass der Graph *nicht* dekorativ ist — Capsule-Struktur, Blast-Radius, `implementations_of`, Freshness sind ohne Graph nicht reproduzierbar. Der richtige Schritt ist V11 (Graph-Präzision reparieren), nicht der Rückbau.
- **Cross-Encoder-Reranking jetzt:** Bei Top-K≤10 aus FTS+Vektor+RRF auf Code-Symbolen ist der erwartete Gewinn klein gegenüber V2/V5/V7, und ein lokales Reranker-Modell addiert Latenz + Betriebskomplexität. Erst nach sauberer Eval (V1/V2) entscheiden — dann misst man den Effekt statt ihn zu vermuten.
- **Externer Graph-Store (Neo4j etc.):** SQLite + rekursive CTE ist korrekt implementiert, deterministisch, offline und für die Zielgröße performant (nach V14). Migration = hohes Risiko, kein gemessener Nutzen.
