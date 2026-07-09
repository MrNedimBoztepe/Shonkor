# Bug-Hunt-Report: Shonkor (Präzises Graph-RAG mit MCP-Anbindung)

**Datum:** 2026-07-07 · **Scope:** gesamtes Repo (`src/`, Scope-Felder waren leer) · **Stand:** `main` @ `e2031dd`

Methodik: Sechs parallele Deep-Reads pro Subsystem (Retrieval/Embedding, Graph, MCP-Layer, Storage, LLM-Integration, Parser/Ingestion), anschließend manuelle Verifikation der kritischen Funde im Quellcode. Alle als **Bestätigt** markierten Funde sind mit konkretem Code belegt; **Verdacht** heißt: Logik spricht dafür, aber Laufzeit-/Datenprüfung steht aus. Ein Agent-Fund wurde bei der Verifikation **widerlegt** und ist nicht enthalten (siehe „Geprüft und sauber").

---

## Executive Summary

| Schweregrad | Anzahl |
|---|---|
| Kritisch | 3 |
| Hoch | 12 |
| Mittel | 20 |
| Niedrig | 19 |

**Die 3 gefährlichsten Funde:**

1. **BUG-001 — Ein NaN-Score vergiftet die semantische Suche; zwei NaN hängen den Request für immer.** Ein einziger Null-Vektor in der DB macht das Top-K-Ranking still zu „erste N Zeilen in Tabellenordnung"; ein zweiter Null-Vektor erzeugt eine Endlosschleife (`Math.BitIncrement(NaN)` = NaN) und pinnt den Thread.
2. **BUG-002 — FTS5-Index-Korruption bei jedem Re-Index.** `INSERT OR REPLACE` + externe-Content-FTS-Trigger ohne `PRAGMA recursive_triggers`: jedes Upsert eines existierenden Knotens hinterlässt einen Geister-FTS-Eintrag. Volltextsuche liefert im laufenden Betrieb zunehmend falsche/verschwundene Treffer — bei rowid-Wiederverwendung sogar den **falschen Knoten**.
3. **BUG-003 — Unbekannter Projektname fällt still auf das aktive Projekt zurück.** Tippfehler im `projectName`/Header → Antworten (und `record`-Schreibzugriffe!) landen im falschen Projekt-Graph. Im SaaS-Fall (Projekt zwischen Auth und Query gelöscht/umbenannt) ist das ein Cross-Tenant-Datenleck.

Ein wiederkehrendes Grundmuster: **`INSERT OR REPLACE` in `UpsertNodesAsync`** ist die gemeinsame Wurzel von BUG-002, BUG-007 und Teilen von BUG-016 — ein Umbau auf `ON CONFLICT(Id) DO UPDATE` behebt drei Fundgruppen mit einer Änderung.

---

## Kritisch

### BUG-001 — NaN vergiftet den Top-K-Heap der semantischen Suche; zweites NaN → Endlosschleife
- **Schweregrad:** Kritisch · **Status:** Bestätigt (Logik; Auslöser braucht einen Null-Norm-Vektor in der DB)
- **Ort:** [SqliteGraphStorageProvider.cs:390-404](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs) (`SearchSemanticAsync`)
- **Beschreibung:** `TensorPrimitives.CosineSimilarity` liefert **NaN** bei einem Null-Vektor (0/0); es gibt keinen NaN-Guard. `Comparer<double>` sortiert NaN unter alles → NaN wird `heap.Keys[0]` und ist nie evictbar (Eviction passiert nur bei Insert). Sobald der Heap voll ist, ist `score > NaN` für **jeden** echten Score `false` → alle weiteren Zeilen werden verworfen. Bei einem **zweiten** NaN: `while (heap.ContainsKey(key)) key = Math.BitIncrement(key)` — `BitIncrement(NaN)` bleibt NaN → Endlosschleife.
- **Auslöser:** korrupter Embedding-Blob oder ein Modell, das einen Null-Vektor liefert (z. B. für leeren/Whitespace-Text).
- **Auswirkung:** Semantik- und Hybrid-Suche liefern still Zufallsergebnisse (Tabellenordnung statt Ranking); im Hang-Fall Request-Thread für immer belegt → unter Last Threadpool-Starvation des ganzen Servers.
- **Reproduktion:** Node mit Embedding = `new float[768]` (alles 0) upserten, `search_semantic` mit `limit` klein genug, dass der Heap füllt; zweiten Null-Vektor-Node hinzufügen → Request kehrt nie zurück.
- **Fix:** direkt nach Zeile 390: `if (double.IsNaN(score)) continue;` — zusätzlich Null-Norm-Vektoren beim Schreiben ablehnen.

### BUG-002 — FTS5-Index-Korruption: `INSERT OR REPLACE` + externe-Content-FTS ohne `recursive_triggers`
- **Schweregrad:** Kritisch · **Status:** Bestätigt
- **Ort:** [SqliteGraphStorageProvider.cs:104](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs) (`UpsertNodesAsync`); Trigger in [SqliteSchema.cs:112-150](../src/Shonkor.Infrastructure/Storage/SqliteSchema.cs); `recursive_triggers` wird nirgends gesetzt (repo-weit geprüft)
- **Beschreibung:** Bei `INSERT OR REPLACE` löscht SQLite die kollidierende Zeile, **ohne** den `AFTER DELETE`-Trigger zu feuern (das passiert nur mit `PRAGMA recursive_triggers = ON`). Der `AFTER INSERT`-Trigger feuert für die neue Zeile (neue rowid). Ergebnis pro Re-Upsert eines existierenden Knotens: ein verwaister FTS-Eintrag (alte rowid, alter Inhalt) + ein neuer Eintrag.
- **Auslöser:** jeder inkrementelle Re-Index einer bereits indizierten Datei (`reindex_file`, Drift-Reconcile, erneuter Scan).
- **Auswirkung:** `NodesFts` ist eine external-content-Tabelle (`content=Nodes`) — Geister-Einträge lesen Spaltenwerte über die veraltete rowid. Wird die rowid für einen **anderen** Knoten wiederverwendet, matcht der ALTE Suchtext den FALSCHEN Knoten; sonst verschwinden Treffer still. Der Count-basierte Rebuild-Guard ([SqliteSchema.cs:157-162](../src/Shonkor.Infrastructure/Storage/SqliteSchema.cs)) repariert erst beim **nächsten Prozessstart** — ein langlaufender Server liefert die ganze Session über zunehmend falsche Volltextergebnisse.
- **Reproduktion:** Datei indizieren, editieren, `reindex_file`; dann `SELECT COUNT(*) FROM Nodes` vs. `SELECT COUNT(*) FROM NodesFts` vergleichen → FTS-Zähler ist höher.
- **Fix:** `INSERT OR REPLACE` durch `INSERT ... ON CONFLICT(Id) DO UPDATE SET ...` ersetzen (erhält rowid, feuert den UPDATE-Trigger). Alternativ/zusätzlich `PRAGMA recursive_triggers = ON` in `OpenConnectionAsync`. Der `ON CONFLICT`-Umbau behebt zugleich BUG-007.

### BUG-003 — Unbekannter Projektname fällt still auf das aktive Projekt zurück (Cross-Projekt-/Cross-Tenant-Leak)
- **Schweregrad:** Kritisch · **Status:** Bestätigt
- **Ort:** [ProjectManager.cs:309-323](../src/Shonkor.Infrastructure/Services/ProjectManager.cs) (`ResolveProject`, Zeile 320: `project ??= GetActiveProject();`); Konsument [McpToolContext.cs:75-81](../src/Shonkor.Infrastructure/Services/Mcp/McpToolContext.cs)
- **Beschreibung:** Wird ein Projektname explizit angefragt, aber nicht gefunden, gibt es keinen Fehler — es wird still das **aktive** Projekt verwendet. Das aktive Projekt ist eine global persistierte, über das Web-Dashboard änderbare Einstellung.
- **Auslöser:** Tippfehler in `projectName`/`X-Project-Name`; Projekt gelöscht/umbenannt während einer Session; Dashboard-Nutzer wechselt das aktive Projekt, während ein anderer Client MCP-Anfragen ohne Projektbindung stellt.
- **Auswirkung:** Antworten kommen unbemerkt aus dem **falschen Graph** (Halluzinationsquelle erster Ordnung für den konsumierenden Agenten); `record`-Schreibzugriffe verschmutzen fremde Projekt-Graphen. SaaS-Worst-Case: Tenant-gebundene Session, deren Projekt zwischen Auth und Query aus der Registry verschwindet, wird auf das aktive Projekt eines **anderen Tenants** umgeleitet → Datenleck.
- **Reproduktion:** `tools/call` mit `projectName="Gibtsnicht"` → Ergebnis kommt aus dem aktiven Projekt, ohne Warnung.
- **Fix:** Wenn ein Name explizit angefragt wurde (oder die Session tenant-locked ist) und die Auflösung fehlschlägt → JSON-RPC-Fehler, niemals Fallback.

---

## Hoch

### BUG-004 — Alle C#-Zitate um eine Zeile daneben (0-basierte StartLine als 1-basiert ausgegeben)
- **Schweregrad:** Hoch · **Status:** Bestätigt
- **Ort:** [RoslynAstParser.cs:128,163,198,234,329](../src/Shonkor.Core/Services/RoslynAstParser.cs) (`StartLinePosition.Line`, 0-basiert gespeichert); Ausgabe-Stellen drucken roh: u. a. [ReadTools.cs:47](../src/Shonkor.Infrastructure/Services/Mcp/Tools/ReadTools.cs), FindTools, AnalyzeTools, EditLoopTools, CLI. [CSharpDiagnostics.cs:90](../src/Shonkor.Infrastructure/Services/CSharpDiagnostics.cs) rechnet dagegen explizit `+ 1 // 1-based for humans/agents` — die intendierte Konvention ist also 1-basiert. Nur [McpToolContext.cs:109-123](../src/Shonkor.Infrastructure/Services/Mcp/McpToolContext.cs) (`TryReadSourceSlice`) behandelt die Werte korrekt als 0-basiert.
- **Auswirkung:** Jede `file:line`-Angabe, die das System für C#-Symbole ausgibt (`locate`, `find_usages`, `edit_plan`, `signature`, …), zeigt eine Zeile **über** die echte Deklaration — das ist die Kernwährung eines „präzisen" Graph-RAG.
- **Reproduktion:** `signature` für eine bekannte Klasse aufrufen und die ausgegebene Zeile mit der Datei vergleichen.
- **Fix:** Eine Konvention festlegen (empfohlen: 1-basiert speichern, `Line + 1` in den Parsern, `TryReadSourceSlice` auf `-1` umstellen), auf `GraphNode.StartLine` dokumentieren. Betrifft auch EndLine.

### BUG-005 — `INSERT OR REPLACE` löscht Embedding-Versionierung und Enrichment bei jedem Re-Upsert
- **Schweregrad:** Hoch · **Status:** Bestätigt
- **Ort:** [SqliteGraphStorageProvider.cs:104-105,147-149](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs) vs. [1509-1517](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs)
- **Beschreibung:** Die Spaltenliste des REPLACE enthält weder `EmbeddingDim` noch `EmbeddingModel` → beide werden bei jedem Upsert `NULL`. `MarkStaleEmbeddingsForReembedAsync` überspringt Zeilen mit `EmbeddingDim IS NULL` gezielt — überlebende Embeddings sind für den Stale-Detektor **unsichtbar**; ein dimensionsgleicher Modellwechsel mischt dann still Vektoren zweier Modelle (Cosine-Scores über Modellgrenzen sind bedeutungslos). Zusätzlich: `pNeedsAnalysis.Value = 1` ist hartkodiert (Zeile 148) und `Summary`/`Embedding` werden mit den (typischerweise leeren) Werten des eingehenden Knotens überschrieben → jeder Re-Index eines unveränderten Symbols wirft bezahltes LLM-Enrichment weg und stellt es neu in die Queue.
- **Verschärfung:** `POST /api/interactions/status` ([StatsEndpoints.cs:96-109](../src/Shonkor.Web/Endpoints/StatsEndpoints.cs)) lädt einen Knoten über `GetNodeByIdAsync` (der Mapper liest **kein** Embedding, [SqliteRowMapper.cs:51-63](../src/Shonkor.Infrastructure/Storage/SqliteRowMapper.cs)) und upsertet ihn zurück → das Embedding des Knotens wird zerstört; der Endpoint akzeptiert **beliebige** Node-IDs.
- **Fix:** `ON CONFLICT(Id) DO UPDATE`, das `Summary`/`Embedding`/`EmbeddingDim`/`EmbeddingModel` erhält, wenn der eingehende Knoten sie nicht liefert; `NeedsSemanticAnalysis = 1` nur bei geändertem `ContentHash`.

### BUG-006 — `set_project` ist über das HTTP-Relay ein stiller No-op, meldet aber Erfolg
- **Schweregrad:** Hoch · **Status:** Bestätigt
- **Ort:** [McpEndpoints.cs:68-70](../src/Shonkor.Web/Endpoints/McpEndpoints.cs) (neuer `McpRequestHandler` **pro POST**); [MetaTools.cs:117-121](../src/Shonkor.Infrastructure/Services/Mcp/Tools/MetaTools.cs) setzt `ctx.SessionProjectOverride` auf dem request-lokalen Kontext
- **Auswirkung:** Der Client glaubt, das Projekt gewechselt zu haben („Active project … is now 'X'"), aber die nächste Anfrage löst wieder über Header/aktives Projekt auf → Agent liest und **schreibt** (`record`) selbstbewusst in den falschen Graph. Kombiniert mit BUG-003 doppelt tückisch.
- **Fix:** Ohne persistente Session eine klare Fehlermeldung zurückgeben („über HTTP-Relay nicht unterstützt — `projectName` pro Aufruf übergeben") oder Sessions über einen `Mcp-Session-Id`-Header persistieren.

### BUG-007 — Stale-File-Cleanup löscht per Pfad-Präfix Daten fremder Verzeichnisse
- **Schweregrad:** Hoch · **Status:** Bestätigt
- **Ort:** [GraphIndexScanner.cs:190](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs) und [:379-380](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs)
- **Beschreibung:** `indexedFile.StartsWith(directoryPath, OrdinalIgnoreCase)` ohne Trailing-Separator-Guard: Ein Scan von `C:\Repo` klassifiziert indizierte Dateien unter `C:\Repo2\…` als „unter diesem Verzeichnis"; da sie keine Kandidaten sind, werden sie **aus dem Graph gelöscht**. Umgekehrt wird `directoryPath` nie mit `Path.GetFullPath` normalisiert (Kandidaten schon) — ein relativer/abweichend geformter Pfad deaktiviert das Cleanup still (Stale-Nodes überleben jeden Re-Index).
- **Auslöser:** eine DB mit mehr als einer Wurzel (Multi-Root, `reindex_file` auf Out-of-Root-Pfad, Namens-Präfix-Geschwister wie `Brain`/`Brainstorm`).
- **Fix:** `directoryPath` normalisieren + `Path.DirectorySeparatorChar` anhängen, dann vergleichen.

### BUG-008 — Plugin-Registry wird bei transientem Lesefehler komplett geleert
- **Schweregrad:** Hoch · **Status:** Bestätigt
- **Ort:** [PluginRegistry.cs:261-273](../src/Shonkor.Infrastructure/Services/PluginRegistry.cs) (`catch { return new List<InstalledPlugin>(); }`), Schreibpfad `InstallFromZip` (Zeile 133-134), nicht-atomares `Save()` (Zeile 278)
- **Beschreibung:** `Load()` verschluckt **alle** Exceptions und gibt eine leere Liste zurück. Ist `registry.json` gerade gesperrt (AV, Backup, zweite Instanz — `AssemblyPluginLoader` baut eine **eigene** Registry-Instanz mit eigenem Lock, [AssemblyPluginLoader.cs:71](../src/Shonkor.Infrastructure/Services/AssemblyPluginLoader.cs)), persistiert der nächste Schreibvorgang den leeren Zustand → **alle installierten Plugins sind aus der Registry gelöscht**.
- **Fix:** „Datei fehlt" von „Lesen fehlgeschlagen" unterscheiden (Letzteres → Operation abbrechen); atomar schreiben (Temp-Datei + `File.Replace`); eine gemeinsame Registry-Instanz bzw. prozessübergreifendes Mutex.

### BUG-009 — MCP-Proxy verschluckt Transport-Exceptions → Host hängt unbegrenzt
- **Schweregrad:** Hoch · **Status:** Bestätigt
- **Ort:** [McpProxyClient.cs:162-165](../src/Shonkor.CLI/McpProxyClient.cs) (`catch` schreibt nur nach stderr, keine JSON-RPC-Antwort); Default-`HttpClient.Timeout` 100 s trifft langsame Tools (`audit`, `generate_capsule`)
- **Auswirkung:** Der MCP-Host wartet ewig auf die Antwort zur Request-ID — typische Clients blockieren damit die gesamte Konversation. Der Nicht-2xx-Zweig (Zeilen 139-159) macht es korrekt vor.
- **Fix:** Im `catch` die `id` der Zeile parsen und eine `-32603`-Fehlerantwort auf stdout schreiben; Timeout konfigurierbar erhöhen.

### BUG-010 — 64-Hex-Token wird als „bereits gehasht" fehlklassifiziert: Klartext-Speicherung + permanenter Auth-Lockout
- **Schweregrad:** Hoch · **Status:** Bestätigt
- **Ort:** [TokenHasher.cs:20-25](../src/Shonkor.Infrastructure/Services/TokenHasher.cs) (`LooksHashed`/`EnsureHashed`); Aufrufer [ProjectManager.cs:195-201](../src/Shonkor.Infrastructure/Services/ProjectManager.cs)
- **Beschreibung:** Ein **vom Aufrufer gelieferter** API-Key, der zufällig genau 64 Hex-Zeichen lang ist (die häufigste Form eines 32-Byte-Tokens), wird als Digest durchgereicht und im Klartext in `projects.json` gespeichert. `Verify` hasht das präsentierte Token → `SHA256(token) != token` → Authentifizierung schlägt für diesen Key **immer** fehl, ohne erklärenden Fehler. Nebenbefund: `LooksHashed` akzeptiert Großbuchstaben-Hex, `Hash` emittiert Kleinbuchstaben → ein groß geschriebener Digest matcht in `Verify` nie (kein `ToLowerInvariant` vor dem Vergleich).
- **Fix:** Nicht raten — Hashes selbstbeschreibend speichern (`sha256:<hex>`) oder Migration einmalig/versioniert statt Shape-Sniffing; `storedHash` normalisieren.

### BUG-011 — Streaming-Antworten werden nach 2 Minuten hart abgebrochen, ohne Unvollständigkeits-Marker
- **Schweregrad:** Hoch · **Status:** Bestätigt
- **Ort:** [OllamaSemanticAnalyzer.cs:34](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs) (`_httpClient.Timeout = TimeSpan.FromMinutes(2)`), Stream-Loop Zeilen 269-322
- **Beschreibung:** `HttpClient.Timeout` gilt auch für das Lesen des Response-Bodys — auch mit `ResponseHeadersRead`. Eine RAG-Generierung > 120 s (großes Kapsel-Kontextfenster + 7B-Modell auf CPU ist dafür genug) wird mitten im Stream abgebrochen; der Abbruch kommt als `TaskCanceledException` aus `ReadLineAsync`, der Graceful-Truncation-Marker (`[Antwort unvollständig]`, Zeilen 317-322) feuert nur bei `line == null`. In `SearchEndpoints.cs:186-189` sind Teil-Tokens bereits an den Client geflusht → Antwort endet mitten im Satz, ohne Marker.
- **Fix:** `Timeout = Timeout.InfiniteTimeSpan` auf dem Typed Client, Connect-/First-Byte-Timeout über `SocketsHttpHandler.ConnectTimeout`/CTS; Read-Loop so umbauen, dass auch der Exception-Pfad den Marker emittiert.

### BUG-012 — JS-/GraphQL-Parser erzeugen kleingeschriebene Node-IDs → ganze Kantenfamilien hängen ins Leere
- **Schweregrad:** Hoch · **Status:** Bestätigt
- **Ort:** [JavaScriptParser.cs:48,119](../src/Shonkor.Core/Services/JavaScriptParser.cs); [GraphQLParser.cs:48](../src/Shonkor.Core/Services/GraphQLParser.cs) (`filePath.ToLowerInvariant()`); Scanner-File-Node nutzt Original-Case ([GraphIndexScanner.cs:169](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs)); `Nodes.Id` ist case-sensitiv
- **Auswirkung:** Auf typischen Windows-Pfaden (`C:\Projects\…`) zeigen **alle** `IMPORTS`- und `DEFINED_IN`-Kanten auf IDs, die kein Knoten hat → die JS/GraphQL-Teilgraphen sind strukturell tot. Auf komplett kleingeschriebenen Pfaden (Linux-Konvention) kollidiert die Komponenten-ID stattdessen mit der File-Node-ID; wer gewinnt, ist wegen `ConcurrentBag`-Reihenfolge nichtdeterministisch — verliert der File-Node, ist sein `ContentHash` weg und die Datei wird bei **jedem** Scan neu indiziert.
- **Verwandt (Mittel):** relative Imports werden ohne Extension-/Index-Auflösung übernommen (`./Button` ≠ `Button.tsx`, [JavaScriptParser.cs:133-142](../src/Shonkor.Core/Services/JavaScriptParser.cs)), und Esprima kann kein TypeScript parsen — bei den meisten `.ts/.tsx`-Dateien werden Imports still verworfen (Zeilen 88-99). Effektiv verbindet heute **keine** JS-Import-Kante reale Knoten. (Bekannte Richtung: JS/TS-Plugin-Familie mit Node-Sidecar ersetzt diesen Parser.)
- **Fix:** `ToLowerInvariant()` entfernen; dieselbe kanonische Pfadform wie der Scanner verwenden (gemeinsamer Helper).

### BUG-013 — PHP-Parser: `metadata.php` erzeugt Phantom-`EXTENDS`-Kanten aus *jedem* `'k' => 'v'`-Paar
- **Schweregrad:** Hoch · **Status:** Bestätigt
- **Ort:** [PhpModuleParser.cs:29](../src/Shonkor.Core/Services/PhpModuleParser.cs) (`['"](\w+)['"]\s*=>\s*['"]([^'"]+)['"]` auf die **ganze Datei** angewendet, Zeile 148)
- **Auswirkung:** Ein normales OXID-`metadata.php` (`'id'`, `'title'`, `'author'`, `'templates'`, `'settings'`, …) erzeugt Dutzende Bogus-Kanten wie `My Module EXTENDS title` — Modul-Abhängigkeits- und Impact-Abfragen sind mit Müll geflutet.
- **Verwandt (Mittel):** `^\s*class` verfehlt `abstract class`/`final class` und namespaced Basisklassen (`\w+` stoppt am `\`) — genau die Basisklassen-Schicht der OXID-Modulketten fehlt ([PhpModuleParser.cs:21](../src/Shonkor.Core/Services/PhpModuleParser.cs)).
- **Fix:** zuerst den `'extend' => [ … ]`-Block isolieren (Klammer-Balancierung), Pair-Pattern nur darin anwenden; Klassen-Regex: `^\s*(?:final\s+|abstract\s+)*class\s+(\w+)\s+extends\s+([\w\\]+)`.

### BUG-014 — C#-Typ-ID-Kollisionen: gleichnamige Typen in einer Datei werden zu einem Knoten verschmolzen
- **Schweregrad:** Hoch · **Status:** Bestätigt
- **Ort:** [CsharpNodeId.cs:33](../src/Shonkor.Core/Services/CsharpNodeId.cs) (`ForType = {filePath}::{typeName}` — ohne Namespace, Generik-Arity, Nesting-Kette); Member-IDs kollidieren transitiv ([RoslynAstParser.cs:152](../src/Shonkor.Core/Services/RoslynAstParser.cs) nutzt nur den innersten Typnamen)
- **Auslöser:** `namespace A { class C {} } namespace B { class C {} }` in einer Datei; `class Foo {}` + `class Foo<T> {}`; zwei Klassen mit gleichnamiger nested `class Builder`.
- **Auswirkung:** Zwei Entitäten fusionieren zu einem Knoten (letzter Upsert gewinnt) — falsche Call-Hierarchien, falsche Impact-/Rename-Ergebnisse, Inhalt/Zeilen des einen Typs überschreiben den anderen. Die `CsharpNodeId`-Remarks dokumentieren nur die Partial-Type-Ambiguität; diese Kollision ist undokumentiert.
- **Fix:** Namespace + Nesting-Kette + Generik-Arity in die ID aufnehmen, gespiegelt in `RoslynSemantics.ToNodeId`; **`SchemeVersion` bumpen**.

### BUG-015 — Drift-Reconcile konvergiert nie (zwei Ursachen): ausgeschlossene Dateien werden wiederbelebt; neue Binär-/Übergröße-Dateien loopen
- **Schweregrad:** Hoch · **Status:** Bestätigt
- **Ort:** (a) [GraphIndexScanner.cs:395-408](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs): `ReconcileDriftAsync` schickt `drift.Deleted` durch `ScanFileAsync`, das Exclude-Patterns nicht kennt — eine vorhandene, parsebare, aber **ausgeschlossene** Datei wird re-indiziert statt entfernt → nächster Drift-Report meldet sie wieder `Deleted`, der Hintergrund-Reconciler indiziert sie **jeden Zyklus** neu; explizit ausgeschlossener Inhalt bleibt dauerhaft im Graph. (b) [GraphIndexScanner.cs:345-361](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs): der Größen-/Binär-Check gilt nur für den `changed`-Zweig — eine neue >5-MB- oder Binär-Datei mit parsebarer Extension landet in `New`, wird von `ScanFileAsync` verworfen, taucht beim nächsten Pass wieder als `New` auf. `DriftReport.IsClean` wird in beiden Fällen nie `true`.
- **Fix:** (a) `drift.Deleted` direkt über `DeleteByFilePathAsync` + `MaintainReferencersAsync` abwickeln oder Exclude-Patterns in `ScanFileAsync` durchreichen; (b) Größen-/Binär-Filter auch auf den `added`-Zweig anwenden.

---

## Mittel

### BUG-016 — FTS-LIKE-Fallback liefert eine *ungeordnete* Liste, die RRF als Ranking interpretiert
- **Status:** Bestätigt · **Ort:** [SqliteGraphStorageProvider.cs:288-318](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs); Konsumenten `search_hybrid` ([FindTools.cs:248/267](../src/Shonkor.Infrastructure/Services/Mcp/Tools/FindTools.cs)), Web-Suche
- Wirft FTS5 einen Syntaxfehler (Doppelpunkte, Slashes, `<`/`>` — also genau Identifier-Queries wie `Foo::Bar`, `IFoo<T>`), greift ein LIKE-Fallback **ohne `ORDER BY`** mit Score 1.0. RRF gewichtet Listenposition als Rang → die Keyword-Hälfte der Fusion ist Tabellenzufall; irrelevante Knoten können den echten Treffer verdrängen. **Fix:** deterministisches Relevanz-Proxy (`ORDER BY CASE WHEN Name LIKE @q THEN 0 ELSE 1 END, Name`) oder FTS-Query quoten statt fallbacken.

### BUG-017 — Kein Similarity-Floor, Scores werden dem Client verschwiegen (Halluzinationspfad)
- **Status:** Bestätigt · **Ort:** `SearchSemanticAsync` (kein Schwellwert); [FindTools.cs:197-202, 273-278](../src/Shonkor.Infrastructure/Services/Mcp/Tools/FindTools.cs) (Ausgabezeile ohne Score)
- Eine Query zu einem im Korpus nicht existenten Konzept liefert trotzdem `limit` autoritativ aussehende Treffer. **Fix:** konfigurierbarer Min-Cosine-Floor (~0.3–0.4 für nomic) und/oder Score in der Ausgabezeile. Ergänzend im RAG-Pfad: bei `contextNodes.Count == 0` gar nicht erst generieren, sondern Abstention zurückgeben (heute verlässt sich der Pfad allein auf Prompt-Compliance des kleinen Modells).

### BUG-018 — CLI-Embed-Pass liest andere Config-Quelle als die Query-Seite → stiller Totalausfall der Semantik-Suche
- **Status:** Bestätigt · **Ort:** [Program.cs:323-325](../src/Shonkor.CLI/Program.cs) (`AddEnvironmentVariables()` **only**) vs. Web/appsettings auf der Query-Seite
- Ist `EmbeddingService:OllamaModel`/`:DocumentPrefix` in appsettings gesetzt, indiziert der CLI-Pass unter anderem Modell/Prefix als die Query. Anderes Modell → Dimensions-Guard überspringt **jeden** Knoten → Semantik leer, Hybrid degradiert still zu FTS-only, ohne Diagnose. **Fix:** CLI lädt dieselbe appsettings-Datei.

### BUG-019 — Enrichment-Starvation: dauerhaft fehlschlagende Nodes blockieren die Queue für immer
- **Status:** Bestätigt · **Ort:** [SqliteGraphStorageProvider.cs:1389-1393](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs) (`LIMIT @BatchSize`, kein `ORDER BY`, kein Retry-Zähler); [SemanticEnrichmentService.cs:267-272](../src/Shonkor.Web/Services/SemanticEnrichmentService.cs) (Flag bleibt 1)
- Schlagen ≥ BatchSize (16) Nodes deterministisch fehl, selektiert jeder Zyklus dieselbe Batch — kein weiterer Knoten wird je embedded. **Fix:** Retry-Zähler / Last-Attempt-Spalte / `ORDER BY RANDOM()`.

### BUG-020 — MCP-Fehlerformat: Tool-Fehler als Protokollfehler, Parse-Fehler ohne Antwort, Notifications → HTTP 400
- **Status:** Bestätigt · **Ort:** [McpRequestHandler.cs:104-109,151-195](../src/Shonkor.Infrastructure/Services/McpRequestHandler.cs); [McpEndpoints.cs:82-86](../src/Shonkor.Web/Endpoints/McpEndpoints.cs)
- (a) Tool-Ausführungsfehler kommen als JSON-RPC `-32603` statt als `result` mit `isError: true` (MCP-Spec) — viele Hosts zeigen das als opaken Client-Fehler statt es dem Modell zur Selbstkorrektur zu geben; `SendToolResponse` kann gar kein `isError`. (b) Ungültiges JSON → `null` statt `-32700` mit `id: null`; fehlende `method` → falscher Code `-32601` statt `-32600`. (c) Das Relay macht aus der korrekten `null`-Antwort auf Notifications ein HTTP 400 — **jede Standard-Session beginnt mit einem Fehler** (`notifications/initialized`). **Fix:** `isError`-Support; korrekte Fehlercodes; Notifications → 202/leer.

### BUG-021 — Keine Cancellation in der Tool-Pipeline; serieller stdio-Loop → ein hängendes Tool blockiert den Server komplett
- **Status:** Bestätigt · **Ort:** [IMcpTool.cs:30](../src/Shonkor.Infrastructure/Services/Mcp/IMcpTool.cs) (kein `CancellationToken`); [McpRequestHandler.cs:71-97](../src/Shonkor.Infrastructure/Services/McpRequestHandler.cs) (awaitet vor dem nächsten Read — `notifications/cancelled` kann nicht mal gelesen werden); Relay reicht `RequestAborted` nicht durch
- Ein hängender Backend-Call (z. B. `GenerateEmbeddingAsync` in [FindTools.cs:185](../src/Shonkor.Infrastructure/Services/Mcp/Tools/FindTools.cs), ohne Token/Timeout) blockiert den stdio-Server bis zum Prozess-Kill; abgebrochene HTTP-Requests rechnen Full-Graph-Loads (`audit`) zu Ende. **Fix:** Token durch die Pipeline; stdio-Loop nebenläufig lesen.

### BUG-022 — Retry-Schleifen verschlucken Cancellation, retryen Nicht-Transientes und verwerfen Ollama-Fehlerbodies
- **Status:** Bestätigt · **Ort:** [OllamaSemanticAnalyzer.cs:128-137,227-236](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs); [OllamaEmbeddingService.cs:87-96](../src/Shonkor.Infrastructure/Services/OllamaEmbeddingService.cs); `EnsureSuccessStatusCode` ohne Body-Log an 4 Stellen
- `catch (Exception)` fängt `OperationCanceledException` des Aufrufers (Cancellation wird als „retrybarer" Fehler geloggt); ein 404 „model not found" wird 3× mit vollem 2-Minuten-Timeout wiederholt (bis ~6 min/Node, im Enrichment über Hunderte Nodes: Stunden); Ollamas JSON-Fehlerbody (`{"error":"model 'x' not found…"}`) wird nie gelesen — Operatoren sehen nur den Statuscode. **Fix:** `catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }` zuerst; nur Transientes retryen; Fehlerbody in die Meldung.

### BUG-023 — Node-Analyse: Truncation mitten in Zeile/Surrogatpaar + fehlende Determinismus-Optionen → Summary- und Vektor-Churn
- **Status:** Bestätigt · **Ort:** [OllamaSemanticAnalyzer.cs:40-44](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs) (`[..8000]` statt des vorhandenen `TruncateAtLineBoundary`, Zeile 145) und [:63-69](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs) (kein `temperature`/`seed`, während die RAG-Pfade explizit `temperature = 0` setzen)
- Das Modell bekommt kein Signal, dass der Code gekürzt wurde (beschreibt Fragment als Ganzes; ein gesplittetes Surrogatpaar erzeugt einen invaliden String); Re-Analyse eines unveränderten Knotens liefert eine andere Summary → da die Summary in den Embedding-Text eingeht (`EmbeddingTextBuilder`), verschiebt jeder Enrichment-Lauf den Vektorraum — instabile Suchergebnisse über Re-Indexe hinweg. **Fix:** `TruncateAtLineBoundary` verwenden; `options = new { temperature = 0 }`.

### BUG-024 — Kapsel-„Budget" begrenzt die Kapsel nicht
- **Status:** Bestätigt · **Ort:** [ContextCapsuleSynthesizer.cs:261-310](../src/Shonkor.Core/Services/ContextCapsuleSynthesizer.cs)
- Seeds dekrementieren `remaining` nie; Header/Signatur/Summary-Zeilen und das Mermaid-Diagramm werden für **jeden** Knoten emittiert und nie gezählt; `MaxNodes` default `null`. Eine 2-Hop-Hub-Expansion überschreitet `MaxContentChars` unbegrenzt, während Trailer/Omission-Note fälschlich „Budget erreicht / ~N Tokens" behaupten. Worst Case: die „token-optimierte" Kapsel sprengt den Kontext des konsumierenden LLM. **Fix:** Header/Diagramm/Seed-Bodies gegen ein (zweites) Budget zählen; `MaxNodes` sinnvoll defaulten.

### BUG-025 — Semantik-Linker stempelt heuristische Fallback-Kanten als `Extracted`; Provenance wird bei Re-Scan nie korrigiert
- **Status:** Bestätigt · **Ort:** [SemanticCsharpLinker.cs:120-176](../src/Shonkor.Infrastructure/Services/SemanticCsharpLinker.cs) (Provenance nicht gesetzt → Default `Extracted`, auch für die namensbasierten Kanten aus `ResolveUnresolvedByNameAsync`, die eine Kante zu *jeder* gleichnamigen Definition erzeugen); [SqliteGraphStorageProvider.cs:183](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs) (`INSERT OR IGNORE` aktualisiert Provenance nie)
- Das korrumpiert genau das Vertrauens-Signal, das `references`/`find_usages` per `provenance`-Filter anbieten (CrossTechLinker taggt dieselben Fälle korrekt `Inferred`/`Ambiguous`). **Fix:** Provenance pro Kante mitführen (`defs.Count > 1 ? Ambiguous : Inferred` für den Fallback); Upsert auf `ON CONFLICT … DO UPDATE SET Provenance = …`.

### BUG-026 — Ungechunktes `IN (…)` im Subgraph-/Incident-Edge-Fetch → „too many SQL variables" bei großen Traversalen
- **Status:** Bestätigt · **Ort:** [SqliteGraphStorageProvider.cs:1346-1372](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs) (`GetEdgesBetweenNodesAsync`), [:542-552](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs) (`GetIncidentEdgesAsync`) — während dieselbe Datei `MaxSqlParameters = 900` deklariert und überall sonst chunked
- `find_path`/`get_subgraph` mit hohem `maxHops` auf großem Repo → `SqliteException` statt Ergebnis. **Fix:** ID-Set chunken (einseitig `IN` + In-Memory-Filter über `HashSet`).

### BUG-027 — Use-after-dispose auf gecachten Storage-Providern unter nebenläufigen Requests
- **Status:** Bestätigt (Race-Fenster) · **Ort:** [ProjectManager.cs:259-262, 377-384](../src/Shonkor.Infrastructure/Services/ProjectManager.cs)
- `RefreshStorageProvider`/`DeleteProject` disposen den geteilten Provider, während In-Flight-Queries anderer Clients dieselbe Referenz nutzen → `ObjectDisposedException` als `-32603`. Zusätzlich können zwei parallele Erstaufrufe zwei Provider gegen dieselbe SQLite-Datei initialisieren (GetOrAdd-Race, Zeilen 285-298). **Fix:** Referenzzählung/Drain vor Dispose.

### BUG-028 — `SqliteGraphStorageProvider.Dispose` leert den Connection-Pool nicht → DB-Datei bleibt unter Windows gesperrt
- **Status:** Bestätigt · **Ort:** [SqliteGraphStorageProvider.cs:58-64, 1231-1234](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs)
- Nach `DeleteProject`/`RefreshStorageProvider` halten gepoolte Connections `.db`/`-wal`/`-shm` offen — Löschen/Verschieben schlägt mit Sharing Violation fehl. **Fix:** `SqliteConnection.ClearPool(...)` in `Dispose()`.

### BUG-029 — `SemanticCompilationCache.Invalidate` raced mit Lesern: stale Compilation entkommt und wird zurückgeschrieben
- **Status:** Bestätigt (Fenster) · **Ort:** [SemanticCompilationCache.cs:121-129](../src/Shonkor.Infrastructure/Services/SemanticCompilationCache.cs) (gateless `entry.Built = false; entry.Compilation = null;` ohne volatile/Interlocked); `ApplyEditsAsync` schreibt die vor dem Invalidate gecapturte Compilation in Zeile 111 zurück. Außerdem keine mtime-/Hash-Validierung: extern geänderte Dateien (git checkout) werden bis zum nächsten expliziten Invalidate stale serviert (Verdacht bzgl. Praxishäufigkeit). **Fix:** Entry atomar aus dem Dictionary entfernen (`TryRemove`) statt Felder zu nullen.

### BUG-030 — In-Memory-Shared-Cache: `busy_timeout` deckt `SQLITE_LOCKED` nicht ab
- **Status:** Verdacht · **Ort:** [SqliteGraphStorageProvider.cs:40-55, 76-81](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs)
- Shared-Cache-Connections konkurrieren über Table-Locks, die sofort mit `SQLITE_LOCKED` fehlschlagen (busy_timeout retryt nur `SQLITE_BUSY`) — paralleler Write+Read auf demselben In-Memory-Provider kann „database table is locked" werfen. Zur Bestätigung: nebenläufiger Test gegen den In-Memory-Modus. **Fix:** `SemaphoreSlim(1,1)` für den In-Memory-Fall.

### BUG-031 — `Console.Input/OutputEncoding`-Setter können den stdio-Server beim Start killen (headless spawn)
- **Status:** Verdacht (umgebungsabhängig) · **Ort:** [McpRequestHandler.cs:66-67](../src/Shonkor.Infrastructure/Services/McpRequestHandler.cs); [McpProxyClient.cs:79-80](../src/Shonkor.CLI/McpProxyClient.cs)
- Auf Windows werfen die Setter `IOException`, wenn kein Konsolen-Handle existiert — genau so spawnen MCP-Hosts stdio-Server. Zur Bestätigung: Start mit `CREATE_NO_WINDOW`/detached. **Fix:** Setter guarden; Ausgabe über UTF-8-`StreamWriter` auf `OpenStandardOutput()`.

### BUG-032 — Prompt-Injection: abgerufener Inhalt fließt ungekapselt zwischen Server-Anweisungstext
- **Status:** Verdacht (by construction) · **Ort:** [McpToolContext.cs:189-190](../src/Shonkor.Infrastructure/Services/Mcp/McpToolContext.cs) (Node-Summaries inline auf Kantenzeilen), [McpToolHelpers.cs:105-117](../src/Shonkor.Infrastructure/Services/Mcp/McpToolHelpers.cs) (Snippets), `get_open_threads` druckt gespeicherte Namen/Status roh; zudem interpoliert [SurprisingConnectionExplainer.cs:20-25](../src/Shonkor.Core/Services/SurprisingConnectionExplainer.cs) Node-*Namen* in die als vertrauenswürdig gerahmte NUTZERFRAGE-Sektion des RAG-Prompts
- Indizierter Inhalt (Markdown-Doku, Kommentare, per `record` von anderen Clients geschriebene Nodes) steht auf denselben Zeilen wie echte Tool-Guidance („Suggested starting points" mit ready-to-run Tool-Calls) — ein präpariertes Dokument kann dem konsumierenden Agenten Anweisungen unterschieben. Eine mehrzeilige Summary bricht heute schon das Zeilenformat. **Fix:** Abgerufene `Summary`/`Content`-Texte in klar gelabelte Fences („untrusted indexed content"), Newlines aus Summaries strippen; Namen in die Datensektion des Prompts.

### BUG-033 — UTF-16-Quelldateien gelten als „binär": stiller Skip + Graph-Limbo ohne Staleness-Signal
- **Status:** Bestätigt · **Ort:** [GraphIndexScanner.cs:694-711](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs) (NUL-Byte-Scan; UTF-16 ist ~50 % NUL) mit Skip vor Hash-Check und `filesToClear` (Zeilen 127-138)
- PowerShell 5.1 `Out-File` schreibt default UTF-16 LE. Eine früher indizierte Datei, die UTF-16 wird (oder >5 MB wächst), behält ihre alten Nodes **für immer** — ohne Drift-Signal (`DetectDriftAsync` skippt sie ebenfalls). **Fix:** BOM-Erkennung (FF FE/FE FF) vor dem NUL-Scan; beim Skip explizit entscheiden: Nodes löschen oder als stale markieren.

### BUG-034 — Record-Primärkonstruktoren: Semantik-Linker und Parser erzeugen inkonsistente Ctor-IDs → hängende CALLS-Kanten
- **Status:** Bestätigt (Logik-Mismatch) · **Ort:** [RoslynSemantics.cs:73-98](../src/Shonkor.Core/Services/RoslynSemantics.cs) vs. [RoslynAstParser.cs:220, 279-293](../src/Shonkor.Core/Services/RoslynAstParser.cs)
- Der Primär-Ctor eines Records ist ein Constructor-Symbol ohne `ConstructorDeclarationSyntax` — `ToNodeId` erzeugt eine ID, zu der kein Knoten existiert; bei gleicher Arity divergiert zusätzlich der Overload-Span-Zähler beider Seiten, sodass sogar die ID des expliziten Ctors abweicht. **Fix:** Primär-Ctors in `OverloadSpan`/`ToNodeId` gesondert behandeln, Regel im Parser spiegeln.

### BUG-035 — Markdown-Parser: Header in Codefences werden Phantom-Sections; Link-Ziele behalten `#fragment`/`"title"`, Nicht-HTTP-Schemes werden zu Pfaden
- **Status:** Bestätigt · **Ort:** [MarkdownHierarchyParser.cs:19, 26, 67, 111-121](../src/Shonkor.Core/Services/MarkdownHierarchyParser.cs)
- Jede `# Kommentar`-Zeile in einem ```bash-Block wird ein `MarkdownSection`-Knoten (und verschiebt via Laufindex alle nachfolgenden Section-IDs); `[x](./file.md#install)` → Pfad endet auf `#install`; `mailto:`/`//cdn…` passieren den `(?!https?://|#)`-Guard (zudem case-sensitiv) und werden per `Path.GetFullPath` zu Unsinn kombiniert → hängende REFERENCES-Kanten. **Fix:** Fenced-Blocks vorab strippen; Fragment/Titel aus Gruppe 2 entfernen; jedes `^[a-z][a-z0-9+.-]*:`-Scheme und `//` ausschließen.

### BUG-036 — GraphQL-Parser: Regex ohne Wortgrenzen/Kommentar-Bewusstsein erzeugt Phantom-Operationen; `...on` (ohne Leerzeichen) wird verfehlt
- **Status:** Bestätigt · **Ort:** [GraphQLParser.cs:25, 28, 34, 141-151](../src/Shonkor.Core/Services/GraphQLParser.cs)
- `query\s+(\w+)` mit `IgnoreCase` matcht in `# query GetUser`-Kommentaren und als Substring (`subquery Foo` → Phantom-Query `Foo`); `\.\.\.\s+on\s+` verlangt Whitespace → das sehr übliche `...on Promo` liefert leere `referencedTemplates`; zudem bekommt jeder Knoten der Datei die **Union** aller Inline-Fragment-Ziele. **Fix:** `(?m)^\s*query\b`, Kommentare strippen, `\.\.\.\s*on\s+`, Templates pro Operation zuordnen.

### BUG-037 — ProjectManager: Reload/Save-Races und nicht-atomare Writes an `projects.json`
- **Status:** Bestätigt (Fenster) · **Ort:** [ProjectManager.cs:105-127, 455-499, 502-625](../src/Shonkor.Infrastructure/Services/ProjectManager.cs)
- `LoadProjects` parst außerhalb des Locks und swappt dann `_projects/_users` wholesale — ein paralleles `AddProject` kann transient verschwinden (inkl. 401 für frisch angelegte User); `SaveProjects` aktualisiert `_lastLoadTime` nicht (jeder Save erzwingt Re-Load und weitet das Fenster); Writes sind nicht-atomar — Crash mid-write korrumpiert die Datei, und `LoadProjects` registriert bei leerem In-Memory-Zustand dann still den Workspace als Default-Projekt (Registry faktisch verworfen). Zudem fehlt dem Präfix-Check in `SaveProjectConfig` (Zeile 472) der Separator-Guard. **Fix:** Temp-Datei + `File.Replace`; `_lastLoadTime` stempeln; Reload-Entscheidung+Swap unter einem Lock; `project` im Lock re-resolven.

### BUG-038 — AssemblyPluginLoader: ALC-Leak bei Teilfehler; Tamper-Check deckt nur die Entry-Assembly
- **Status:** Bestätigt · **Ort:** [AssemblyPluginLoader.cs:96-141, 177](../src/Shonkor.Infrastructure/Services/AssemblyPluginLoader.cs)
- Wirft `GetTypes()`/`CreateInstance` nach Kontext-Erzeugung, wird der collectible `AssemblyLoadContext` nie unloaded (Leak; bereits instanziierte Parser bleiben gebunden). Nur `EntryAssemblySha256` wird verifiziert — Dependency-DLLs im Plugin-Ordner lädt der `AssemblyDependencyResolver` **ohne** Hash-Check, entgegen der dokumentierten Garantie. **Fix:** per-Plugin try/finally mit `context.Unload()`; Hashes für alle DLLs des Pakets.

### BUG-039 — `SurprisingConnections` meldet strukturell enthaltene Paare als „überraschend"
- **Status:** Verdacht (Logik klar, Dominanz-Effekt braucht Datenprüfung) · **Ort:** [GraphAnalytics.cs:205-211](../src/Shonkor.Core/Services/GraphAnalytics.cs)
- Mit Default `includeStructural = false` werden `CONTAINS`-Kanten aus dem Linked-Set **ausgeschlossen** — File-Knoten und die enthaltene Klasse gelten als „nicht verbunden", ihre Embeddings sind aber nahezu identisch → triviale Eltern/Kind-Paare können die Top-N fluten. Die Flag-Semantik ist für diese Methode invertiert: eine existierende strukturelle Kante *ist* eine Verbindung. **Fix:** strukturelle Kanten immer ins Linked-Set aufnehmen.

---

## Niedrig

| ID | Fund | Status | Ort |
|---|---|---|---|
| BUG-040 | RRF-Tie-Break hängt an `Dictionary`-Enumerationsreihenfolge — Grenzfälle bei Position `maxResults` nichtdeterministisch, entgegen der dokumentierten Determinismus-Garantie. Fix: `.ThenBy(kv => kv.Key, StringComparer.Ordinal)` | Bestätigt | [HybridFusion.cs:40-42](../src/Shonkor.Core/Services/HybridFusion.cs) |
| BUG-041 | Korrupter Embedding-Blob (`Length % 4 != 0`) kann `BlockCopy` werfen → gesamte Semantik-Suche schlägt fehl statt Zeile zu skippen; `GetNodesWithEmbeddingsAsync` behandelt denselben Fall abweichend (trunkiert) | Bestätigt (braucht korrupte Daten) | [SqliteGraphStorageProvider.cs:384-388](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs) |
| BUG-042 | `limit=0` → interner `ArgumentOutOfRange` als `-32603`; `limit` nahe `int.MaxValue` → Overflow (`limit * 2`, `maxResults * 4`) → negative Kapazität | Bestätigt | [FindTools.cs:44,126,244](../src/Shonkor.Infrastructure/Services/Mcp/Tools/FindTools.cs) |
| BUG-043 | `hops`/`maxHops` ungeklemmt in `get_subgraph`, `generate_capsule`, `find_path` und `/api/rag/query` (dort auch: CancellationToken nicht an Storage-Calls durchgereicht) — beliebig teure Traversalen durch einen Client | Bestätigt | [ReadTools.cs:221,315](../src/Shonkor.Infrastructure/Services/Mcp/Tools/ReadTools.cs), [AnalyzeTools.cs:473](../src/Shonkor.Infrastructure/Services/Mcp/Tools/AnalyzeTools.cs), [GraphRagEndpoints.cs:40](../src/Shonkor.Web/Endpoints/GraphRagEndpoints.cs) |
| BUG-044 | `GetContentHashesAsync` nutzt `OrdinalIgnoreCase`-Dictionary auf Node-IDs, Rest des Providers ist `Ordinal` — Case-Kollision verfälscht den Change-Detektor | Bestätigt | [SqliteGraphStorageProvider.cs:849](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs) |
| BUG-045 | `TryExecuteAsync` verschluckt **alle** `SqliteException`s bei Migrationen (gedacht nur für „duplicate column") — Lock-/IO-Fehler hinterlassen fehlende Spalten mit verwirrendem Folgefehler | Bestätigt | [SqliteSchema.cs:182-186](../src/Shonkor.Infrastructure/Storage/SqliteSchema.cs) |
| BUG-046 | `NodesFts` hängt an der impliziten rowid einer TEXT-PK-Tabelle — ein zukünftiges `VACUUM` remappt still alle FTS-Einträge auf falsche Knoten (Counts bleiben gleich, Drift-Guard blind) | Verdacht (heute kein VACUUM im Code) | [SqliteSchema.cs:112-120](../src/Shonkor.Infrastructure/Storage/SqliteSchema.cs) |
| BUG-047 | Verwaiste `HelixModule`-Knoten akkumulieren: kein `FilePath` → kein Delete-Pfad greift je | Bestätigt | [CrossTechLinker.cs:161-204](../src/Shonkor.Infrastructure/Services/CrossTechLinker.cs) |
| BUG-048 | Pfad-Casing-Drift (Windows): Hash-Map ist `OrdinalIgnoreCase`, SQL-`FilePath`-Vergleiche byte-sensitiv → bei wechselnder Root-Schreibweise Duplikate pro Symbol, die der Stale-Sweep nicht entfernt | Verdacht | [GraphIndexScanner.cs:145](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs) + Provider-`FilePath`-Queries |
| BUG-049 | Mermaid-Escaping falsch: `\"` ist kein gültiges Mermaid-Escape (korrekt: `#quot;`), Edge-Labels (`\|`) gar nicht escaped → Diagramm-Parse-Fehler (vgl. Commit b76a581, derselbe Bug-Typ in Doku) | Bestätigt | [ContextCapsuleSynthesizer.cs:112-116,143-144](../src/Shonkor.Core/Services/ContextCapsuleSynthesizer.cs) |
| BUG-050 | RAG: 200-Antwort ohne `response`-Feld liefert den Fallback-Satz „Es konnte keine Antwort generiert werden." als normale Antwort (kein Throw/Log); `SurprisingConnectionExplainer` persistiert das als `[INFERRED]`-Erklärung | Bestätigt | [OllamaSemanticAnalyzer.cs:225](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs) |
| BUG-051 | Leerer/Whitespace-Input → 0-dim-Embedding statt Fehler: Semantik-Suche matcht still nichts (ununterscheidbar von „keine Treffer"); toter Code `return Array.Empty<float>()` in Zeile 99 | Bestätigt | [OllamaEmbeddingService.cs:47-50,99](../src/Shonkor.Infrastructure/Services/OllamaEmbeddingService.cs) |
| BUG-052 | `Timeout`-Mutation auf injiziertem HttpClient im Konstruktor — mit heutiger Typed-Client-Registrierung safe, aber derselbe Footgun, den der danebenstehende Kommentar für `BaseAddress` explizit vermeidet | Verdacht (latent) | [OllamaSemanticAnalyzer.cs:31-34](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs), [OllamaEmbeddingService.cs:36-39](../src/Shonkor.Infrastructure/Services/OllamaEmbeddingService.cs) |
| BUG-053 | Injection-Scanner-Regexes ohne matchTimeout/NonBacktracking über attacker-kontrollierten Repo-Inhalt — pathologische Whitespace-Läufe können die Indizierung stallen | Verdacht | [SuspiciousContentPostProcessor.cs:27-33](../src/Shonkor.Infrastructure/Services/SuspiciousContentPostProcessor.cs) |
| BUG-054 | MCP: `initialize` echot jede Client-`protocolVersion` zurück (statt nur unterstützte); `ex.Message`/absolute Serverpfade erreichen Remote-Clients (`get_diagnostics`, `-32603`-Texte); `-k/--key` auf der Kommandozeile ist in Prozesslisten sichtbar; Proxy schreibt jeden 2xx-Body unvalidiert auf stdout (HTML einer Captive-Portal-Seite korrumpiert das Framing) | Bestätigt | [McpRequestHandler.cs:133-136,161,194](../src/Shonkor.Infrastructure/Services/McpRequestHandler.cs), [MetaTools.cs:172-175](../src/Shonkor.Infrastructure/Services/Mcp/Tools/MetaTools.cs), [McpProxyClient.cs:33-37,126-133](../src/Shonkor.CLI/McpProxyClient.cs) |
| BUG-055 | Enum-/Methoden-/Property-/Ctor-Knoten setzen nie `EndLine` (→ `get_source`-Fallback liest pauschal 40 Zeilen); Markdown-Sections tragen gar keine Zeilennummern | Bestätigt | [RoslynAstParser.cs:121-133,156-170,192-204,227-240](../src/Shonkor.Core/Services/RoslynAstParser.cs), [MarkdownHierarchyParser.cs:76-87](../src/Shonkor.Core/Services/MarkdownHierarchyParser.cs) |
| BUG-056 | Parser-Exception mid-file: `filesToClear.Add` passiert **vor** dem Parsen — bei transientem Parserfehler werden die alten (validen) Knoten der Datei gelöscht und nichts nachgelegt, bis die Datei sich erneut ändert | Bestätigt | [GraphIndexScanner.cs:151,179-182](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs) |
| BUG-057 | Scan puffert alle Nodes/Edges des ganzen Repos in-memory vor dem ersten Upsert (Erst-Index sehr großer Repos: hunderte MB+); `IndexResult`-Metriken zählen Skips als „scanned" und Upserts als „created" | Bestätigt | [GraphIndexScanner.cs:110-122,163-165,209-216](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs) |
| BUG-058 | Nicht-UTF-8-Legacy-Encodings (ISO-8859-1, ältere OXID-Shops) werden mit U+FFFD ersetzt — Namen/FTS verschmutzt, Zitate degradiert | Verdacht | [GraphIndexScanner.cs:140](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs) |
| BUG-059 | `IsLikelyInterface`-Heuristik (I+Großbuchstabe) macht aus Basisklassen wie `IOManager` `IMPLEMENTS` statt `EXTENDS`; Partial-Type-Referenzen werden an eine willkürliche Datei gepinnt (`DeclaringSyntaxReferences.FirstOrDefault`) | Verdacht/Bestätigt | [RoslynAstParser.cs:476-477](../src/Shonkor.Core/Services/RoslynAstParser.cs), [RoslynSemantics.cs:67](../src/Shonkor.Core/Services/RoslynSemantics.cs) |

Ferner dokumentiert, aber nicht als neuer Defekt gezählt: Partial-Type-Overloads gleicher Arity erzeugen hängende Semantik-Kanten (in den `CsharpNodeId`-Remarks bereits als bekannte Restambiguität beschrieben); `AddProject` akzeptiert beliebige Dateisystempfade (Read-anything-Primitive, falls die Projekt-API je über Admin hinaus exponiert wird — [ProjectManager.cs:173-211](../src/Shonkor.Infrastructure/Services/ProjectManager.cs)); Benchmarks embedden Queries über die kind-lose Überladung (Document-Prefix) und messen damit nicht die Produktion ([RetrievalBenchmark.cs:68](../src/Shonkor.Bench/RetrievalBenchmark.cs)).

---

## Geprüft und sauber

- **RRF-Formel** (`HybridFusion`): `1/(k0 + rank + 1)` mit 0-basiertem Rang ≡ Standard; Gewichte symmetrisch. BM25-Sortierrichtung korrekt (negativ = besser, `Math.Abs` nur kosmetisch).
- **Cosine-Similarity**: an `TensorPrimitives` delegiert; Dimensions-Guard verhindert Cross-Modell-Dim-Mixing (abseits BUG-005/018).
- **Batch-Embedding-Zuordnung**: existiert nicht — ein Embedding pro Node, ID im selben Closure; kein N↔M-Vertauschungsrisiko.
- **Embedding-Truncation auf dem Index-Pfad**: beide Index-Pfade (Web-Worker, CLI) laufen durch `EmbeddingTextBuilder` (Body head+tail-bounded auf 1500 Zeichen, Header überlebt immer) — ein Agent-Verdacht „Input wird nie gekürzt" wurde hierdurch **widerlegt**.
- **Graph-Traversalen**: `GraphPathFinder` (BFS, Visited-Set, Richtung pro Hop korrekt), `references`/`call_hierarchy`/`related_tests` (Visited-Sets, Caps), Brandes-Centrality, Louvain/BFS-Communities — alle korrekt und terminierend.
- **Stale-Embedding-Invalidierung bei Re-Ingest** (geänderte Datei → Clear + `NeedsSemanticAnalysis=1`) und **Kanten-Kaskade** bei `DeleteByFilePathsAsync` (beide Richtungen, eine Transaktion).
- **SQL-Injection**: alle Nutzereingaben parametrisiert; interpolierte Fragmente sind generierte Parameternamen bzw. ein typisierter `int`.
- **Kein `.Result`/`.Wait()`/Fire-and-forget** in den geprüften Pfaden; DB-Zugriff durchgängig async mit `ConfigureAwait(false)`; Reader/Commands konsequent `await using`.
- **stdio-Framing**: seriell → keine interleaved Writes (Kehrseite: BUG-021); Notification-Erkennung über `ContainsKey("id")` korrekt.
- **Tenant-Lock des Relays**: `lockToContextProject` ignoriert per-Tool-`projectName` korrekt; `reindex_file`/Parser werden tenant-locked korrekt vorenthalten.
- **MockSemanticAnalyzer**, **StorageBackedGraphView**, **CSharpDiagnostics** (1-basierte Ausgabe!), **AmbiguousCSharpTypePostProcessor**: keine Defekte.

## Nicht geprüft / blinde Flecken

- **CMS-Plugins** (`Shonkor.Plugin.Kentico/Optimizely/Sitecore`) und **`Shonkor.Eval`/`Shonkor.Benchmarks`**: nicht gelesen (außer den zwei zitierten Bench-Stellen).
- **Web-Endpoints außerhalb von MCP/GraphRAG/Search/Stats** (Browse, Admin, Insights, Settings, Webhook, Index) und `ApiKeyMiddleware` im Detail: nur punktuell gequert.
- **Laufzeitverhalten**: keine Tests/Repros ausgeführt; alle „Bestätigt"-Markierungen sind Code-Belege, keine Laufzeitbeweise. Insbesondere BUG-001 (NaN-Erzeugbarkeit durch das konkrete Embedding-Backend), BUG-030 (SQLITE_LOCKED), BUG-031 (headless spawn) und BUG-039 (Dominanz-Effekt) wären mit je einem kleinen Test/Repro endgültig zu bestätigen.
- **Ollama-Verhalten** (Fehlerbodies, num_ctx-Defaults, Null-Vektor-Fälle): aus Doku/Erfahrung angenommen, nicht gegen eine laufende Instanz verifiziert.
- **Docker/k8s/Deploy-Skripte**: außerhalb der Betrachtung.

## Empfohlene Fix-Reihenfolge

1. **BUG-001** (Einzeiler, verhindert Hang + stilles Falsch-Ranking).
2. **`ON CONFLICT(Id) DO UPDATE` statt `INSERT OR REPLACE`** — behebt BUG-002 + BUG-005 (und den Enrichment-Churn) in einer Änderung; zusammen mit dem `/api/interactions/status`-Fix.
3. **BUG-003 + BUG-006** (Projektauflösung: kein stiller Fallback; `set_project` über Relay ehrlich machen).
4. **BUG-004** (Zeilenkonvention) und **BUG-014** (ID-Schema, `SchemeVersion`-Bump) — beide erfordern einen Re-Index, sinnvoll zu bündeln.
5. **BUG-007/008/009** (Datenverlust-/Hang-Klasse), danach die Mittel-Liste.
