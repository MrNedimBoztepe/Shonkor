# LLMBrain 🧠 - Precision GraphRAG & Structural Context Engine

LLMBrain ist ein hochpräzises, lokal ausgeführtes Indexierungs- und Abfragesystem für Code und Dokumentation, entwickelt in **.NET 10 (C#)**. Es verwendet einen **Knowledge Graph (GraphRAG)**-Ansatz auf Basis von SQLite (FTS5 + rekursive CTEs), um den logischen Kontext von Softwarearchitekturen vollständig offline und deterministisch zu erfassen und für Large Language Models (LLMs) aufzubereiten. 

Im Gegensatz zu probabilistischen Vektordatenbanken garantiert LLMBrain **100% präzisen und strukturellen Kontext**. Es extrahiert Compiler-genaue Syntaxbäume (AST) mittels **Roslyn (C#)** sowie Abhängigkeiten für **JavaScript/TypeScript**, **PHP**, **Sitecore-Konfigurationen (YAML)** und **Markdown-Hierarchien**.

---

## 🌟 Features

* **Multi-Language AST-Parsing**:
  * **C# (.cs)**: Volle Roslyn-Integration zur Extraktion von Namespaces, Klassen, Interfaces, Records und Methoden. Erkennt zudem **Optimizely**-Content-Typen.
  * **JavaScript/TypeScript (.js, .jsx, .ts, .tsx)**: Extraktion von ES-Imports, React-Komponenten und Backend-APIs.
  * **PHP (.php, .tpl)**: Regex-basierter Modul-Parser für OXID eShop mit Modul-Extends und Smarty-Template-Blöcken.
  * **Sitecore SCS (.yml, .yaml)**: Template- und Layout-Abhängigkeiten.
  * **Markdown (.md)**: Segmentiert Dokumente nach Überschriften und verknüpft relative Links.
* **100% Offline & Self-Contained**: Lokale SQLite-Datenbank (`llmbrain.db`) mit FTS5-Volltextsuche und rekursiven CTE-Subgraph-Abfragen. Keine externen API-Abhängigkeiten.
* **Token-Optimierter Context Capsule Synthesizer**: Generiert prompt-fertige Markdown-Dateien inklusive automatischer **Mermaid.js**-Architekturdiagramme.
* **Visual Web Dashboard**: Ein wunderschönes, glassmorphes Web-Interface mit einer interaktiven 2D Force-Directed Graph-Visualisierung (Vis.js), Live-Physics, Code-Vorschau (Prism.js) und Capsule-Creator.
* **Leistungsstarke CLI**: Einfache Automatisierung über Terminal-Befehle (`init`, `index`, `search`, `capsule`).

---

## 📁 Systemstruktur

Das Projekt folgt einer sauberen **Clean Architecture**-Struktur:

```
src/
  ├── LLMBrain.Core/          # Reine Domänenmodelle, Schnittstellen & AST-Parser
  ├── LLMBrain.Infrastructure/# SQLite Graph-Speicher & Dateisystem-Crawler (SHA256)
  ├── LLMBrain.CLI/           # Konsolen-Schnittstelle (init, index, search, capsule)
  └── LLMBrain.Web/           # Minimal APIs & Glassmorphic Web Dashboard (wwwroot)
tests/
  └── LLMBrain.Tests/         # Unit Tests für Parser & SQLite-CTE-Abfragen
docs/
  ├── developer/arc42/        # Entwicklerdokumentation nach arc42-Standard (Kapitel 1-8)
  └── user/                   # Benutzerhandbücher (Setup, CLI, LLM-Integration)
```

---

## 🚀 Schnellstart

### Voraussetzungen
* [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### 1. Klonen & Kompilieren
```powershell
# In das Workspace-Verzeichnis wechseln und bauen
dotnet build
```

### 2. CLI initialisieren und indexieren
```powershell
# CLI-Verzeichnis öffnen oder direkt ausführen
cd src/LLMBrain.CLI

# Standard-Konfiguration (llmbrain.json) erstellen
dotnet run -- init

# Eigenes Projekt indexieren
dotnet run -- index ../../

# Nach einer Klasse oder Methode suchen
dotnet run -- search "RoslynAstParser"

# Token-optimierten Context Capsule erstellen
dotnet run -- capsule "RoslynAstParser" --hops 2 --out capsule.md
```

### 3. Web Dashboard starten
```powershell
cd ../LLMBrain.Web

# Server starten
dotnet run

# Browser öffnen auf: http://localhost:5000 oder http://localhost:5001
```

---

## 📚 Dokumentations-Architektur

Unser Projekt legt höchsten Wert auf strukturierte Dokumentation gemäß den Entwicklungsrichtlinien. Sie teilt sich in zwei Bereiche:

1. **Entwickler-Dokumentation (arc42)**: [docs/developer/arc42/README.md](file:///c:/Projects/Brain/docs/developer/arc42/README.md)
   * Enthält die detaillierte Softwarearchitektur (Kapitel 1 bis 8) nach dem offiziellen arc42-Standard.
2. **Benutzer-Handbücher**: [docs/user/README.md](file:///c:/Projects/Brain/docs/user/README.md)
   * [Setup Guide](file:///c:/Projects/Brain/docs/user/setup_guide.md): Onboarding, Konfiguration (`llmbrain.json`).
   * [CLI Reference](file:///c:/Projects/Brain/docs/user/cli_reference.md): Ausführliche Erklärung aller CLI-Kommandos mit Beispielen.
   * [LLM Integration Manual](file:///c:/Projects/Brain/docs/user/llm_integration.md): Anleitung zur Anbindung an Cursor, Claude Desktop (MCP) und Web-LLMs.

---

## ⚖️ Lizenz
Dieses Projekt ist unter der MIT-Lizenz lizenziert. Weitere Details finden Sie in der `LICENSE`-Datei.
