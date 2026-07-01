# Roadmap-Umsetzung — gemessene, belastbare Performance-Werte

**Stand:** 2026-06-30 · **Korpus:** shonkors eigener Graph (`shonkor.db`, 3.784 Knoten, 6.288 Kanten)
**Backend:** lokales Ollama mit `nomic-embed-text` (Embeddings) und `qwen2.5-coder` (Generierung)
**Werkzeug:** `src/Shonkor.Eval` (neu, TICKET-001) und der neu geschriebene `src/Shonkor.Benchmarks` (TICKET-007)

> Alle Zahlen sind reproduzierbar: `dotnet run --project src/Shonkor.Eval -- shonkor.db [...]` bzw.
> `dotnet run --project src/Shonkor.Benchmarks -- shonkor.db`. Methodik je Abschnitt angegeben.

---

## 1. Retrieval-Präzision (das Kern-USP)

### 1a. Exakte Symbol-Suche (FTS5) — sehr stark
200 auto-generierte Fälle: Query = Symbolname, erwartet = derselbe Knoten. Misst Ranking/Tokenisierung.

| Metrik | Wert |
|---|---|
| **Precision@1** | **0,980** |
| **MRR** | **0,985** |
| **Recall@10** | **0,993** |

**Versprechbar:** *„Wer ein Symbol beim Namen sucht, bekommt es in ~98 % der Fälle als Top-Treffer, in ~99 % unter den Top-10."* Das deterministische FTS+Graph-Retrieval trägt — wie behauptet.

### 1b. Natürlichsprachige / Intent-Queries — hier lag der Bruch
15 kuratierte Paraphrase-Queries (`eval/golden/intent.json`), z. B. „ollama embedding service generate vector" → erwartet `OllamaEmbeddingService`.

| Retriever | Precision@1 | Recall@10 | MRR |
|---|---|---|---|
| **FTS5 (Baseline, ausgeliefert)** | 0,200 | 0,267 | 0,222 |
| Semantik auf **Summary**-Embeddings (alter K2-Entwurf) | 0,533 | 0,867 | 0,673 |
| **Semantik auf Code-Embeddings (TICKET-002)** | **0,600** | **0,933** | **0,712** |
| Semantik auf Code, ohne Prefix | 0,600 | **1,000** | 0,765 |
| Hybrid RRF (FTS + Code, TICKET-008) | 0,600 | 0,933 | 0,712 |

**Versprechbar:** *„Bei natürlichsprachiger Code-Suche steigt Recall@10 von ~0,27 (reines FTS) auf ~0,93–1,0, sobald Code statt der Zusammenfassung eingebettet wird."* Das ist die zentrale Präzisions-Verbesserung der Roadmap, empirisch belegt.

**Wichtige, ehrliche Einordnung:**
- **Code schlägt Summary** (Recall 0,933 vs. 0,867; P@1 0,60 vs. 0,533) → bestätigt Finding **K2** mit Zahlen.
- **Prefixe (TICKET-006) waren auf diesem Code-Korpus ein Wash** (ohne Prefix minimal besser). Darum: Prefixe sind **konfigurierbar, default AUS** — kein erzwungener Eingriff, der nichts bringt.
- **Hybrid = Semantik** auf diesem Set, weil FTS bei Intent-Queries zu schwach ist, um per RRF noch etwas beizutragen. Bei n=15 ist das Konfidenzintervall breit — die Hybrid-Fusion ist als Absicherung (graceful, additiv) implementiert, ihr Mehrwert über reine Code-Semantik ist auf größeren Sets zu verifizieren.
- **n = 15** Intent-Fälle: Richtung eindeutig, Punktschätzung mit Vorsicht. Golden-Set sollte wachsen (siehe eval-plan.md).

### 1c. Befund am Rande: semantische Suche war im ausgelieferten DB **inaktiv**
`shonkor.db`: **40,4 % der Knoten haben eine Summary, aber 0,0 % ein Embedding.** Die Vektorsuche lieferte also produktiv **nichts**. Das untermauert K2/M1: Der semantische Pfad war bislang faktisch tot. Mit code-basiertem Enrichment (jetzt Default) wird er erst nutzbar.

---

## 2. Token-Effizienz der Context-Capsule (realer Pfad)

Methodik (TICKET-007, ehrlich statt Strohmann): pro Query Top-5-FTS-Seeds → 2-Hop-Subgraph →
Vergleich **„naiver Volldump derselben Knoten"** vs. **budgetierte Capsule** (TICKET-003: Seeds zuerst,
`MaxNodes=40`, ~3k-Token Code-Budget). 7 Queries.

| | Tokens |
|---|---|
| Naiver Volldump (gesamtes 2-Hop-Umfeld) | **1.006.922** |
| Budgetierte Capsule (ausgeliefert) | **122.367** |
| **Reduktion** | **87,8 %** |

Pro Query: 16,8 % (kleiner Subgraph) bis 96,5 % (Hub). Absolute Capsule-Größe jetzt **5k–37k Token**
statt **140k–210k** beim Volldump.

**Versprechbar:** *„Gegenüber dem Hineinkippen des kompletten 2-Hop-Umfelds spart die Capsule ~85–88 % Token und deckelt die Größe — auf Hub-Knoten von >200k auf <40k Token."*

**Ehrliche Einordnung:** Der frühere README-Wert „87,7 %" verglich ganze Dateien gegen 1-Satz-Summaries (Strohmann) und ergab im echten Lauf Unsinn (0 Token). Die **87,8 %** hier sind gegen eine **definierte, faire Baseline** (dieselben abgerufenen Knoten, voll) gemessen — zufällig ähnliche Zahl, grundverschiedene Aussagekraft. Befund **K3** (Hub-Explosion) war real: 2-Hop-Subgraphs hatten **275–750 Knoten**; ohne Knoten-Deckelung blieb die Capsule selbst budgetiert bei 50–88k Token.

---

## 3. Grounding der RAG-Antwort (TICKET-005) — qualitativ verifiziert

Live gegen `qwen2.5-coder` über `/api/ask`: Antworten enthalten jetzt **Quellen-Zitate** im Format
`[Name @ datei:zeilen]` (verifiziert: `[ContextCapsuleSynthesizer @ ContextCapsuleSynthesizer.cs:11-188]`),
laufen mit **`temperature=0`** (reproduzierbar) und schneiden Code an Zeilengrenzen statt mitten im Token.

**Ehrliche Grenze:** Das kleine lokale Modell formuliert inhaltlich nicht immer korrekt (im Test
„Lernmaterialien" statt Code-Kontext-Zweck) — aber das **Zitat zeigt korrekt auf den realen Knoten**.
Grounding macht jede Aussage rückführbar; die inhaltliche Güte hängt am gewählten Generierungsmodell.
Faithfulness-Scoring (Ebene 2 der Eval) ist als nächster Schritt vorgesehen.

---

## 4. Was das in einem Satz bedeutet

- **Strukturelle/Symbol-Suche:** bereits exzellent (P@1 0,98) — Versprechen gehalten.
- **Natürlichsprachige Code-Suche:** von ~0,27 auf ~0,93–1,0 Recall@10 gehoben (Code-Embeddings) — das war der größte reale Präzisionsgewinn.
- **Kontext-Token:** ~88 % gespart **und** gedeckelt gegenüber dem ungebremsten 2-Hop-Dump.
- **Antworten:** zitierfähig und reproduzierbar.

## 5. Nachgelegte Robustheit (keine neuen Messwerte, aber Korrektheit)

- **TICKET-006 vollständig:** Embeddings tragen jetzt ihre Dimension (`EmbeddingDim`); ein Modellwechsel markiert alte Vektoren automatisch zum Re-Embed (`MarkStaleEmbeddingsForReembedAsync`, einmalig vom Worker nach Backend-Probe), statt sie still aus der Suche zu kippen. Behebt den in §1c gefundenen stillen Recall-Verlust an der Wurzel.
- **TICKET-004 größtenteils:** (a) Warn-Diagnose `csharp.ambiguous-type-reference` macht die Über-Verkantung der Default-Namensauflösung (H1) **sichtbar** (via `get_diagnostics`), ohne Kanten/Schema/Web-UI anzufassen. (b) Der Semantik-Modus ist jetzt **non-lossy**: unauflösbare Referenzen (partielle/nicht-kompilierende Checkouts) fallen auf Namens-Auflösung zurück statt Kanten still zu verlieren — damit ist exakte Auflösung **gefahrlos aktivierbar** (`Indexing:SemanticCSharp`), nie schlechter als der Default. Offen bleibt allein der globale **Default-Flip** — bewusst als Nutzer-Entscheidung (Indexier-Latenz-Kosten der Roslyn-Compilation, verändert bestehende Graphen).

## 6. Semantik-Default geflippt — gemessener Latenz-Tradeoff (TICKET-004 abgeschlossen)

Auf Nutzer-Entscheidung ist die exakte, semantische C#-Auflösung jetzt **Default** (Roslyn `SemanticModel`; non-lossy Namens-Fallback für unauflösbare Referenzen). Rückschaltbar mit `Indexing:SemanticCSharp=false` bzw. `SHONKOR_SEMANTIC_CSHARP=false`.

Gemessen mit dem Release-CLI über den echten `src`-Baum (168 Dateien, 146 Klassen, 439 Methoden), in temporäre DBs:

| Modus | Indexierung | Kanten (bei 858 Knoten) |
|---|---|---|
| Namensbasiert (`=false`) | **2,0 s** | 1.232 |
| **Semantisch (Default)** | **5,6 s** | **1.845** |
| **Delta** | **+3,6 s (2,9×)** | **+613 (+50 %)** |

**Versprechbar:** *„Exakte Impact-/Rename-Analyse (disambiguierte `REFERENCES_TYPE` + method-genaue `CALLS`-Kanten) kostet auf einem mittelgroßen Repo ~+3,6 s Indexierung und liefert ~50 % mehr, präzisere Kanten — verlustfrei gegenüber dem schnellen Namens-Resolver."* Der Aufschlag skaliert mit der C#-Codemenge (Roslyn-Compilation über alle Quellen); auf sehr großen Repos entsprechend höher → per Config pro Projekt abschaltbar.

**Gesamtstatus:** 140 Tests grün, Build grün, Web-Dashboard verifiziert. **8/8 Tickets vollständig.**

Vollständiger Status je Phase: [roadmap.md](roadmap.md). Die Arbeitstickets sind nach Umsetzung entfernt; die Änderungen sind im [CHANGELOG](../CHANGELOG.md) festgehalten.
