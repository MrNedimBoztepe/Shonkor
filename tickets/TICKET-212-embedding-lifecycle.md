# TICKET-212 – Embedding-/Enrichment-Lifecycle im Edit-Loop reparieren

**Schweregrad-Bezug:** H11, M1–M3 (Review-Abschnitt MITTEL) · **Aufwand:** L · **Risiko:** mittel × mittel (invasivste Änderung der Roadmap — Feature-Flag + gezielte Tests)

## Kontext
1. **File-weiter Wipe:** Ein-Zeilen-Edit → delete-then-insert (`GraphIndexScanner.cs:612-613`) + Upsert erzwingt `NeedsSemanticAnalysis=1`, Summary/Embedding null (`SqliteGraphStorageProvider.cs:147-149`) für **alle** Knoten der Datei — kein per-Node-Hash.
2. **Kein Re-Embed im stdio-MCP:** `reindex_file` (`EditLoopTools.cs:56-67`) endet nach Scan/Reconcile; nur der Web-Worker embedded — editierte Dateien fallen still aus `search_semantic`, genau im agentischen Edit-Loop.
3. **CLI re-embedded alles:** `Program.cs:348-349` filtert nicht auf `Embedding IS NULL`.
4. **Race:** `UpdateNodeSemanticDataAsync` (`SqliteGraphStorageProvider.cs:1476-1492`) stempelt ohne Content-Guard — nach zwischenzeitlichem reindex_file landet ein Embedding des alten Contents auf frischem Code, Flag wird gelöscht.
5. **Queue-Verklemmung:** `GetNodesPendingSemanticAnalysisAsync` (`:1385-1394`) ohne Ordering/Attempt-Tracking — 16 deterministisch fehlschlagende Knoten (BatchSize) starven den Rest für immer.
6. **Atomarität:** delete→Nodes→Edges als drei Transaktionen (`GraphIndexScanner.cs:199-217`) — Crash nach Node-Commit hinterlässt hash-validen, kantenlosen Dateigraph, den der Hash-Skip nie repariert.
7. **Plugin-File-Nodes:** `DockerPlugin.cs:466-476` clobbert nichtdeterministisch den Scanner-ContentHash (permanenter Re-Index); `PythonParser.cs:29-37` erzeugt eine zweite File-Identität (`file::…`).

## Akzeptanzkriterien
- [ ] Per-Node-Content-Hash: beim Re-Parse einer Datei behalten Knoten mit unverändertem Hash ihr `Summary`/`Embedding` (Carry-over), Flag bleibt 0. Hinter Config-Flag (`Indexing:PerNodeHashCarryover`, Default an nach Bewährung).
- [ ] `reindex_file` embedded die frischen Knoten synchron, wenn der Host einen Embedding-Service hat (CLI-MCP erstellt ihn bereits, `Program.cs:675`).
- [ ] CLI-`--embed` verarbeitet nur `Embedding IS NULL` (bzw. Hash-geändert); `--embed-all` als explizites Voll-Re-Embed.
- [ ] `UpdateNodeSemanticDataAsync` mit Content-Fingerprint-Guard (`WHERE Id=@Id AND ContentHash=@Captured`).
- [ ] `AnalysisAttempts`/`LastAttemptAt`-Spalten; Ordering nach Attempts, Parken nach N Fehlversuchen mit Diagnostic.
- [ ] `ReplaceFileGraphAsync(nodes, edges)`: Clear + Nodes + Edges in **einer** Transaktion, File-Hash zuletzt.
- [ ] Scanner filtert `Type="File"`-Knoten aus Parser-Output (Scanner owns File nodes); Docker-/Python-Plugin angepasst.
- [ ] Szenariotest „Edit-Loop": Methode editieren → `reindex_file` → `search_semantic` findet den neuen Code; unveränderte Nachbar-Methoden behalten ihr Embedding (SQL-Assertion).

## Betroffene Bereiche
`GraphIndexScanner.cs`, `SqliteGraphStorageProvider.cs`, `SqliteSchema.cs` (Migration), `EditLoopTools.cs`, `SemanticEnrichmentService.cs`, `Shonkor.CLI/Program.cs`, `plugins/DockerPlugin.cs`, `plugins/PythonParser.cs`, Tests.

## Abhängigkeiten
Nach TICKET-203 (Upsert-Semantik). Effektmessung: Edit-Loop-Szenariotest + Embed-Kosten (Anzahl Ollama-Calls) vorher/nachher.

## Definition of Done
Alle Kriterien-Tests grün; Flag nach einer Woche Selbstnutzung auf Default-an; CHANGELOG dokumentiert Migrations- und Kostenverhalten.
