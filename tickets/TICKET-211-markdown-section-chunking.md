# TICKET-211 – Markdown-Sektions-Chunking + Summary in FTS + Concept-Embeddings

**Schweregrad-Bezug:** H6, M8 · **Aufwand:** M · **Risiko:** niedrig × niedrig (FTS-Rebuild einmalig)

## Kontext
`MarkdownSection`-Knoten haben weder `Content` noch `StartLine`/`EndLine` (`MarkdownHierarchyParser.cs:76-87`) — ihr Embedding-Text ist nur der Titel; der Sektions-Body liegt allein auf dem File-Node (Cap 100k Zeichen ohne Marker, `GraphIndexScanner.cs:26,163-165`). Doku-Retrieval funktioniert damit nur file-granular; Sektions-Zitate sind unmöglich. Außerdem: FTS indiziert `Summary` nicht (`SqliteSchema.cs:114-118`) — AI-Summaries (oft die einzige Intent-Vokabel-Quelle) sind für Keyword-Suche unsichtbar; Concept-Knoten werden nie embedded (`SqliteGraphStorageProvider.cs:1392,1432-1433`).

## Akzeptanzkriterien
- [ ] Jede `MarkdownSection` erhält den Content zwischen ihrem und dem nächsten Header (inkl. `StartLine`/`EndLine` aus den Match-Offsets); Code-Fences und Tabellen bleiben innerhalb einer Sektion intakt; Header innerhalb von Fences werden nicht als Sektionsgrenze gewertet.
- [ ] Übergroße Sektionen (> ~4k Zeichen) werden an Absatzgrenzen in nummerierte Teil-Knoten gesplittet.
- [ ] File-Node-Content-Cap bekommt einen expliziten Truncation-Marker.
- [ ] `NodesFts` um `Summary` erweitert (Schema-Migration + Trigger + Rebuild).
- [ ] Concept-Knoten werden embedded (Name + verbundene Knoten-Namen als Dokument).
- [ ] Golden-Set-Erweiterung (TICKET-202): ≥ 10 Doku-Intent-Fälle mit erwarteter Sektion; Recall@10 messbar verbessert gegenüber Baseline.

## Betroffene Bereiche
`MarkdownHierarchyParser.cs`, `GraphIndexScanner.cs`, `SqliteSchema.cs`, `EmbeddingTextBuilder.cs`, Bench-Golden-Sets.

## Abhängigkeiten
TICKET-202 (Messbarkeit). Re-Index nötig.

## Definition of Done
Doku-Fälle im Bench grün; `get_source` auf eine MarkdownSection liefert den Sektions-Body mit korrektem Zeilenbereich; FTS findet Summary-Vokabular.
