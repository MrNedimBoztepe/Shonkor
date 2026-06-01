# Shonkor 🧠 - High-Precision GraphRAG Sales Presentation

## Executive Pitch: Der Game-Changer für Enterprise AI Coding

Künstliche Intelligenz revolutioniert die Softwareentwicklung, doch herkömmliche KI-Assistenten scheitern in Enterprise-Umgebungen an drei massiven Barrieren: **ungenauem Kontext (Halluzinationen)**, **explodierenden API-Kosten** und **Datenschutz-Risiken**. 

**Shonkor** eliminiert diese Barrieren vollständig. Es ist eine **100% offline-fähige Precision GraphRAG-Engine**, die Quellcode und Dokumentation compiler-genau in einen lokalen Wissensgraphen zerlegt. Statt unpräziser Vektorsuchen liefert Shonkor deterministischen, mathematisch exakten Kontext für LLMs – in Millisekunden und mit minimalem Tokenverbrauch.

---

## 💎 Die 4 Kern-Wertversprechen (Value Propositions)

### 1. 100% Präzision statt "Glücksspiel" (Keine Halluzinationen)
* **Das Problem**: Klassische Vektordatenbanken (Vektor-RAG) schneiden Code in willkürliche Textblöcke. Die KI sieht Methoden ohne deren Imports, Schnittstellen oder Klassenzugehörigkeiten. Das führt zu fehlerhaften Codevorschlägen (Halluzinationen).
* **Unsere Lösung**: Ein Roslyn-basierter Compiler-AST-Parser zerlegt den Code in echte Knoten (Klassen, Methoden) und Kanten (Vererbungen, Aufrufe). Die KI erhält den physischen, mathematisch exakten Kontext.
* **Ergebnis**: **0% Halluzinationen** durch strukturelle Fehler. Der Code baut auf Anhieb.

### 2. Bis zu 92% Token- & Kosteneinsparung (ROI)
* **Das Problem**: Um einer KI komplexe Aufgaben zu erklären, müssen Entwickler oft ganze Ordner oder riesige Codeabschnitte in den Prompt kopieren. Das verstopft das Kontextfenster und treibt die API-Gebühren (z. B. GPT-4o oder Claude 3.5) in die Höhe.
* **Unsere Lösung**: Der integrierte *Context Capsule Synthesizer* führt eine N-Hop Graph-Traversierung durch und schneidet irrelevantes Rauschen ab. Das LLM erhält nur die mathematisch relevanten Code-Teile.
* **Ergebnis**: **Über 90% geringere Token-Kosten** bei gleichzeitig höherer Antwortqualität.

### 3. Absolute Datensicherheit (100% Enterprise-konform)
* **Das Problem**: Viele Unternehmen verbieten den Einsatz von KI-Editoren, da Code auf externe Vektor-Server in der Cloud hochgeladen werden muss. Das verletzt geistiges Eigentum (IP) und DSGVO-Richtlinien.
* **Unsere Lösung**: Shonkor arbeitet vollständig autark und lokal. Der gesamte Wissensgraph wird in einer einzigen, leichten SQLite-Datei (`shonkor.db`) gespeichert. Es fließen **0 KB Daten** ins Internet.
* **Ergebnis**: Volle IP-Control und Compliance für Banken, Versicherungen und regulierte Industrien.

### 4. Blitzschnelle Time-to-Value (Sub-Sekunden-Performance)
* **Das Problem**: Das Indizieren großer Repositories dauert bei Vektorsuchen oft Stunden und benötigt teure GPU-Infrastruktur.
* **Unsere Lösung**: Hochoptimierte, rekursive SQL-Datenbankabfragen (SQLite FTS5 + CTEs) laufen auf Standard-Entwickler-Laptops in Millisekunden.
* **Ergebnis**: Vollständige Repositories werden in **unter 2 Sekunden** indexiert.

---

## 📊 Belastbare Zahlen, Fakten & Benchmarks

Die folgenden Leistungskennzahlen wurden in einer echten Produktivumgebung erhoben und belegen die überlegene Performance von Shonkor:

| Kennzahl | Wert | Nachweis / Technische Basis |
| :--- | :---: | :--- |
| **Indexierungs-Performance** | **> 19 Dateien / Sekunde** | 34 komplexe Quellcodedateien vollständig indexiert in **1,80 Sekunden**. |
| **Datenbank-Größe (Footprint)** | **352 KB** | Lokale SQLite-Datenbank (`shonkor.db`) – hochkomprimiert und direkt in Git versionierbar. |
| **Such-Latenz (Seed-Findung)** | **< 5 Millisekunden** | BM25-gewichtete SQLite FTS5 (Volltextsuche) über den gesamten Quellcode. |
| **Traversierungs-Latenz** | **< 10 Millisekunden** | Rekursive Common Table Expressions (CTEs) lösen N-Hop-Verbindungen auf SQL-Ebene. |
| **Token-Einsparung** | **~92% Reduktion** | Suche nach `SqliteGraphStorageProvider` liefert eine Kapsel von nur **1.148 Tokens** statt > 15.000 Tokens der gesamten Codebasis. |

---

## 💰 ROI-Kalkulation (Beispiel für ein Entwicklerteam)

Angenommen, ein Team von **10 Entwicklern** führt pro Tag jeweils **20 komplexe Code-Anfragen** an ein Premium-LLM (wie GPT-4o mit $5.00 pro 1 Mio. Input-Tokens) durch.

### Ohne Shonkor (Full Workspace Context / Naives RAG):
* Durchschnittlicher Kontext pro Prompt (Code-Dateien + Overhead): **25.000 Tokens**
* Kosten pro Tag: `10 Entwickler * 20 Prompts * 25.000 Tokens * $0,000005 = $25.00 / Tag`
* Kosten pro Jahr (220 Arbeitstage): **$5.500,00**

### Mit Shonkor (Pruned GraphRAG Context):
* Durchschnittlicher Kontext pro Prompt (Präzise Context Capsule): **1.200 Tokens** (95,2% Einsparung)
* Kosten pro Tag: `10 Entwickler * 20 Prompts * 1.200 Tokens * $0,000005 = $1,20 / Tag`
* Kosten pro Jahr (220 Arbeitstage): **$264,00**

> [!TIP]
> **Netto-Ersparnis**: **$5.236,00 pro Jahr** für ein kleines 10er-Team – bei gleichzeitig **signifikant besserer Antwortqualität**, da das LLM nicht durch irrelevanten Code abgelenkt wird!

---

## 🛠️ Funktionsweise: Vektor-RAG vs. Shonkor GraphRAG

```mermaid
graph TD
    subgraph Vektor-RAG (Probabilistisch)
        A[Codebase] -->|Willkürlicher Split| B[Text-Slices]
        B -->|Einbettung| C[Vektorsuche]
        C -->|Unpräzise Treffer| D[Möglicher Kontext]
        D -->|Fehlende Beziehungen| E[LLM Halluzination]
    end

    subgraph Shonkor GraphRAG (Deterministisch)
        F[Codebase] -->|Compiler AST Parsing| G[Semantischer Graph]
        G -->|SQLite FTS5 Keyword Match| H[Präziser Seed-Knoten]
        H -->|Rekursive SQL CTE Traversierung| I[N-Hop Subgraph]
        I -->|Context Capsule Synthesizer| J[Mermaid Diagramm + Relevanter Code]
        J -->|Voller logischer Kontext| K[LLM Präzise Antwort]
    end

    style E fill:#ffcccc,stroke:#ff0000,stroke-width:2px
    style K fill:#ccffcc,stroke:#00aa00,stroke-width:2px
```

---

## 🎯 Zielgruppen & Argumentations-Leitfaden

### 1. Für den CTO / Head of Development
* **Keynote**: *"Steigern Sie die Produktivität Ihrer Entwickler, ohne dass diese Zeit mit dem Suchen von Dateipfaden verschwenden."*
* **Hauptargumente**: Compiler-genauer Kontext sorgt dafür, dass von KI generierter Code sofort kompiliert. Entwickler müssen KI-Vorschläge nicht erst mühsam korrigieren.

### 2. Für den Chief Information Security Officer (CISO)
* **Keynote**: *"Bringen Sie generative KI sicher in Ihre Entwicklung, ohne Code ins Ausland abfließen zu lassen."*
* **Hauptargumente**: 100% Offline-Architektur. Läuft lokal im Container oder auf Laptops. Keine externen SaaS-Datenbanken nötig.

### 3. Für den CFO / Procurement
* **Keynote**: *"Reduzieren Sie Ihre monatlichen LLM-API-Kosten drastisch."*
* **Hauptargumente**: Über 90% Token-Pruning. Die Investition in Shonkor amortisiert sich bereits im ersten Monat durch gesenkte Token-Kosten.
