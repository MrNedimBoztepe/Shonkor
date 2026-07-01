# Kritisches Review: shonkor — Präzises Graph-RAG (C#/.NET)

**Reviewer-Rolle:** Senior Engineer Graph-RAG / Retrieval / MCP
**Datum:** 2026-06-30
**Branch:** develop · **Fokus:** Präzision & Grounding der Antworten
**Methodik:** Code gelesen (Ingestion, Graph-Linking, Retrieval, MCP, LLM-Pfad), Aussagen mit Datei-/Zeilenverweisen belegt. Laufzeit-Präzision nicht messbar, weil kein Eval existiert (siehe K1).

---

## Executive Summary (1 Seite)

shonkor hat ein **starkes Fundament und ein schwaches Versprechen**. Das Fundament ist der deterministische, AST-abgeleitete Struktur-Graph (Roslyn) plus FTS5 + rekursive CTEs. Für die *strukturellen* MCP-Tools (`get_source`, `find_usages`, `call_hierarchy`, `signature`, `outline`) ist das tatsächlich präzise und reproduzierbar — das ist das beste am System und sollte das Produkt-Zentrum bleiben.

Das Versprechen — „**100 % präzise**", „**präziser als probabilistische Vektor-DBs**", „**87 % Token-Ersparnis**" — hält der Code an drei zentralen Stellen **nicht**:

1. **Es gibt keinerlei Präzisions-Messung.** Kein Golden-Set, keine Faithfulness-/Precision@k-Metrik, keine Regression. Alle Tests sind Unit-Tests für Parser/Storage/Plugins. „Präzise" ist damit eine **unbelegte Behauptung** — und ohne Eval merkt niemand, wenn eine Änderung die Antwortqualität verschlechtert. (→ **K1**)

2. **Die semantische Suche bettet nicht den Code ein, sondern eine vom kleinen lokalen LLM erzeugte 1-Satz-Zusammenfassung auf Deutsch** ([SemanticEnrichmentService.cs:187](../src/Shonkor.Web/Services/SemanticEnrichmentService.cs:187)). Damit wird gegen einen lossy, potenziell halluzinierten Paraphrase-Vektor gematcht; bei englischer Query entsteht zusätzlich ein Cross-Lingual-Mismatch. Der „semantische" Pfad ist strukturell ungenau. (→ **K2**)

3. **Die Kontext-Assemblierung hat kein Token-Budget und kein Query-Relevanz-Ranking.** Der Capsule-Synthesizer kippt den **vollständigen Quellcode aller Knoten der 2-Hop-Nachbarschaft** in den Prompt, flach nach Datei sortiert, ohne Seeds von Randknoten zu unterscheiden ([ContextCapsuleSynthesizer.cs:159](../src/Shonkor.Core/Services/ContextCapsuleSynthesizer.cs:159)). Das ist „mehr als Top-K reinkippen" — und auf Hub-Knoten explodiert es, womit auch das Token-Versprechen kippt. (→ **K3**)

Dazu kommt ein **High-Finding am Graph-Kern selbst**: Im Default (`SHONKOR_SEMANTIC_CSHARP` aus) werden `REFERENCES_TYPE`-Kanten **per Namen** aufgelöst — „a name match creates an edge to EVERY same-named type" ([CrossTechLinker.cs:119](../src/Shonkor.Infrastructure/Services/CrossTechLinker.cs:119)). Gleichnamige Typen über Namespaces hinweg erzeugen damit **falsche Kanten**, die `blast_radius`, `impact_of` und `rename_plan` direkt verfälschen. Die „100 %"-Präzision gilt nur im opt-in-Semantikmodus, der standardmäßig **aus** ist.

**Bottom line:** Das deterministische Struktur-Retrieval ist gut und sollte geschärft (Semantik-Default) und ehrlich vermarktet werden. Der LLM-/Vektor-/Antwort-Pfad ist heute nicht „grounded" im versprochenen Sinn und braucht: (a) eine Eval, (b) Embeddings auf Code statt Summary, (c) ein Kontext-Budget mit Ranking, (d) Zitate + Determinismus im Antwort-Prompt. Priorität: **K1 → K2 → K3**, in dieser Reihenfolge, weil ohne K1 keine der anderen Verbesserungen verifizierbar ist.

---

## Findings nach Schweregrad

### 🔴 Kritisch

#### K1 — Keine Präzisions-Evaluation; „präzise" ist unbelegt
- **Befund:** Kein Golden-Dataset, keine Metriken (Precision@k, Recall, Faithfulness/Groundedness, Answer-Relevance), keine Regressions-Checks. `grep` über `tests/` und `docs/` findet nur Unit-Regressionen für Parser/CTE/Plugins, keine Retrieval- oder Antwort-Qualitätsmessung.
- **Beleg:** `tests/Shonkor.Tests/*` (22 Dateien, alle struktur-/infrastrukturbezogen); kein Eval-Harness, kein Query→Erwartung-Set.
- **Auswirkung auf Präzision:** Maximal. Jede Aussage über „Präzision" ist nicht falsifizierbar. Eine Änderung an Chunking, Linking, Embedding-Quelle oder Prompt kann die Antwortqualität still verschlechtern — niemand misst es. Das ist die Grundlage, ohne die K2/K3 nicht bewertbar sind.

#### K2 — Semantische Suche bettet die LLM-Zusammenfassung ein, nicht den Code
- **Befund:** Im Enrichment wird pro Knoten erst ein 1-Satz-Summary vom lokalen Modell erzeugt und dann **dieses Summary** eingebettet — nicht Code, Signatur oder Identifier.
- **Beleg:** [SemanticEnrichmentService.cs:182-190](../src/Shonkor.Web/Services/SemanticEnrichmentService.cs:182) (`result = AnalyzeNodeAsync(...)` → `GenerateEmbeddingAsync(result.Summary)`); Summary-Prompt erzwingt **deutsche** 1-Satz-Fachbeschreibung ([OllamaSemanticAnalyzer.cs:46-61](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs:46)).
- **Auswirkung auf Präzision:** Hoch. (a) Garbage-in: ist das Summary generisch/falsch (kleines Modell, `qwen2.5-coder` default), ist der Vektor wertlos. (b) Identifier-/API-Level-Treffer gehen verloren — exakt das, was bei Code-Suche zählt. (c) Query (oft EN, roher Text) vs. Dokument (DE, abstrakter Satz) = Domänen-/Sprach-Mismatch. Der „semantische" Modus ist damit kein verlässlicher Recall-Lieferant und untergräbt das Hybrid-Versprechen des READMEs.

#### K3 — Kontext-Assemblierung ohne Token-Budget und ohne Relevanz-Ranking
- **Befund:** `Synthesize()` rendert für **jeden** Knoten der Subgraph-Nachbarschaft den **vollständigen** `node.Content`, gruppiert nach Datei, sortiert nach Zeilennummer. Kein Budget, keine Truncation, keine Gewichtung Seed > 1-Hop > 2-Hop.
- **Beleg:** [ContextCapsuleSynthesizer.cs:155-162](../src/Shonkor.Core/Services/ContextCapsuleSynthesizer.cs:155); aufgerufen aus `/api/rag/query` mit `hops=2` ([GraphRagEndpoints.cs:40-44](../src/Shonkor.Web/Endpoints/GraphRagEndpoints.cs:40)) und `/api/capsule` ([SearchEndpoints.cs:250-252](../src/Shonkor.Web/Endpoints/SearchEndpoints.cs:250)). Seeds = nur Top-5 FTS, danach blinde 2-Hop-Expansion über rekursive CTE.
- **Auswirkung auf Präzision:** Hoch. Bei Hub-Knoten (z. B. eine Basisklasse mit 200 Referenzen) bläht die 2-Hop-Expansion den Kontext massiv auf → (1) das 87 %-Token-Versprechen kippt ins Gegenteil, (2) das LLM ertrinkt in Rauschen („lost in the middle"), (3) die wirklich relevanten Seeds sind nicht priorisiert. Das ist genau das im Auftrag benannte Anti-Pattern „einfach Top-K (hier: ganze Nachbarschaft) reinkippen".

### 🟠 Hoch

#### H1 — Default-C#-Linking ist namensbasiert → falsche Kanten, untergräbt „100 % präzise"
- **Befund:** Ohne `SHONKOR_SEMANTIC_CSHARP=true` werden `REFERENCES_TYPE`-Kanten per Namensgleichheit gesetzt; bei gleichnamigen Typen in verschiedenen Namespaces entsteht eine Kante zu **jedem** gleichnamigen Typ.
- **Beleg:** [CrossTechLinker.cs:116-152](../src/Shonkor.Infrastructure/Services/CrossTechLinker.cs:116) („resolves … by NAME … a name match creates an edge to EVERY same-named type"); Opt-in-Schalter in [Program.cs:255](../src/Shonkor.CLI/Program.cs:255).
- **Auswirkung:** `blast_radius`, `impact_of`, `rename_plan`, `related_tests` liefern im Default Über-Verbindungen (False Positives). Ein Agent, der `rename_plan` vertraut, editiert ggf. falsche Stellen. Die READMEs „compiler-accurate / 100 % präzise" gelten nur im Semantikmodus.

#### H2 — RAG-Antwort ohne Zitate, ohne Grounding-Prüfung, nicht-deterministisch
- **Befund:** Der Antwort-Prompt instruiert zwar „nur aus Kontext, sonst ‚weiß ich nicht'" (gut), aber: keine Quellen-/Zeilen-Zitate angefordert, keine Post-hoc-Faithfulness-Prüfung, **keine `temperature`/`options`** gesetzt → Ollama-Default (~0.8), also nicht reproduzierbar. Pro Knoten harte 2000-Zeichen-Truncation, die Methodenrümpfe mitten durchschneidet.
- **Beleg:** [OllamaSemanticAnalyzer.cs:143-176](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs:143) (Prompt + `requestBody = { model, prompt, stream=false }`, keine Options); Truncation Zeile 152.
- **Auswirkung:** Anti-Halluzination hängt allein an einer Prompt-Bitte an ein kleines Modell. Ohne Zitate ist keine Aussage zur Quelle rückführbar; ohne `temperature=0` ist „gleiche Query → vergleichbares Ergebnis" nicht gegeben (Reproduzierbarkeit aus dem Auftrag verletzt).

#### H3 — Keine echte Hybrid-Suche (FTS + Vektor + Graph) trotz README-Claim
- **Befund:** FTS5 (`/api/search`) und Vektor (`/api/search/semantic`) sind **getrennte** Endpunkte; der Nutzer/Agent muss einen Modus wählen. Keine Fusion (z. B. Reciprocal Rank Fusion), kein Reranking, keine kombinierte Kandidatenmenge vor der Graph-Expansion.
- **Beleg:** [SearchEndpoints.cs:23-67](../src/Shonkor.Web/Endpoints/SearchEndpoints.cs:23); README spricht von „Hybrid-Suche", Code zeigt zwei Toggles.
- **Auswirkung:** Recall und Präzision bleiben unter dem Möglichen. Gerade weil K2 den Vektor-Pfad schwächt, fehlt die Absicherung durch BM25-Fusion + Reranking.

#### H4 — Benchmark „87 %" misst das Falsche und ist nicht repräsentativ
- **Befund:** Verglichen wird „klassisches RAG = **ganze Quelldatei** senden" gegen „shonkor = **nur 1-Satz-Summary** senden", mit **synthetischen** Konstanten (4 chars/token, 0.005 s/token). Gemessen wird **Token-Volumen**, nicht Antwort-Korrektheit. Der real ausgelieferte Capsule-Pfad sendet aber **vollen Code** (K3), nicht nur Summaries.
- **Beleg:** [Shonkor.Benchmarks/Program.cs:58-107](../src/Shonkor.Benchmarks/Program.cs:58).
- **Auswirkung:** Die 87 %/7,6×-Zahlen im README sind ein Strohmann-Vergleich und nicht das, was der ausgelieferte Pfad tut. Reputations-/Erwartungsrisiko; sollte entweder korrekt gemessen oder relativiert werden.

### 🟡 Mittel

#### M1 — Embedding-Modell ohne Task-Prefixe; Dimensions-Mismatch wird still verworfen
- **Befund:** `nomic-embed-text` wird ohne die trainierten Prefixe `search_query:` / `search_document:` genutzt; zudem werden bei der Vektorsuche Knoten mit abweichender Embedding-Dimension **still übersprungen**.
- **Beleg:** [OllamaEmbeddingService.cs:41-47](../src/Shonkor.Infrastructure/Services/OllamaEmbeddingService.cs:41) (kein Prefix); [SqliteGraphStorageProvider.cs:383](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs:383) (`if (floatCount != queryEmbedding.Length) continue;`).
- **Auswirkung:** Prefix-losigkeit kostet messbar Retrieval-Qualität (immerhin symmetrisch). Der Dimensions-Skip ist gefährlicher: ein **Modellwechsel** macht den Alt-Index still teilweise unsichtbar (Recall-Verlust ohne Fehler/Log). Kein Re-Embed-Trigger, kein Versionsfeld am Embedding.

#### M2 — Vektorsuche ist O(N)-Full-Scan ohne ANN-Index
- **Befund:** Jede semantische Query lädt alle Embeddings und rechnet Cosine in-process (mit bounded Heap — speicherseitig ok).
- **Beleg:** [SqliteGraphStorageProvider.cs:371-403](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs:371).
- **Auswirkung:** Für <100k Knoten vertretbar (deckt sich mit dem bestehenden Arch-Review). Ab Multi-Projekt-/SaaS-Skalierung ein Latenz-Treiber. Heute eher Hinweis als Defekt.

#### M3 — SaaS-`/api/rag/query` liefert Kontext ohne Grounding-Gerüst
- **Befund:** Gibt nur das Capsule-Markdown an den externen LLM zurück — ohne System-Instruktion „nur aus Kontext antworten + Quellen zitieren". Die Grounding-Verantwortung wird ungeführt an den Client delegiert.
- **Beleg:** [GraphRagEndpoints.cs:44-53](../src/Shonkor.Web/Endpoints/GraphRagEndpoints.cs:44).
- **Auswirkung:** Externe Agenten können den präzisen Kontext trotzdem ungrounded verwenden; das Produkt verschenkt seinen Präzisionsvorteil am letzten Meter.

### 🟢 Niedrig

- **N1 — FTS5-`rebuild` bei jedem Start** und **N+1 in `SearchAsync`** sind im bestehenden Arch-Review notiert; Status prüfen (Performance, nicht Präzision).
- **N2 — Summary-Truncation per Zeichenzahl** (8000 bzw. 2000 chars) statt Tokenizer ([OllamaSemanticAnalyzer.cs:41,152](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs:41)) — schneidet an willkürlichen Grenzen.
- **N3 — Fehlerpfade verschlucken Detail:** `/api/rag/query` loggt auf `Console.Error` statt strukturiert ([GraphRagEndpoints.cs:57](../src/Shonkor.Web/Endpoints/GraphRagEndpoints.cs:57)).

---

## Was trägt (in je einem Satz)
- **Deterministischer Struktur-Graph + Roslyn-AST:** echtes Alleinstellungsmerkmal, präzise und reproduzierbar — Produkt-Kern.
- **MCP-Tool-Schnitt:** sinnvoll granular (find/read/analyze/plan/apply), gute Namen, Anti-Halluzination via `verify_exists` und Freshness-Flagging — konzeptionell stark.
- **Resilienz im Enrichment:** Circuit Breaker + Backoff gegen totes Ollama, korrekte HttpClientFactory-Scope-Nutzung ([SemanticEnrichmentService.cs:77-110](../src/Shonkor.Web/Services/SemanticEnrichmentService.cs:77)) — sauber gelöst.
- **Async-Hygiene im LLM-Pfad:** durchgängig `await` + `ConfigureAwait(false)`, Retry mit Backoff, kein `.Result`/`.Wait()` in den heißen Pfaden.

> Details, Lösungsvorschläge inkl. Alternativen, Risiko und Aufwand: siehe [improvements.md](improvements.md). Eval-Aufbau: [eval-plan.md](eval-plan.md). Reihenfolge: [roadmap.md](roadmap.md).
