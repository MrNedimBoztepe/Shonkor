# Kritisches Review #2: shonkor — Präzises Graph-RAG (C#/.NET) — Stand nach Roadmap-Umsetzung

**Reviewer-Rolle:** Senior Engineer Graph-RAG / Retrieval / MCP
**Datum:** 2026-07-02 · **Branch:** feat/precision-graphrag-roadmap (PR #38) · **Fokus:** Präzision & Grounding
**Kontext:** Dies ist der **zweite** Durchgang, am *umgesetzten* Code. Der erste Review + die Umsetzung sind in [results.md](results.md) und im [CHANGELOG](../CHANGELOG.md) dokumentiert. Hier wird geprüft, was *jetzt* — nach den Verbesserungen — die Antwortqualität gefährdet, inklusive schonungsloser Prüfung der neu gebauten Teile.

---

## Executive Summary (1 Seite)

Die Roadmap hat echte, gemessene Verbesserungen gebracht (semantische C#-Auflösung als Default, Code-Embeddings, budgetierte Capsule, Zitate im RAG-Prompt, Eval-Harness). **Das trägt.** Aber der zweite Blick deckt einen unbequemen, code-belegten Kern auf:

**Die zwei/drei Dinge, die die Antwortqualität jetzt wirklich gefährden:**

1. **Die Retrieval-Verbesserungen erreichen die Hauptpfade nicht (KRITISCH).** Der gemessene Sprung „Intent-Recall@10 0,27 → 0,93" hängt an *Code-Embeddings*, die es auf den meistgenutzten Wegen gar nicht gibt:
   - **CLI `index` erzeugt keine Embeddings.** Der Index-Pfad ([Program.cs `ParseAndRunIndexAsync`](../src/Shonkor.CLI/Program.cs:203)) ruft nie einen Embedding-Service auf — Embeddings entstehen ausschließlich im Web-Hintergrund-Worker `SemanticEnrichmentService`. Ein offline per `shonkor index` gebauter Graph hat **0 Embeddings**.
   - **Der stdio-MCP-Server (der Agenten-Pfad) wird ohne Embedding-Service gebaut** ([Program.cs:544](../src/Shonkor.CLI/Program.cs:544) — `embeddingService`-Parameter bleibt `null`). Damit ist `search_semantic` dort gar nicht gelistet ([MetaTools.cs:50](../src/Shonkor.Infrastructure/Services/Mcp/Tools/MetaTools.cs:50)), und `/api/search/hybrid` ist kein MCP-Tool.
   - **Folge:** Auf dem primären Agenten- (MCP) und Offline- (CLI) Pfad ist die Retrieval-Präzision **unverändert gegenüber vor der Roadmap** — reines FTS + Graph. Die 0,93 gelten nur für einen web-angereicherten Graphen mit laufendem Ollama. Das ist die Lücke zwischen versprochener Zahl und dem, was die meisten Nutzungen tatsächlich bekommen. (→ **K1**)

2. **Grounding wird verlangt, aber nicht verifiziert (KRITISCH/HOCH).** Die RAG-Antwort fordert per Prompt Zitate `[Name @ file:zeilen]` und läuft mit `temperature=0` — gut. Aber **nichts prüft, ob die zitierte Stelle die Aussage tatsächlich stützt**, und die Eval misst nur *Retrieval* (Precision/Recall/MRR), **keine Faithfulness/Abstention** ([Shonkor.Eval/Program.cs](../src/Shonkor.Eval/Program.cs) — retrieval-only). Das kleine lokale Modell halluzinierte im Smoke-Test sichtbar („Lernmaterialien"), während das Zitat formal korrekt aussah. „Grounded" ist damit weiterhin überwiegend eine *Behauptung*, nicht ein *gemessener* Zustand. (→ **K2**)

3. **Hybrid-Retrieval ist verwaist (HOCH).** Die RRF-Fusion existiert nur als REST-Endpoint `/api/search/hybrid` — **ohne Aufrufer**: die Dashboard-UI toggelt weiter FTS vs. Semantik ([app.js](../src/Shonkor.Web/wwwroot/app.js:797)), und es gibt **kein `search_hybrid` MCP-Tool**. Die Verbesserung sitzt nicht dort, wo Retrieval tatsächlich passiert. (→ **H1**)

**Bottom line:** Der Präzisions-*Kern* (deterministischer Graph, semantische C#-Auflösung als Default) ist solide und real verbessert. Der *semantische/hybride/geerdete* Teil ist gebaut, aber **an den falschen Enden verdrahtet** — die gemessenen Gewinne erreichen weder den Agenten noch den Offline-Nutzer. Reihenfolge der Behebung: **K1 (Verdrahtung/Embeddings am Haupt­pfad) → K2 (Faithfulness messen) → H1 (Hybrid dorthin bringen, wo abgefragt wird)**.

---

## Findings nach Schweregrad

### 🔴 Kritisch

#### K1 — Semantik/Hybrid-Retrieval erreicht den Agenten- und CLI-Pfad nicht
- **Befund:** Embeddings entstehen nur im Web-`SemanticEnrichmentService`; CLI-Index und stdio-MCP haben keine.
- **Beleg:** kein Embedding-Aufruf in [ParseAndRunIndexAsync](../src/Shonkor.CLI/Program.cs:203); MCP-Server ohne `embeddingService` ([Program.cs:544](../src/Shonkor.CLI/Program.cs:544)); `search_semantic` nur bei `HasEmbeddingService` ([MetaTools.cs:50](../src/Shonkor.Infrastructure/Services/Mcp/Tools/MetaTools.cs:50), [McpToolContext.cs:44](../src/Shonkor.Infrastructure/Services/Mcp/McpToolContext.cs:44)).
- **Auswirkung:** Der Kern-Präzisionsgewinn der Roadmap ist auf dem Hauptpfad **nicht wirksam**. Agenten/Offline-Nutzer bekommen FTS-Intent-Recall ~0,27 statt ~0,93. Die README-/results-Zahlen sind nur unter „Web + Ollama-Enrichment gelaufen" wahr — das ist unzureichend kommuniziert.

#### K2 — Grounding ist prompt-gefordert, nicht verifiziert; keine Faithfulness-Eval
- **Befund:** Zitate + `temperature=0` im RAG-Prompt ([OllamaSemanticAnalyzer.cs:143](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs:143)), aber keine Post-hoc-Prüfung der Beleg-Treue und keine Antwort-Metrik in der Eval.
- **Beleg:** Eval enthält nur Precision@k/Recall@k/MRR; kein Faithfulness/Abstention-Lauf ([Shonkor.Eval/Program.cs](../src/Shonkor.Eval/Program.cs)); Abstention-Golden-Fälle im Plan skizziert, aber nicht implementiert.
- **Auswirkung:** Halluzinationen mit formal korrekt *aussehenden* Zitaten bleiben unentdeckt. Der zentrale Auftrag „jede Aussage belegt" ist nicht messbar erfüllt.

### 🟠 Hoch

#### H1 — Hybrid-Retrieval (RRF) hat keinen Consumer
- **Befund:** RRF nur in `/api/search/hybrid`; kein MCP-Tool, kein UI-Aufruf.
- **Beleg:** Endpoint [SearchEndpoints.cs](../src/Shonkor.Web/Endpoints/SearchEndpoints.cs) (`/api/search/hybrid`); UI toggelt FTS/Semantik ([app.js:797](../src/Shonkor.Web/wwwroot/app.js:797)); MCP-Registry ohne Hybrid ([McpToolRegistryFactory.cs:25](../src/Shonkor.Infrastructure/Services/Mcp/McpToolRegistryFactory.cs:25)).
- **Auswirkung:** Der Recall-/Präzisions-Absicherungsmechanismus (BM25 ∪ Vektor) ist effektiv toter Code — er verbessert keine reale Abfrage.

#### H2 — Non-lossy-Fallback reintroduziert die Namens-Ambiguität (H1 aus Review #1)
- **Befund:** Bei nicht auflösbaren Referenzen fällt der Semantik-Linker auf Namensauflösung zurück und verkantet dann wieder zu *allen* gleichnamigen Typen.
- **Beleg:** [SemanticCsharpLinker.ResolveUnresolvedByNameAsync](../src/Shonkor.Infrastructure/Services/SemanticCsharpLinker.cs).
- **Auswirkung:** Auf partiellen/nicht-kompilierenden Checkouts entstehen wieder Über-Kanten in Impact/Rename — bewusst (nie schlechter als reiner Namensmodus), aber die Präzisionsgrenze ist real und nur via Diagnose sichtbar.

#### H3 — Keine Streaming-Antwort; Blocking-LLM-Call bis 2 Minuten
- **Befund:** RAG und Analyse nutzen `stream = false`; kein `IAsyncEnumerable`.
- **Beleg:** [OllamaSemanticAnalyzer.cs:67,195](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs:195); HttpClient-Timeout 2 min.
- **Auswirkung:** Die „Ask AI"-Antwort erscheint erst komplett nach der vollen Generierung — schlechte wahrgenommene Latenz, Timeout-Risiko bei langen Antworten.

### 🟡 Mittel

- **M1 — Embedding-Input char-truncated (1500 Zeichen)** ([SemanticEnrichmentService.BuildEmbeddingText](../src/Shonkor.Web/Services/SemanticEnrichmentService.cs)): große Klassen/Methoden werden nur „kopf-eingebettet"; kein semantisches Chunking → Rumpf-Logik ist im Vektor unsichtbar.
- **M2 — Golden-Set winzig & selbst-authored** (15 Intent-Fälle, 200 Self-Retrieval; shonkor-eigene Namen): Punktschätzungen mit breitem Konfidenzintervall, nicht repräsentativ. „0,93" ist ein Signal, keine belastbare Kennzahl.
- **M3 — Prompt-Injection-Fläche** ungemindert: `get_source`/`generate_capsule` geben indizierten Code **roh** an den Agenten; eingebetteter Schadtext („ignore previous instructions…") in einer Codebasis könnte Agenten-Verhalten beeinflussen. Inhärent bei Code-RAG, aber nirgends benannt/markiert.
- **M4 — Semantik-Default = Dauer-CPU im Hintergrund:** `DriftReconciliationService` baut je Zyklus eine Roslyn-Compilation (durch `SemanticCompilationCache` gemildert), jetzt per Default für jedes Projekt.

### 🟢 Niedrig
- **N1 —** Token-Budget nutzt `chars/4` als groben Token-Proxy (Capsule/Benchmark); reale Tokenizer-Zahl weicht ab.
- **N2 —** `search_semantic` (MCP) — Query-Embedding-Kind prüfen (Query- vs. Document-Prefix-Konsistenz), heute Prefixe ohnehin default aus.

---

## Was trägt (je ein Satz)
- **Semantische C#-Auflösung als Default + non-lossy Fallback** — der Graph-Kern ist jetzt real präzise und robust gegen nicht-kompilierende Repos.
- **Budgetierte Capsule mit Hub-Deckelung** — löst die Token-Explosion aus Review #1 messbar (~88 %).
- **Eval-Harness + Baseline-Gate** — macht Retrieval-Regressionen erstmals erkennbar (auch wenn Antwort-Faithfulness noch fehlt).
- **Async-/Resilienz-Hygiene** — Circuit Breaker, HttpClientFactory-Scopes, `ConfigureAwait(false)` durchgängig; keine Deadlock-Muster.

> Verbesserungen inkl. Alternativen/Risiko/Aufwand: [improvements.md](improvements.md) · Faithfulness-Eval-Ausbau: [eval-plan.md](eval-plan.md) · Reihenfolge: [roadmap.md](roadmap.md) · Tickets: [`tickets/`](../tickets/).
