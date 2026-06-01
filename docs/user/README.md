# Shonkor 🧠 - Benutzerhandbücher & End-User Guides

Willkommen in der Benutzerdokumentation von Shonkor. Diese Guides wurden für Softwareentwickler verfasst, die Shonkor in ihrem täglichen Workflow zur präzisen Kontextbereitstellung für KI-Modelle einsetzen möchten.

---

## 📚 Inhaltsverzeichnis

### 1. ⚙️ [Setup & Onboarding Guide](file:///c:/Projects/Brain/docs/user/setup_guide.md)
* Schritt-für-Schritt Installation und Start.
* Konfiguration über `shonkor.json`.
* Ausschlussmuster (Glob-Patterns) zur Performanzoptimierung.

### 2. 💻 [CLI-Referenzhandbuch](file:///c:/Projects/Brain/docs/user/cli_reference.md)
* Beschreibung aller CLI-Befehle: `init`, `index`, `search`, `capsule` und `mcp` (+`mcp install`).
* Argumente, Parameter und praxisnahe Terminalbeispiele.
* Interpretation von Datenbankstatistiken.

### 3. 🤖 [LLM & IDE-Integration](file:///c:/Projects/Brain/docs/user/llm_integration.md)
* Live-Anbindung über den **MCP-Server** (**Claude**, **Antigravity**) inkl. Tool-Übersicht.
* Nutzung von generierten Context Capsules in **Cursor** und **VS Code**.
* Integration in Web-Oberflächen (**ChatGPT**, **Claude.ai**).

### 4. 📈 [Sales Presentation / Pitch Deck](file:///c:/Projects/Brain/docs/user/sales_presentation.md)
* Kern-Wertversprechen (Value Propositions), Pitch und Zielgruppen.
* Belastbare Messzahlen, Performance-Benchmarks und technische Nachweise.
* ROI-Kalkulation und Token-Kostenersparnis für Enterprise-Kunden.

---

## 🛠️ Kurzanleitung zur Verwendung

1. **Konfiguration erstellen**: `shonkor init` ausführen, um `shonkor.json` zu generieren.
2. **Repository indexieren**: `shonkor index .` starten, um die SQLite Graphdatenbank aufzubauen.
3. **Kontext extrahieren**: `shonkor capsule "MeinModul" -h 2 -o capsule.md` ausführen.
4. **KI füttern**: Die generierte `capsule.md` kopieren oder direkt in Ihren Prompt laden.
