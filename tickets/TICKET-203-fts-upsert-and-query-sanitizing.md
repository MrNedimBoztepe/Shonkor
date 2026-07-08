# TICKET-203 – FTS5-Integrität: `ON CONFLICT DO UPDATE`-Upsert + Query-Sanitizing

**Schweregrad-Bezug:** K3, H1 · **Aufwand:** S · **Risiko:** niedrig × mittel (zentrale Upsert-Semantik — Testpflicht)

## Kontext
1. `UpsertNodesAsync` nutzt `INSERT OR REPLACE` (`SqliteGraphStorageProvider.cs:104`). Bei der external-content-FTS-Tabelle (`SqliteSchema.cs:112-150`) feuert das implizite DELETE des REPLACE die AFTER-DELETE-Trigger nicht (kein `recursive_triggers`) → Geister-Postings, bis hin zu `SQLITE_CORRUPT_VTAB`. Exponiert u. a. über `CrossTechLinker.cs:203`, `McpToolContext.cs:223`, `StatsEndpoints.cs:109`.
2. `SearchAsync` fängt jede `SqliteException` und fällt auf `LIKE '%q%'` ohne `ORDER BY`, Score uniform 1,0 zurück (`:288-319`) — und normale Code-Queries (`Foo.Bar`, `List<T>`, `nomic-embed-text`) sind FTS5-Syntaxfehler. `search_hybrid` füttert diese Scheinränge in RRF.

## Akzeptanzkriterien
- [ ] Node-Upsert als `INSERT … ON CONFLICT(Id) DO UPDATE` (rowid bleibt, UPDATE-Trigger feuert); `NeedsSemanticAnalysis`/`Summary`/`Embedding` werden nur zurückgesetzt, wenn sich `Content`/`ContentHash` tatsächlich ändert.
- [ ] Zusätzlich `PRAGMA recursive_triggers=ON` in `OpenConnectionAsync` (Defense in depth).
- [ ] FTS-Query-Sanitizer: Tokens in `"`-Phrasen (mit `""`-Escaping), optional Prefix-`*` für das letzte Token; Groß-`AND/OR/NOT` und `Spalte:`-Filter neutralisiert.
- [ ] LIKE-Fallback nur noch bei echtem FTS-Ausfall, mit `ESCAPE`-Behandlung für `%`/`_`, definierter Ordnung (Name-Match zuerst, dann Id) und einem Flag im Ergebnis, das `search_hybrid` zur Abwertung in RRF nutzt.
- [ ] Konsistenztest: N×(Insert, Re-Upsert mit geändertem Content, Suche) → FTS-Trefferzahl == erwartete Trefferzahl, keine Duplikate; Test schlägt auf dem alten Code fehl.
- [ ] Query-Test: `Foo.Bar`, `List<T>`, `a-b`, `"unbalanced` liefern BM25-geordnete Treffer ohne Exception.

## Betroffene Bereiche
`SqliteGraphStorageProvider.cs` (Upsert, SearchAsync), `SqliteSchema.cs`, `HybridFusion`/`FindTools` (Fallback-Signal), Tests.

## Abhängigkeiten
Vor TICKET-212 (dessen Carry-over-Logik baut auf der neuen Upsert-Semantik auf).

## Definition of Done
Beide Tests grün, Bench-Retrieval-Zahlen (TICKET-202-Suite) unverändert oder besser, Changelog-Eintrag mit Hinweis auf empfohlenen einmaligen FTS-Rebuild (`INSERT INTO NodesFts(NodesFts) VALUES('rebuild')`) für Bestandsdatenbanken.
