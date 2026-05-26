# arc42 Kapitel 8: Konzepte 💡

Dieses Kapitel beschreibt die grundlegenden Konzepte, Muster und Implementierungsdetails, die das Fundament von LLMBrain bilden.

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

Die Graph-Traversierung in relationalen Datenbanken wird typischerweise durch Joins ausgebremst. LLMBrain nutzt hochoptimiertes SQLite-Standard-SQL, um dieses Problem vollständig zu eliminieren.

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

### 2. Rekursives CTE (Graph-Traversierung)
Ein rekursiver Common Table Expression (CTE)-Join expandiert ausgehend von den Seed-Knoten über N Hops in beide Richtungen (bidirektional):
```sql
WITH RECURSIVE Subgraph(Id, Depth) AS (
    SELECT Id, 0 FROM Nodes WHERE Id IN (@seeds)
    UNION
    SELECT CASE WHEN e.SourceId = s.Id THEN e.TargetId ELSE e.SourceId END, s.Depth + 1
    FROM Edges e
    JOIN Subgraph s ON (e.SourceId = s.Id OR e.TargetId = s.Id)
    WHERE s.Depth < @hops
)
SELECT DISTINCT n.* FROM Nodes n JOIN Subgraph s ON n.Id = s.Id;
```
Dieses Statement führt eine N-Hop Graph-Traversierung in einer einzigen, atomaren SQL-Transaktion aus. Dies eliminiert Hunderte von einzelnen Datenbankabfragen und läuft selbst bei Zehntausenden Code-Knoten in weniger als 5 Millisekunden.

---

## 8.3 Token-Optimierung & Pruning

Für ein LLM ist das Verhältnis von Signal zu Rauschen im Kontext entscheidend.
* **Filterung**: File-Knoten, die keinen Textinhalt haben, werden bei der Kapselgenerierung übersprungen, um redundanten Pfad-Kontext zu vermeiden.
* **Formatierte Syntax-Blöcke**: Knoten-Inhalte werden in saubere Markdown-Codeblöcke mit sprachspezifischen Syntax-Highlight-Bezeichnern (z. B. `csharp`, `javascript`) verpackt.
* **Struktur vor Code**: Durch die Bereitstellung eines initialen Mermaid.js Diagramms versteht das LLM die Beziehungen, noch bevor es die Quelltext-Blöcke analysiert.
