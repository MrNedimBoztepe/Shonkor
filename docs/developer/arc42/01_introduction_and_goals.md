# arc42 Kapitel 1: Einführung & Ziele 🎯

Dieses Kapitel beschreibt die wesentlichen Anforderungen und Qualitätsziele für LLMBrain.

---

## 1.1 Aufgabenstellung

Heutige RAG-Systeme (Retrieval-Augmented Generation) verlassen sich meist auf probabilistische Vektordatenbanken und einfache Zeichen-Slices. Dies führt in der Praxis bei Codebasen zu gravierenden Problemen:
1. **Halluzinationen**: Das LLM sieht Codeabschnitte ohne deren Kontext (Imports, Klassenstrukturen, vererbte Schnittstellen).
2. **Ungenauigkeit**: Relevante Beziehungen (z. B. welche Methode eine andere aufruft) werden durch die Ähnlichkeitssuche oft übersehen.
3. **Hoher Token-Verbrauch**: Es werden irrelevante Codeblöcke geladen, was die Kosten steigert und die Context-Fenster verstopft.

**LLMBrain** löst diese Probleme durch einen **deterministischen Knowledge Graph (GraphRAG)**:
* Quellcode und Dokumentation werden präzise in Knoten (Klassen, Methoden, Dateien, Abschnitte) und Kanten (Abhängigkeiten, Aufrufe, Vererbung) zerlegt.
* Suchanfragen nutzen ein hybrides Modell aus FTS5-Volltextsuche zur Keimzellen-Findung (Seeds) und rekursiven SQL-Graph-Traversierungen (CTEs) zur Kontext-Ermittlung.
* Das Ergebnis wird in einer token-optimierten Kapsel (inklusive einer visuellen Darstellung als Mermaid.js) an das LLM übergeben.

---

## 1.2 Qualitätsziele

| Priorität | Qualitätsziel | Beschreibung |
| :---: | :--- | :--- |
| **1** | **100% Präzision** | Keine Annahmen. Das LLM erhält exakt die physischen Deklarationen und Beziehungen, die im Graphen existieren. |
| **2** | **Portabilität & Autarkie** | Das System läuft zu 100% offline, ohne externe API-Abhängigkeiten und nutzt ein lokales SQLite-Backend. |
| **3** | **Performanz** | Indexierung lokaler Repositories in Sekunden. Abfragen und CTE-Traversierung in Millisekunden. |
| **4** | **Token-Effizienz** | Durch das Pruning (Abschneiden des Graphen nach N Hops) wird das Rauschen minimiert und teure LLM-Tokens werden geschont. |

---

## 1.3 Stakeholder

* **Entwickler / Endanwender**: Möchten präzise Antworten von ihren KI-Assistenten bei komplexen Code-Refactorings.
* **Unternehmen / Security Officers**: Möchten sicherstellen, dass sensible Code-Strukturen nicht an externe RAG-Server übertragen werden (vollständige Offline-Sicherheit).
* **Systemarchitekten**: Möchten die strukturellen Abhängigkeiten ihrer Systeme visuell im Dashboard analysieren.
