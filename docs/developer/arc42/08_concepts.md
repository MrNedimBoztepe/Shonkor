# arc42 Kapitel 8: Konzepte 💡

Dieses Kapitel beschreibt die grundlegenden Konzepte, Muster und Implementierungsdetails, die das Fundament von Shonkor bilden.

---

## 8.1 Das relationale Graph-Metamodell

Um Flexibilität für verschiedene Programmiersprachen und Konfigurationsformate zu bieten, wird ein verallgemeinertes relationales Metamodell in SQLite verwendet.

```
+---------------------------------------------------------------------------------+
|                                     NODES                                       |
+---------------------------------------------------------------------------------+
| Id (PK) | Type | Name | Content | Metadata (JSON) | FilePath | Start/End | Hash |
+---------------------------------------------------------------------------------+
                                       |
                                       | (1)
                                       |
                                       | (N)
+---------------------------------------------------------------------------------+
|                                     EDGES                                       |
+---------------------------------------------------------------------------------+
| SourceId (FK) | TargetId (FK) | RelationType                                    |
+---------------------------------------------------------------------------------+
```

### 1. Extensible Properties via Key-Value Dictionary
Um Signaturen schlank und gleichzeitig erweiterbar zu halten (z. B. für Sitecore Templates oder Optimizely Properties), speichert die Klasse `GraphNode` alle anwendungsspezifischen Details in einem `Dictionary<string, string> Properties`. 
Das SQLite-Repository bildet bekannte Spalten (wie `Content` oder `FilePath`) auf dedizierte Spalten ab – alle anderen dynamischen Schlüssel-Wert-Paare werden vollautomatisch als serialisiertes JSON in der Spalte `Metadata` abgelegt. Dies vereint die Vorteile relationaler Datenhaltung mit schemaloser Flexibilität.

---

## 8.2 SQLite FTS5 & Rekursives CTE (Das Performance-Geheimnis)

Die Graph-Traversierung in relationalen Datenbanken wird typischerweise durch Joins ausgebremst. Shonkor nutzt hochoptimiertes SQLite-Standard-SQL, um dieses Problem vollständig zu eliminieren.

### 1. FTS5 Keyword-Suche (Seeds finden)
Die FTS5-Virtual-Table `NodesFts` wird über Trigger vollautomatisch mit der `Nodes`-Tabelle synchronisiert:
```sql
SELECT n.Id, n.Type, n.Name, bm25(NodesFts) AS Score
FROM NodesFts fts
JOIN Nodes n ON fts.Id = n.Id
WHERE NodesFts MATCH @query
ORDER BY Score
LIMIT @limit;
```
Dies liefert extrem schnelle, gewichtete Einstiegspunkte für jede Suchanfrage.

Die Suche lädt die zugehörigen Kanten aller Treffer in **einer** Batch-Abfrage (statt einer Query pro Treffer) und vermeidet so ein N+1-Problem bei großen Ergebnismengen.

### 2. Rekursives CTE (Graph-Traversierung)
Ein rekursiver Common Table Expression (CTE)-Join expandiert ausgehend von den Seed-Knoten über N Hops in beide Richtungen (bidirektional):
```sql
WITH RECURSIVE Subgraph(Id, Depth) AS (
    SELECT Id, 0 FROM Nodes WHERE Id IN (@seeds)
    UNION ALL
    SELECT CASE WHEN e.SourceId = s.Id THEN e.TargetId ELSE e.SourceId END, s.Depth + 1
    FROM Edges e
    JOIN Subgraph s ON (e.SourceId = s.Id OR e.TargetId = s.Id)
    WHERE s.Depth < @hops
)
SELECT DISTINCT n.*
FROM Nodes n
JOIN (SELECT Id, MIN(Depth) AS Depth FROM Subgraph GROUP BY Id) s ON n.Id = s.Id;
```
Dieses Statement führt eine N-Hop-Traversierung in einer einzigen SQL-Transaktion aus. Durch `UNION ALL` mit anschließender `MIN(Depth)`-Aggregation wird jeder Knoten von seinem **kürzesten** Pfad aus betrachtet – die Hop-Begrenzung ist damit deterministisch und unabhängig von der Kanten-Reihenfolge.

---

## 8.3 Typ-Referenzen & Cross-Technology-Linking (Post-Scan)

Reine Syntaxbäume liefern Containment (`CONTAINS`) und Vererbung (`IMPLEMENTS`/`EXTENDS`), aber keine Verwendungs-Beziehungen. Damit „wer verwendet Typ X?" als Graph-Traversierung beantwortbar ist, läuft nach dem Scan ein **Linker** (`CrossTechLinker`):

1. Der `RoslynAstParser` sammelt je Typ die Namen referenzierter Typen (Felder, Properties, Parameter, Rückgabetypen, `new`-Ausdrücke, Generics, Basistypen) und legt sie als Property `referencedTypes` ab.
2. Der Linker löst diese Namen gegen die Definition-Knoten (Class/Interface/Record/Struct/Enum) auf und erzeugt `REFERENCES_TYPE`-Kanten **Verwender → Definition**.

Analog werden Cross-Technology-Kanten (`BINDS_TO`, `CONTROLLER_OF`, `QUERIES_TEMPLATE`) und Helix-Modul-Zugehörigkeiten (`BELONGS_TO_MODULE`) aufgelöst. Dieses Muster benötigt keine zusätzliche Storage-API – es liest Knoten-Properties und schreibt aufgelöste Kanten zurück.

---

## 8.4 Nebenläufigkeit & Verbindungsverwaltung

Da das Web-Dashboard Anfragen parallel bedient, öffnet `SqliteGraphStorageProvider` **pro Operation eine eigene Connection** aus dem Microsoft.Data.Sqlite-Pool (statt eine geteilte, nicht thread-sichere Connection). Datei-DBs nutzen WAL und einen `busy_timeout`; In-Memory-DBs werden über eine eindeutig benannte Shared-Cache-DB mit Keep-Alive-Connection am Leben gehalten. Der `ProjectManager` cached Provider pro Projekt via `Lazy<>` (genau eine Initialisierung) und verhindert über `TryBeginScan`/`EndScan` parallele Scans desselben Projekts.

---

## 8.5 Sicherheitsmodell

Shonkor ist primär lokal, unterstützt aber einen Multi-Tenant-/SaaS-Modus:
* **API-Keys** werden konstantzeitig verglichen; der Loopback-Bypass ist nur in `Development` aktiv (sonst kollabiert die Auth hinter einem Proxy).
* **Plugins** sind ein RCE-Vektor: Laufzeit-Kompilierung ist Opt-in (`Security:EnablePlugins`) und lädt in einen entladbaren `AssemblyLoadContext`.
* **Webhooks** verifizieren `X-Hub-Signature-256` (HMAC) und sanitisieren Repository-Namen gegen Path-Traversal.
* Fehlerantworten geben **keine** internen Details/Pfade preis; Details landen ausschließlich im Server-Log.

---

## 8.6 MCP-Projektauflösung & Token-Effizienz

* **Projekt aus dem Arbeitsverzeichnis**: Der MCP-Server leitet das aktive Projekt aus seinem Working Directory ab (`FindProjectByPath`), nicht aus dem web-mutierbaren `ActiveProjectName`. Dadurch sind Dashboard und KI-Kontext entkoppelt.
* **Lean-Ausgaben**: `locate` und `search_graph` liefern standardmäßig kompakten Text (`name -> datei:zeile`) statt JSON; `get_subgraph` einen kompakten `NODES`/`EDGES`-Block. `verbose: true` schaltet auf volles JSON. Das reduziert den Token-Verbrauch flacher Lookups um ~90 %.

---

## 8.7 Token-Optimierung & Pruning (Capsule)

Für ein LLM ist das Verhältnis von Signal zu Rauschen im Kontext entscheidend.
* **Größenbegrenzung**: File-Knoten speichern Inhalt gedeckelt (Hash weiterhin über den vollen Inhalt); die Kapsel akzeptiert ein `maxChars`-Budget und kürzt an Sektionsgrenzen.
* **Filterung**: File-Knoten ohne Textinhalt werden bei der Kapselgenerierung übersprungen.
* **Formatierte Syntax-Blöcke**: Knoten-Inhalte werden in Markdown-Codeblöcke mit sprachspezifischen Highlight-Bezeichnern verpackt.
* **Struktur vor Code**: Ein initiales Mermaid.js-Diagramm vermittelt die Beziehungen, bevor das LLM die Quelltext-Blöcke analysiert.
