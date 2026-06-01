# Shonkor 🧠 - arc42 Architekturdokumentation

Dieses Verzeichnis enthält die vollständige Architekturdokumentation des Shonkor-Projekts, strukturiert nach dem international bewährten **arc42**-Standard.

Die Dokumentation richtet sich an Softwarearchitekten, Entwickler und Auditoren, die das innere Design, die logischen Bausteine, die Laufzeitaspekte und die zugrundeliegenden Konzepte des Systems verstehen oder erweitern möchten.

---

## 📚 Kapitelverzeichnis

### 📄 [Kapitel 1: Einführung & Ziele](file:///c:/Projects/Brain/docs/developer/arc42/01_introduction_and_goals.md)
* Aufgabenstellung und Kernanforderungen (Präzise GraphRAG, 100% Offline-Portabilität).
* Qualitätsziele (Präzision vor Wahrscheinlichkeit, Performance, Token-Pruning).
* Stakeholder-Übersicht.

### 📄 [Kapitel 2: Randbedingungen](file:///c:/Projects/Brain/docs/developer/arc42/02_architecture_constraints.md)
* Technische Randbedingungen (.NET 10, SQLite, 0-externe Abhängigkeiten).
* Organisatorische Randbedingungen (GitFlow, arc42-Standard).
* Konventionen (.NET Code Guidelines, Clean Code).

### 📄 [Kapitel 3: Kontextabgrenzung](file:///c:/Projects/Brain/docs/developer/arc42/03_system_scope_and_context.md)
* Fachlicher Kontext (Datenflüsse zwischen Entwickler, Workspace-Quellcode und LLMs).
* Technischer Kontext (CLI, Web-Dashboard, Dateisystem-Crawler).

### 📄 [Kapitel 4: Lösungsstrategie](file:///c:/Projects/Brain/docs/developer/arc42/04_solution_strategy.md)
* Grundlegende Entscheidungen und Lösungsansätze.
* Warum SQLite FTS5 + Recursive CTEs?
* Multi-Language AST-Parsing-Konzept.

### 📄 [Kapitel 5: Bausteinsicht](file:///c:/Projects/Brain/docs/developer/arc42/05_building_block_view.md)
* Statische Struktur des Systems (Ebene 1 und Ebene 2).
* Aufteilung in Core, Infrastructure, CLI und Web.
* Schnittstellendesign.

### 📄 [Kapitel 6: Laufzeitsicht](file:///c:/Projects/Brain/docs/developer/arc42/06_runtime_view.md)
* Dynamisches Verhalten des Systems.
* Sequenzdiagramm für die inkrementelle Indexierung.
* Sequenzdiagramm für die rekursive Subgraph-Extraktion und Kapsel-Synthese.

### 📄 [Kapitel 8: Konzepte](file:///c:/Projects/Brain/docs/developer/arc42/08_concepts.md)
* AST Graph-Metamodell.
* FTS5 + Rekursives CTE (UNION ALL + MIN-Depth) und Batch-Edge-Loading.
* Typ-Referenzen (`REFERENCES_TYPE`) & Cross-Technology-Linking.
* Nebenläufigkeit (Connection-per-Operation), Sicherheitsmodell.
* MCP-Projektauflösung & Token-Effizienz, Capsule-Pruning.

---

## ⚖️ Qualitätsanspruch

Die Einhaltung dieser Dokumentation wird kontinuierlich durch Pre-Commit-Prüfungen sichergestellt. Jede strukturelle Code-Änderung erfordert zwingend ein Audit und eine Anpassung des entsprechenden arc42-Kapitels.
