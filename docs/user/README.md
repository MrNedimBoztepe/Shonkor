# LLMBrain 🧠 - Benutzerhandbücher & End-User Guides

Willkommen in der Benutzerdokumentation von LLMBrain. Diese Guides wurden für Softwareentwickler verfasst, die LLMBrain in ihrem täglichen Workflow zur präzisen Kontextbereitstellung für KI-Modelle einsetzen möchten.

---

## 📚 Inhaltsverzeichnis

### 1. ⚙️ [Setup & Onboarding Guide](file:///c:/Projects/Brain/docs/user/setup_guide.md)
* Schritt-für-Schritt Installation und Start.
* Konfiguration über `llmbrain.json`.
* Ausschlussmuster (Glob-Patterns) zur Performanzoptimierung.

### 2. 💻 [CLI-Referenzhandbuch](file:///c:/Projects/Brain/docs/user/cli_reference.md)
* Beschreibung aller CLI-Befehle: `init`, `index`, `search` und `capsule`.
* Argumente, Parameter und praxisnahe Terminalbeispiele.
* Interpretation von Datenbankstatistiken.

### 3. 🤖 [LLM & IDE-Integration](file:///c:/Projects/Brain/docs/user/llm_integration.md)
* Nutzung von generierten Context Capsules in **Cursor** und **VS Code**.
* Integration in Web-Oberflächen (**ChatGPT**, **Claude.ai**, **Gemini Advanced**).
* Automatische Pipeline-Skripte für optimierten Kontext.

---

## 🛠️ Kurzanleitung zur Verwendung

1. **Konfiguration erstellen**: `llmbrain init` ausführen, um `llmbrain.json` zu generieren.
2. **Repository indexieren**: `llmbrain index .` starten, um die SQLite Graphdatenbank aufzubauen.
3. **Kontext extrahieren**: `llmbrain capsule "MeinModul" -h 2 -o capsule.md` ausführen.
4. **KI füttern**: Die generierte `capsule.md` kopieren oder direkt in Ihren Prompt laden.
