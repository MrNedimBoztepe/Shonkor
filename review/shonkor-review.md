# Shonkor – Kritisches Review (Präzision, Grounding, Stabilität)

**Stand:** 2026-07-08, HEAD `e2031dd` · **Methode:** Code-Review über sechs Subsysteme (Ingestion, Graph-Aufbau, Embedding/Retrieval, Antwort-Grounding, MCP-Server, .NET/Eval); alle Findings im Quellcode verifiziert, Belege als `Datei:Zeile`.

---

## Executive Summary

Shonkor ist substanziell besser gebaut als der typische RAG-Prototyp: sauberer Async-/SQLite-Layer, durchdachtes Drift-/Freshness-Modell, Embedding-Versionierung, token-effiziente MCP-Outputs und eine ungewöhnlich ehrliche Benchmark-Kultur. **Das Kernversprechen „präzise und grounded" ist aber derzeit an drei Stellen nicht eingelöst — und an keiner davon durch Messung gedeckt:**

1. **„Präzise" ist unbewiesen.** Es gibt **keine Antwort-Groundedness-Eval**: Eine existierte (Zitat-Validität, Abstention-Recall in `Shonkor.Eval`) und wurde in Commit `009b8d7` ersatzlos gestrichen. Das Retrieval-Golden-Set ist für den Vektor-Retriever **zirkulär** (Query = Doc-Kommentar des Zielsymbols, der wörtlich im Embedding-Dokument steckt — P@1 0,88 ist eine Obergrenze, keine Messung), der +21-pp-RAG-Vergleich misst Coverage **asymmetrisch** (Shonkors Coverage am Prä-Budget-Subgraph, Baseline an gelieferten Chunks), und der Default-Modus `search_hybrid` wird **nie** gebencht.
2. **Das Korpus ist an der Wurzel amputiert.** Methoden-/Konstruktor-Bodies werden beim Parsen still auf **500 Zeichen** gekappt (`RoslynAstParser.cs:462-470`) — FTS, Embeddings und sogar `get_source` (bevorzugt den gespeicherten Body, `ReadTools.cs:91-94`) arbeiten auf der ersten Methodenhälfte. Markdown-Sektionen haben **gar keinen** Content/Zeilenbereich — Doku ist faktisch nicht gechunkt.
3. **Grounding ist ein Prompt-Absatz ohne Durchsetzung.** Kein Gesamt-Token-Budget und kein `num_ctx` → Ollama trunkiert still, und **zuerst fallen die Grounding-/Zitier-/Abstentions-Regeln am Prompt-Anfang weg** — genau im kontextreichen Fall. Zitate werden nie gegen die Quell-Labels validiert; es gibt keine Relevanz-Schwelle, schwaches Retrieval führt trotzdem zur LLM-Antwort.

Dazu kommen zwei stille Korrektheits-Zeitbomben: `INSERT OR REPLACE` korrumpiert den external-content-**FTS5-Index** (Delete-Trigger feuern ohne `recursive_triggers` nicht), wonach *jede* Suche unsichtbar in einen unsortierten `LIKE`-Scan degradiert — und die **Provenance-Integrität** (das erklärte Kern-Differenzierungsmerkmal) wird von zwei Schreibpfaden verletzt, die heuristische bzw. LLM-Kanten als `Extracted` speichern.

**Gute Nachricht:** Die fünf kritischen Punkte sind überwiegend kleine, lokale Fixes (ein `ON CONFLICT`-Upsert, ein `num_ctx` + Budget, eine Truncation-Konstante, zwei Provenance-Stempel); nur die Eval ist echte Aufbauarbeit — und dafür existiert die Harness-Infrastruktur (`Shonkor.Bench`, Baseline-Gate) bereits. Priorisierte Umsetzung: [roadmap.md](roadmap.md), Maßnahmen: [improvements.md](improvements.md), Eval-Aufbau: [eval-plan.md](eval-plan.md), Tickets: `tickets/TICKET-201…215`.

---

## Findings

### KRITISCH

**K1 — Es gibt keine Messung von Antwort-Präzision; die vorhandenen Retrieval-Zahlen sind methodisch schief.**
Befund: `src/Shonkor.Eval/` enthält nur `bin`/`obj` ohne Git-Historie; die Groundedness-Eval (Zitat-Validität, Must-Cite-Rate, Abstention-Recall, eingeführt in `ccd6c22`) wurde in `009b8d7` („unify Benchmarks + Eval") ersatzlos entfernt — Commit-Message sagt es selbst. `bench/golden/doc-intent.json` wird aus `<summary>`-Kommentaren generiert (`GoldenSetGenerator.cs:38,50-53`), die via `EmbeddingTextBuilder.Build` (`EmbeddingTextBuilder.cs:40,52`) auch im Embedding-Dokument stehen → Vektor-Query ist Fast-Substring des Zieldokuments. `RagBaselineBenchmark.cs:79-83` prüft Shonkors Coverage gegen den **Prä-Budget**-Subgraph, die Baseline gegen gelieferte Chunks. `search_hybrid` (Default im Dashboard „Brain"-Modus und MCP) taucht in keinem Benchmark auf. Gate-Metrik ist ausgerechnet P@k mit k=10 bei genau 1 Relevanten (Maximum 0,1; Toleranz 0,03 = 31 % relativer Einbruch nötig, `Shonkor.Bench/Program.cs:157-172`); kein CI-Wiring (`.github/workflows/ci.yml` baut/testet nur).
Auswirkung: Alle Präzisions-Aussagen des README (inkl. „guarantees 100% precise") sind Behauptungen. Regressionen in Retrieval- oder Antwortqualität sind unsichtbar.
→ Fix: [eval-plan.md](eval-plan.md), TICKET-201/202.

**K2 — Methoden-Bodies werden beim Parsen auf 500 Zeichen gekappt; `get_source` liefert die Amputation bevorzugt aus.**
Befund: `RoslynAstParser.cs:462-470` (`GetTruncatedContent`, angewandt auf Method :161 und Constructor :232). `EmbeddingTextBuilder` (MaxBodyChars 1500, Head+Tail-Design aus TICKET-105) sieht nie mehr als diese 500 Zeichen — das Tail-Fenster ist für C# tot. `ReadTools.cs:91-94` bevorzugt den gespeicherten Body vor dem Datei-Slice.
Auswirkung: Queries auf die zweite Methodenhälfte (Error-Handling, Return-Pfade) können weder per FTS noch per Vektor treffen; Agenten bekommen still amputierten Code als vermeintlich vollständige Quelle — direkter Grounding-Schaden im Kernkorpus (C#).
→ TICKET-204.

**K3 — `INSERT OR REPLACE` korrumpiert den external-content-FTS5-Index; danach degradiert jede Suche still zu unsortiertem `LIKE`.**
Befund: `SqliteGraphStorageProvider.cs:104` (`INSERT OR REPLACE INTO Nodes`), FTS als external-content-Tabelle mit AFTER-Triggern (`SqliteSchema.cs:112-150`), `PRAGMA recursive_triggers` nirgends gesetzt → das implizite DELETE des REPLACE feuert den Delete-Trigger nicht; der FTS-Index behält Geister-Postings unter verwaisten rowids (dokumentierter SQLite-Failure-Mode bis `SQLITE_CORRUPT_VTAB`). Exponiert über `CrossTechLinker.cs:203` (virtuelle Knoten bei jedem Scan re-upserted), `McpToolContext.cs:223` (`record`), `StatsEndpoints.cs:109`. Da `SearchAsync` **jede** `SqliteException` fängt und auf `LIKE '%q%'` ohne `ORDER BY` mit Score 1,0 zurückfällt (`:288-319`), ist die Degradation unsichtbar; der Count-Diff-Rebuild (`SqliteSchema.cs:157-162`) heilt nur beim nächsten Prozessstart und nur bei zufällig abweichender Zeilenzahl.
Auswirkung: Stale-Treffer und Duplikate in FTS, danach kompletter Ranking-Verlust — im Langläufer (Web + MCP) unbemerkt.
→ TICKET-203 (Fix: `INSERT … ON CONFLICT(Id) DO UPDATE` — feuert den UPDATE-Trigger, erhält rowid und stoppt nebenbei das unnötige Zurücksetzen von `NeedsSemanticAnalysis`/Embedding bei Re-Upserts).

**K4 — Kein Gesamt-Prompt-Budget, kein `num_ctx`: Ollama trunkiert still, und zuerst fallen die Grounding-Regeln weg.**
Befund: `BuildRagPrompt` (`OllamaSemanticAnalyzer.cs:158-196`) kappt nur pro Knoten (2.000 Zeichen); UI sendet bis zu 10 Knoten + 6 Chat-Turns (`app.js:255,313-316`) ≈ 6-8k Tokens. `num_ctx` wird nie gesetzt (0 Treffer in `src`), Ollama-Default oft 2048-4096. Instruktionsblock (Exklusivität, Abstention, Zitierpflicht, Injection-Fence) steht **am Anfang** — Tail-Retention-Truncation wirft ihn zuerst weg.
Auswirkung: Genau im kontextreichen Fall generiert das Modell frei über rohem Code, ohne dass Server oder Nutzer es merken — größtes einzelnes Halluzinationsrisiko im Antwortpfad.
→ TICKET-205.

**K5 — Provenance-Integrität (Kern-Differenzierungsmerkmal) wird von zwei Schreibpfaden verletzt.**
Befund: (a) Der name-basierte Fallback des `SemanticCsharpLinker` (heuristisch, multi-Kandidat: `SemanticCsharpLinker.cs:168-175`) persistiert Kanten ohne Provenance (`:120-122`) → Default `Extracted` (`GraphEdge.cs:20`); der Stempel-Enforcement-Punkt (`GraphIndexScanner.cs:679-680`) deckt nur Parser-Kanten. (b) LLM-generierte `RELATES_TO`-Konzeptkanten werden per Raw-SQL ohne Provenance-Spalte eingefügt (`SqliteGraphStorageProvider.cs:1443-1445`, Spalten-Default 0 = Extracted). Zusätzlich claimen die Regex-Parser `GraphQLParser` und `SitecoreXmCloudPlugin` mangels `DefaultProvenance`-Override Extracted (`IFileParser.cs:37`).
Auswirkung: Konsumenten, die nach Provenance filtern/ranken (`references provenance=extracted`, Capsule, Blast-Radius), behandeln Heuristik und LLM-Raten als Compiler-Fakten — das Vertrauensmodell, mit dem Shonkor sich von Vektor-RAG abgrenzt, ist an genau diesen Stellen hohl.
→ TICKET-207.

### HOCH

**H1 — FTS5 wirft bei normalen Code-Queries Syntaxfehler → unsortierter LIKE-Fallback als „Ranking".** `Foo.Bar`, `nomic-embed-text`, `List<T>` sind FTS5-Syntax (`SqliteGraphStorageProvider.cs:265-319`); der Fallback hat kein ORDER BY, Score uniform 1,0, `%`/`_` unescaped; `search_hybrid` füttert diese Scheinränge in RRF (`FindTools.cs:248,267`). Fix: Tokens in Phrasen-Quotes sanitizen statt fallbacken. → TICKET-203.

**H2 — Zitate werden nie validiert.** Der gesamte Mechanismus ist eine Prompt-Bitte (`OllamaSemanticAnalyzer.cs:163-168,184`); Modelltext geht wörtlich raus. Erfundene oder falsch zugeordnete `[Name @ file:lines]`-Label sind für Nutzer nicht von echten unterscheidbar. Fix: Label-Set-Validierung + Flagging, klickbare Links. → TICKET-206.

**H3 — Keine Relevanz-Schwelle vor dem LLM-Call.** Kontext = Top-10 der letzten Suche, Score wird verworfen (`app.js:255-257`); Server ruft das LLM bei jedem auflösbaren NodeId-Set (`SearchEndpoints.cs:114-141`). Abstention hängt allein am Instruction-Following eines ~7B-Modells. Auch `search_semantic` hat keinen Similarity-Floor und blendet Scores im MCP-Output aus (`FindTools.cs:197-202`). → TICKET-206.

**H4 — Inkrementeller Relink zerstört die Kanten, auf denen `implementations_of` basiert.** Tool fragt nur Name-Target-Kanten ab (`AnalyzeTools.cs:357-360`), Relink löscht IMPLEMENTS/EXTENDS beider Formen (auch für nie re-geparste Referenzer-Dateien, `GraphIndexScanner.cs:484-489`) und re-emittiert nur Id-Target (`SemanticCsharpLinker.cs:36,105-111,206-219`). Jeder `reindex_file` erodiert die Antwort auf „wer implementiert IFileParser?" weiter — kumulativer Recall-Verlust in einem Headline-Tool. → TICKET-213.

**H5 — Name-Target-Phantomknoten wirken als Traversal-Hubs; JS-Imports verbinden faktisch nie.** Roslyn-Basistyp-Kanten targeten Display-Strings (`IRepository<Foo>`, `RoslynAstParser.cs:345-356`) mit „I+Großbuchstabe"-Heuristik als Extracted; die Subgraph-CTE expandiert über nicht-existente Endpunkte (`SqliteGraphStorageProvider.cs:471-482`) → alle gleichnamigen Basen namespacenübergreifend 2 Hops voneinander. JS: `IMPORTS`-Targets ohne Extension-Resolution matchen die `.tsx`-Node-Ids nie (`JavaScriptParser.cs:114-142`) — intra-Projekt-JS-Graph leer, Package-Namen (`react`) als Phantom-Hubs. → TICKET-213.

**H6 — Markdown ist nicht gechunkt.** `MarkdownSection`-Knoten haben weder Content noch StartLine/EndLine (`MarkdownHierarchyParser.cs:76-87`); ihr Embedding-Text ist nur der Titel; Doku-Retrieval funktioniert nur auf File-Ebene (Head+Tail 1500 Zeichen, Cap 100k ohne Marker). Sektion-Zitate unmöglich. → TICKET-211.

**H7 — Zeilennummern inkonsistent: C# 0-basiert, Plugins 1-basiert, Doku sagt 1-basiert.** `GraphNode.cs:20-24` vs `RoslynAstParser.cs:128,163,198,234,329` (Roslyn `.Line` ist 0-basiert) vs `PythonParser.cs:61`; `TryReadSourceSlice` nimmt 0-basiert an (`McpToolContext.cs:119-123`). Jede C#-Zitatangabe (`locate`, `outline`, `edit_plan`-Checklisten) zeigt eine Zeile zu hoch; Plugin-Knoten slicen falsch. → TICKET-208.

**H8 — `set_project` über das HTTP-Relay meldet Erfolg, wirkt aber nicht.** Pro POST wird ein neuer Handler/Context erzeugt (`McpEndpoints.cs:68-70`); der Session-Override (`MetaTools.cs:118`) stirbt mit dem Request; der stdio-Proxy sendet jede Zeile als eigenen POST. Agent arbeitet danach transparent auf dem falschen Projekt-Graph — Confident-Lie-Semantik. → TICKET-209.

**H9 — Kein Projekt-Root-Containment in Datei-Tools (Path Traversal by design).** Absolute Pfade und `@/../`-Handles passieren ungeprüft (`EditLoopTools.cs:46-50`, `McpToolHelpers.cs:174-180`, gleiches Muster in `check_edit`, `outline`, `freshness`, `review`); `reindex_file` indiziert beliebige Dateien, `search_graph`/`get_source` exfiltrieren sie. SaaS-Pfad ist geschützt (tenant-locked ohne Parser), lokal + `Security:AllowLocalBypass` (`ApiKeyMiddleware.cs:30-43`) wird daraus ein unauthentifizierter Arbitrary-File-Read. → TICKET-209.

**H10 — MCP `generate_capsule` ignoriert den Budget-Synthesizer.** Seeds FTS-only, Legacy-Overload ohne Seed-Schutz, blinde Tail-Truncation am letzten `##` (`ReadTools.cs:317-335`, `McpToolHelpers.cs:186-202`); die alphabetische Datei-Gruppierung des unlimitierten Renderers (`ContextCapsuleSynthesizer.cs:169`) schneidet unter Truncation ausgerechnet relevante Seeds weg — während die Web-Endpoints es korrekt machen (`SearchEndpoints.cs:359-360`). → TICKET-214.

**H11 — Embedding-Lifecycle im Edit-Loop kaputt.** Ein-Zeilen-Edit verwirft Summaries/Embeddings der ganzen Datei (kein per-Node-Hash, `GraphIndexScanner.cs:612-613` + `SqliteGraphStorageProvider.cs:147-149`); der stdio-MCP-Host re-embedded nie (nur der Web-Worker tut es) → editierte Dateien fallen still aus `search_semantic`; die CLI re-embedded umgekehrt **alles** bei jedem `--embed`-Lauf (`Program.cs:348-349`, kein `Embedding IS NULL`-Filter). Dazu: Enrichment-Race stempelt veraltete Embeddings auf frischen Content (`SqliteGraphStorageProvider.cs:1476-1492` ohne Content-Guard) und die Queue kann an 16 permanent fehlschlagenden Knoten für immer verklemmen (`:1385-1394` ohne Ordering/Attempt-Tracking). → TICKET-212.

**H12 — Vektorsuche ist ein Full-Table-Blob-Scan pro Query.** `SELECT Id, Embedding FROM Nodes …` + per-Row-Alloc (`SqliteGraphStorageProvider.cs:373-405`); bei 100k+ Knoten ~300 MB I/O und 100k Allokationen pro Query, `search_hybrid` verdoppelt das. Korrektheit ok (exakter Top-K-Heap), Skalierung nicht. → TICKET-215.

**H13 — MCP-Tool-Fehler als JSON-RPC-Protokollfehler statt `isError:true`-Result.** `McpRequestHandler.cs:191-195` → `-32603`; Clients verstecken die actionable Message vorm Modell (Spec: execution errors gehören ins Result). Dazu `ping` nicht implementiert (Spec: MUST), Parse-Error ohne `-32700`-Antwort, Protokollversion wird ungeprüft geechot (`:133-136`). → TICKET-210.

**H14 — Chat-History-Laundering umgeht den Injection-Fence.** Frühere (aus untrusted Kontext abgeleitete) Assistant-Antworten landen via `composedQuery` im vertrauenswürdigen `NUTZERFRAGE`-Slot (`app.js:312-316`) — Zwei-Stufen-Injection am „Kontext ist Daten"-Fence vorbei. Der vorhandene Injection-Detector (`SuspiciousContentPostProcessor.cs`) ist vom Antwortpfad komplett entkoppelt. → TICKET-206.

### MITTEL

- **M1** nomic-Task-Prefixe default aus — und der A/B-Test, der „within noise" ergab, embeddete Queries mit dem **Document**-Prefix (`RetrievalBenchmark.cs:68` → kind-loser Overload, `OllamaEmbeddingService.cs:42-43`), maß also nie die Produktions-Asymmetrie; Prefix-Änderung triggert kein Re-Embed (`MarkStaleEmbeddingsForReembedAsync` prüft nur Dim/Modell). → TICKET-202/212.
- **M2** Keine Atomarität über delete→Nodes→Edges (drei getrennte Transaktionen, `GraphIndexScanner.cs:199-217`): Crash nach Node-Commit hinterlässt hash-validen, kantenlosen Dateigraph, den der Hash-Skip nie repariert. → TICKET-212.
- **M3** Plugins erzeugen konfligierende File-Nodes: `DockerPlugin.cs:466-476` clobbert nichtdeterministisch den ContentHash des Scanners (permanenter Re-Index), `PythonParser.cs:29-37` erzeugt zweite File-Identität. → TICKET-212.
- **M4** Node-Id-Schema: Generics-Arity fehlt (Kollision `Foo`/`Foo<T>`), Partials splitten auf beliebige Teile, Overload-Span-Ids churnen bei Edits darüber; Relink via `TypeReferences` (nur Typnamen) verfehlt Chained-Calls/using-static → hängende CALLS-Kanten. → TICKET-213.
- **M5** `GraphEdge.Properties` wird nie persistiert (4-Spalten-Tabelle, `SqliteSchema.cs:60-68`); `INSERT OR IGNORE` friert veraltete Provenance ein. → TICKET-207.
- **M6** Traversal ohne Hub-Damping/Provenance-Filter; `find_path` routet über `BELONGS_TO_MODULE`/`RELATES_TO`-Hubs; OR-Join in der CTE verhindert beide Edge-Indizes (Full-Scan pro Ebene). → TICKET-213/215.
- **M7** Unbounded MCP-Outputs: `limit`, `hops`, `maxHops` unclamped, `maxChars` default unlimitiert (`FindTools.cs:43,125`, `ReadTools.cs:106,221,250-255,329`, `AnalyzeTools.cs:473`) — im Kontrast zur sonst disziplinierten Clamping-Praxis. → TICKET-210.
- **M8** FTS indiziert `Summary` nicht (`SqliteSchema.cs:114-118`); Concept-Knoten werden nie embedded (`SqliteGraphStorageProvider.cs:1392,1432-1433`) — die Konzeptebene ist für Suche unsichtbar. → TICKET-211.
- **M9** `signature`-Property wird von keinem Parser geschrieben, obwohl `EmbeddingTextBuilder.cs:39` sie liest; Klassen-Knoten haben keinen Content → Klassen-Vektor ≈ nur Name auf CLI-Datenbanken. → TICKET-204.
- **M10** Retry-Hygiene: beide Ollama-Services retryen Cancellation und deterministische 4xx (`OllamaEmbeddingService.cs:87-96`, `OllamaSemanticAnalyzer.cs:128-137`); Blocking-RAG retryt 3× die **volle** Generierung (bis ~6 min); Webhook-Hintergrund-Scan captured den Request-Token (`WebhookEndpoints.cs:241-245`) — Push-Indexierung kann still sterben. → TICKET-210 (Anhang) / improvements.
- **M11** `Security:AllowLocalBypass=true` deaktiviert hinter Reverse-Proxy die Auth komplett (`ApiKeyMiddleware.cs:26-43`); kombiniert mit H9. → TICKET-209.
- **M12** `record`-Memory: unbegrenzte Länge, Kanten zu unverifizierten `connectedNodeIds` (`McpToolContext.cs:226-241`), persistenter Cross-Session-Injection-Kanal; indizierter Content in Tool-Outputs nirgends als Daten markiert. → TICKET-209.
- **M13** Antwortsprache hardcoded Deutsch (`OllamaSemanticAnalyzer.cs:183,194`) bei EN/TR-UI — erhöht Instruction-Load des kleinen Modells und drückt Compliance der übrigen Regeln. → TICKET-206.
- **M14** `GetAllNodesAsync` (inkl. Content) auf Request-Pfaden (`InsightsEndpoints.cs:32,74`, `MemoryAndStatsTools.cs:80,139,204`) — pro Klick Volllast auf großen Graphen; `architecture` macht bis zu 3.000 sequenzielle Edge-Queries. → improvements (V13).
- **M15** XmCloud-Komponenten name-keyed über Dateigrenzen (Kollision in Monorepos, `SitecoreXmCloudPlugin.cs:233`); CrossTechLinker-`NormalizeName` strippt „controller" überall (`ControllerFactory`→`factory`, `CrossTechLinker.cs:47-52`); Multi-Kandidat sollte `Ambiguous` sein. → TICKET-213.

### NIEDRIG (Auswahl)

FTS-Tie-Order ohne Sekundärschlüssel; kein `seed` bei temperature 0, `AnalyzeNodeAsync` ganz ohne Temperature (nichtdeterministische Summaries im RAG-Prompt); Mid-Stream-Fehlermarker landet als Assistant-Text in der Chat-History; `/api/ask` ohne NodeIds-Cap/Dedup; keine `IndexedAt`-Timestamps; Stale-Cleanup mit rohem Pfad-Präfix (`App` matcht `App2`); JS-Ids lowercased vs. Original-Case-File-Nodes (hängende `DEFINED_IN`-Kanten auf Windows-Checkouts); Relationship-Vokabular als Magic Strings in ~20 Dateien; RRF ohne Quellen-Gewichtung; `rag-chunk-cache.json` cached nicht per Embedding-Modell; deutscher Truncation-Marker in englischen Embedding-Dokumenten; Bench-Match per `Id.Contains` (Substring-Inflation); Exception-Messages im `-32603` können Pfade leaken.

---

## Was trägt (verifiziert, je ein Satz)

- **SQLite-Layer:** Connection-per-Op + WAL + busy_timeout, Parameter-Chunking, Batch-Edge-Loading, injection-sichere Parametrisierung, korrekte zyklenfeste CTE mit deterministischem `MIN(Depth)` — deutlich über Hobby-Niveau.
- **Async/Streaming:** Null Vorkommen von `.Result`/`.Wait()`/`async void` in `src`; sauberes `IAsyncEnumerable`-NDJSON-Streaming mit Truncation-Marker und bewusstem No-Retry nach First-Byte.
- **Embedding-Versionierung:** Dim+Modell pro Vektor gestempelt, Stale-Reconcile beim Start, Query-Zeit-Dimension-Guard — genau das, was die meisten RAG-Systeme falsch machen.
- **Drift-/Freshness-Modell:** Hash-Skip, `DetectDriftAsync`, git-aware Reconcile, `TypeReferences`-Reverse-Index, Stale-Warnungen an Tool-Outputs, Id-Schema-Versionierung via `user_version`.
- **MCP-Token-Effizienz & Tenant-Isolation:** kompakte Defaults + opt-in verbose, `@/`-Handles, Provenance-Tags pro Kantenzeile, ehrliche Empty-Results; SaaS-seitig unforgeable Tenant über `HttpContext.Items`, constant-time Key-Vergleich.
- **Enrichment-Worker:** per-Cycle-DI-Scope (Handler-Rotation), echter Circuit-Breaker mit Exponential-Backoff, Linked-CTS-Sibling-Cancellation, Live-Config.
- **Benchmark-Ehrlichkeits-Kultur:** faire Same-Subgraph-Baseline, matched-token Vergleich, CIs, reproduzierbare Kommandos — die Messfehler (K1) sind Bugs, kein Spin.
