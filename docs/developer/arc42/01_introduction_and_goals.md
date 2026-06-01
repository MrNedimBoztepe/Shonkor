# arc42 Kapitel 1: Einführung & Ziele 🎯

Dieses Kapitel beschreibt die wesentlichen Anforderungen und Qualitätsziele für Shonkor.

---

## 1.1 Aufgabenstellung

Heutige RAG-Systeme (Retrieval-Augmented Generation) verlassen sich meist auf probabilistische Vektordatenbanken und einfache Zeichen-Slices. Dies führt in der Praxis bei Codebasen zu gravierenden Problemen:
1. **Halluzinationen**: Das LLM sieht Codeabschnitte ohne deren Kontext (Imports, Klassenstrukturen, vererbte Schnittstellen).
2. **Ungenauigkeit**: Relevante Beziehungen (z. B. welche Methode eine andere aufruft) werden durch die Ähnlichkeitssuche oft übersehen.
3. **Hoher Token-Verbrauch**: Es werden irrelevante Codeblöcke geladen, was die Kosten steigert und die Context-Fenster verstopft.

**Shonkor** löst diese Probleme durch einen **deterministischen Knowledge Graph (GraphRAG)**:
* Quellcode und Dokumentation werden präzise in Knoten (Klassen, Methoden, Dateien, Abschnitte) und Kanten (Abhängigkeiten, Aufrufe, Vererbung) zerlegt.
* Suchanfragen nutzen ein hybrides Modell aus FTS5-Volltextsuche zur Keimzellen-Findung (Seeds) und rekursiven SQL-Graph-Traversierungen (CTEs) zur Kontext-Ermittlung.
* Das Ergebnis wird in einer token-optimierten Kapsel (inklusive einer visuellen Darstellung als Mermaid.js) an das LLM übergeben.

---

## 1.2 Qualitätsziele

| Priorität | Qualitätsziel | Beschreibung | Messbare Zielgröße |
| :---: | :--- | :--- | :--- |
| **1** | **100% Präzision** | Keine Annahmen. Das LLM erhält exakt die physischen Deklarationen und Beziehungen, die im Graphen existieren. | 0% Falschmeldungen über nicht existierende Methoden/Klassen. |
| **2** | **Portabilität & Autarkie** | Das System läuft zu 100% offline, ohne externe API-Abhängigkeiten und nutzt ein lokales SQLite-Backend. | 0 KB Netzwerktraffic; Datenbankgröße < 1 MB für Standard-Repsoitories. |
| **3** | **Performanz** | Indexierung lokaler Repositories in Sekunden. Abfragen und CTE-Traversierung in Millisekunden. | Indexierung: > 15 Dateien/Sek. <br>Suche & Traversierung: < 10ms. |
| **4** | **Token-Effizienz** | Durch das Pruning (Abschneiden des Graphen nach N Hops) wird das Rauschen minimiert und teure LLM-Tokens werden geschont. | > 85% Token-Einsparung im Vergleich zur Bereitstellung der gesamten Codebasis. |

---

## 1.3 Stakeholder

* **Entwickler / Endanwender**: Möchten präzise Antworten von ihren KI-Assistenten bei komplexen Code-Refactorings.
* **Unternehmen / Security Officers**: Möchten sicherstellen, dass sensible Code-Strukturen nicht an externe RAG-Server übertragen werden (vollständige Offline-Sicherheit).
* **Systemarchitekten**: Möchten die strukturellen Abhängigkeiten ihrer Systeme visuell im Dashboard analysieren.

---

## 1.4 Reale Projektergebnisse & Benchmarks (Stand: Mai 2026)

Die folgenden Messdaten wurden direkt in der Produktivumgebung bei der Analyse einer realen Code- und Dokumentationsbasis erhoben und belegen das Erreichen unserer ehrgeizigen Qualitätsziele:

* **Indexierungs-Performance**:
  * **Scangeschwindigkeit**: **34 Quellcodedateien** (.NET C#, JS, Markdown) wurden in **nur 1,80 Sekunden** vollständig eingelesen, lexikalisch geparst und in den Graphen überführt (~19 Dateien/Sekunde).
  * **Graph-Dichte**: Aus den 34 Dateien wurden **241 semantische Knoten** (Klassen, Methoden, Interfaces, Markdown-Sektionen) und **229 präzise logische Kanten** (Abhängigkeiten, Parent-Child-Beziehungen) extrahiert.
* **Abfrage-Geschwindigkeit (FTS5 & CTE-Traversierung)**:
  * **Semantische Suche**: BM25-gewichtete Keyword-Suchen über den gesamten Quelltext der Datenbank beanspruchen **unter 5 Millisekunden**.
  * **N-Hop Graph-Traversierung**: Das Extrahieren eines 2-Hop-Subgraphen inklusive der physikalischen Code-Inhalte benötigt **unter 10 Millisekunden**.
* **Token-Einsparung (Pruning & Capsule-Synthese)**:
  * Bei einer Abfrage nach der Kernklasse `SqliteGraphStorageProvider` generiert Shonkor eine vollständige, prompt-fertige Kontextkapsel (inklusive Mermaid-Diagramm und Code der relevanten Methoden) mit einer Größe von **nur 4.592 Zeichen (~1.148 Tokens)**.
  * Im Vergleich zur Übertragung der gesamten Codebasis entspricht dies einer **Token-Reduktion von ca. 92%**. Die API-Kosten für LLMs sinken somit um denselben Faktor bei gleichzeitig signifikant höherer Antwortqualität.
* **Ressourceneffizienz**:
  * Die gesamte indexierte SQLite-Datenbank (`shonkor.db`) ist lediglich **352 KB** groß und lässt sich problemlos versionskontrolliert im Git-Repository ablegen.

