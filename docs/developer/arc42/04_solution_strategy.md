# arc42 Kapitel 4: Lösungsstrategie 💡

Dieses Kapitel beschreibt die grundlegenden Architekturentscheidungen und technologischen Ansätze des Shonkor-Systems.

---

## 4.1 Kernkonzept: Deterministischer Wissensgraph

Probabilistisches RAG (z. B. Vektordatenbanken) leidet unter dem Verlust logischer Beziehungen. Um 100% präzisen Code-Kontext zu liefern, implementiert Shonkor einen exakten gerichteten Wissensgraphen.

1. **Abstraktion von Code als Graph**:
   * **Knoten (Nodes)** stellen Entitäten dar: Dateien, Klassen, Methoden, Konfigurationen und Markdown-Abschnitte. Jeder Knoten speichert seinen Typ, Namen und Quelltext (`Content`).
   * **Kanten (Edges)** stellen Beziehungen dar, u. a. `CONTAINS` (Datei/Typ enthält Member), `IMPLEMENTS`/`EXTENDS` (Vererbung), `REFERENCES_TYPE` (Typ verwendet anderen Typ – Basis der Impact-Analyse), `IMPORTS` (Modulabhängigkeit) sowie Cross-Technology-Kanten (`BINDS_TO`, `CONTROLLER_OF`, `QUERIES_TEMPLATE`, `BELONGS_TO_MODULE`).
2. **Technologiewahl: SQLite**:
   * Anstelle komplexer Graph-Datenbanken (Neo4j, Memgraph), die eine schwere Serverinstallation erfordern, setzt Shonkor auf **SQLite**.
   * Dies ermöglicht eine 0-Dependency, portable Datei (`shonkor.db`), die direkt in das Git-Repository eingecheckt werden kann.
   * **FTS5 (Full-Text Search)**: Ermöglicht extrem schnelle, BM25-gewertete Schlagwortsuche über den Code-Content, um die Einstiegspunkte ("Seeds") für Suchanfragen zu finden.
   * **Recursive Common Table Expressions (CTEs)**: Ermöglicht die Traversierung des Graphen über beliebig viele Hops ("N-Hops") direkt auf SQL-Ebene. Dies löst das typische Performance-Problem relationaler Graphen in Millisekunden.

---

## 4.2 Multi-Language Parser-Strategie

Das System zerlegt unterschiedliche Sprachen mithilfe spezialisierter Parser-Klassen, die alle die gemeinsame Schnittstelle `IFileParser` implementieren:

* **Roslyn AST-Parser (C#)**: Nutzt den offiziellen Microsoft C#-Compiler, um Quelltext in abstrakte Syntaxbäume (AST) zu zerlegen. Dies ermöglicht die exakte Extraktion von Klassendeklarationen, Vererbung und Methodensignaturen. Erkennt Optimizely `[ContentType]` Attribute.
* **JavaScript/TypeScript Parser**: Nutzt die performante **Esprima**-Bibliothek, um JS/TS-Dateien syntaktisch zu analysieren. Extrahiert Import/Export-Beziehungen zur Abbildung von Modulabhängigkeiten.
* **Smarty- & PHP-Parser**: Regex-basierter Parser zur Extraktion von Erweiterungen (`extends`) in OXID eShop-Modulen sowie Smarty-Template-Blöcken (`[{block name="..."}]`).
* **SCS-YAML Parser (Sitecore)**: Deserialisiert SCS-YAML-Dateien mittels **YamlDotNet**, um Sitecore-Templates und Layouts im Graphen abzubilden.
* **Markdown Hierarchy Parser**: Analysiert Markdown-Strukturen anhand von Überschriften (`#`, `##`, `###`) und extrahiert relative Dateilinks, um Dokumente als Begleitmaterial semantisch mit dem Code zu verknüpfen.
