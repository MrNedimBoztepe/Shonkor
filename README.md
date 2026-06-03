# Shonkor 🧠 - Precision GraphRAG & Structural Context Engine

Shonkor ist ein hochpräzises, lokal ausgeführtes Indexierungs- und Abfragesystem für Code und Dokumentation, entwickelt in **.NET 10 (C#)**. Es verwendet einen **Knowledge Graph (GraphRAG)**-Ansatz auf Basis von SQLite (FTS5 + rekursive CTEs), um den logischen Kontext von Softwarearchitekturen vollständig offline und deterministisch zu erfassen und für Large Language Models (LLMs) aufzubereiten.

Im Gegensatz zu probabilistischen Vektordatenbanken garantiert Shonkor **100 % präzisen und strukturellen Kontext**. Es extrahiert Compiler-genaue Syntaxbäume (AST) mittels **Roslyn (C#)** sowie Abhängigkeiten für **JavaScript/TypeScript**, **PHP**, **Sitecore-Konfigurationen (YAML)**, **GraphQL** und **Markdown-Hierarchien**.

Neu: Shonkor verknüpft sich nativ mit **Ollama (lokal)**, um den rohen Source-Code im Hintergrund durch kleine, effiziente Modelle (z.B. `qwen2.5-coder`) in hochverdichtete KI-Summaries zu transformieren. Das senkt den Token-Bedarf für nachgelagerte RAG-Anfragen um bis zu **87 %**!

---

## 🌟 Features

* **Multi-Language AST-Parsing**:
  * **C# (.cs)**: Volle Roslyn-Integration zur Extraktion von Namespaces, Klassen, Interfaces, Records, Structs, Enums, Properties, Konstruktoren und Methoden – inklusive Vererbung (`IMPLEMENTS`/`EXTENDS`) und **Typ-Referenzkanten** (`REFERENCES_TYPE`) für echte Impact-Analyse.
  * **JavaScript/TypeScript (.js, .jsx, .ts, .tsx)**: Extraktion von ES-Imports, React-Komponenten und Backend-APIs.
  * **PHP (.php, .tpl)**: Regex-basierter Modul-Parser für OXID eShop mit Modul-Extends und Smarty-Template-Blöcken.
  * **Sitecore SCS (.yml, .yaml)**: Template- und Layout-Abhängigkeiten (Unicorn/SCS).
  * **GraphQL (.graphql)**: Queries, Fragmente und referenzierte Templates.
  * **Markdown (.md)**: Segmentiert Dokumente nach Überschriften und verknüpft relative Links.
* **Cross-Technology-Linking**: Ein Post-Scan-Linker verbindet Next.js-Komponenten ↔ Sitecore-Renderings ↔ C#-Controller ↔ GraphQL-Templates und ordnet alles Helix-Modulen (`BELONGS_TO_MODULE`) zu.
* **100 % Offline & Self-Contained**: Lokale SQLite-Datenbank (`shonkor.db`) mit FTS5-Volltextsuche und rekursiven CTE-Subgraph-Abfragen. Keine externen API-Abhängigkeiten.
* **Token-Optimierter Context Capsule Synthesizer**: Generiert prompt-fertige Markdown-Dateien inklusive automatischer **Mermaid.js**-Architekturdiagramme.
* **MCP-Server (Model Context Protocol)**: Stellt den Graphen direkt KI-Assistenten wie **Claude** und **Antigravity** bereit – mit token-effizienten Tools (`search_graph`, `locate`, `get_subgraph`, `generate_capsule`, `record_*`).
* **Visual Web Dashboard**: Ein glassmorphes Web-Interface mit interaktiver 2D-Force-Directed-Graph-Visualisierung (`force-graph`, WebGL-Canvas), Live-Physics, Code-Vorschau (Prism.js), Kapsel-Creator, Projekt- und Plugin-Verwaltung.
* **Multi-Projekt-Registry**: Mehrere Codebasen parallel verwalten (`projects.json`), jede mit eigener Datenbank.
* **Leistungsstarke CLI**: Automatisierung über `init`, `index`, `search`, `capsule` und `mcp`.

---

## ⚡️ Benchmark: KI-Graphen vs. Klassisches RAG

In einer kommerziellen C#-Test-Codebasis (50 Klassen) vergleicht dieser Benchmark die Performance einer herkömmlichen Suchanfrage (Fulltext-RAG) mit Shonkors vorab generiertem semantischem Graphen:

* **Token-Bedarf:** ~1.200 Tokens (Shonkor) vs. ~9.800 Tokens (Klassisches RAG) ➡️ **87,7 % eingespart**
* **Kontext-Latenz:** ~6 Sekunden (Shonkor) vs. ~50 Sekunden (Klassisches RAG) ➡️ **7,6x schneller**

Shonkor erlaubt somit einen **hochprofitablen Betrieb** von LLM-Chatbots, da der teure Kontext auf ein absolutes Minimum reduziert wird, ohne dass das LLM den architektonischen Überblick verliert.

---

## 📁 Systemstruktur

Das Projekt folgt einer sauberen **Clean Architecture**-Struktur:

```
src/
  ├── Shonkor.Core/          # Domänenmodelle, Schnittstellen, AST-Parser & Capsule-Synthesizer
  ├── Shonkor.Infrastructure/# SQLite Graph-Speicher, Crawler (SHA256), Plugin-Loader, Cross-Tech-Linker
  ├── Shonkor.CLI/           # Konsolen-Schnittstelle (init, index, search, capsule, mcp) + MCP-Server
  └── Shonkor.Web/           # Minimal APIs, API-Key-Middleware & Glassmorphic Web Dashboard (wwwroot)
tests/
  └── Shonkor.Tests/         # Unit-Tests für Parser, SQLite-CTE, Concurrency & Type-Reference-Linking
docs/
  ├── developer/arc42/        # Entwicklerdokumentation nach arc42-Standard (Kapitel 1-8)
  ├── user/                   # Benutzerhandbücher (Setup, CLI, LLM-Integration)
  └── architecture/           # Architektur-Reviews
```

---

## 🚀 Schnellstart

### Voraussetzungen
* [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### 1. Klonen & Kompilieren
```powershell
dotnet build
```

### 2. CLI initialisieren und indexieren
```powershell
cd src/Shonkor.CLI

# Standard-Konfiguration (shonkor.json) erstellen
dotnet run -- init

# Eigenes Projekt indexieren
dotnet run -- index ../../

# Nach einer Klasse oder Methode suchen
dotnet run -- search "RoslynAstParser"

# Token-optimierten Context Capsule erstellen
dotnet run -- capsule "RoslynAstParser" --hops 2 --out capsule.md
```

### 3. MCP-Server in Claude/Antigravity registrieren
```powershell
# Registriert Shonkor automatisch in den verfügbaren MCP-Clients
dotnet run -- mcp install
```
Details und manuelle Konfiguration: siehe [LLM Integration Manual](docs/user/llm_integration.md).

### 4. Web Dashboard starten
```powershell
cd ../Shonkor.Web
dotnet run

# Browser öffnen auf: http://localhost:5290
```

---

## 🔐 Sicherheit (Kurzüberblick)

Shonkor ist primär als **lokales** Werkzeug konzipiert. Für den Betrieb hinter einem Proxy / als SaaS gilt:

* **API-Keys & Secrets** gehören **nicht** in `appsettings.json` oder `projects.json` (beide sind gitignored), sondern in User-Secrets / Umgebungsvariablen (`ApiKeys__<key>=<projektName>`, `GitHub__WebhookSecret=…`).
* Der **Loopback-Auth-Bypass** ist nur in `Development` aktiv; in Produktion greift immer die API-Key-Prüfung.
* **Dynamische Plugins** (Laufzeit-Kompilierung von C#) sind ein RCE-Vektor und daher **standardmäßig deaktiviert** – Opt-in über `Security:EnablePlugins=true`.
* **Webhooks** verifizieren `X-Hub-Signature-256` (HMAC) und schlagen ohne konfiguriertes Secret fehl (fail-closed).
* `/api/browse` (Dateisystem-Browser) ist nur lokal/in Development erreichbar.

---

## 📚 Dokumentations-Architektur

1. **Entwickler-Dokumentation (arc42)**: [docs/developer/arc42/README.md](docs/developer/arc42/README.md)
2. **Benutzer-Handbücher**: [docs/user/README.md](docs/user/README.md)
   * [Setup Guide](docs/user/setup_guide.md): Onboarding, Konfiguration, Sicherheit, Multi-Projekt.
   * [CLI Reference](docs/user/cli_reference.md): Alle CLI-Kommandos mit Beispielen.
   * [LLM Integration Manual](docs/user/llm_integration.md): Anbindung an Claude/Antigravity (MCP), Cursor und Web-LLMs.

---

## ⚖️ Lizenz
Dieses Projekt ist unter der MIT-Lizenz lizenziert. Weitere Details finden Sie in der `LICENSE`-Datei.
