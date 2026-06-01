# LLM & IDE Integration Manual 🤖

Dieses Handbuch beschreibt Best Practices und Workflows, um die von Shonkor generierten strukturellen Kontexte optimal in moderne KI-Assistenten (wie Claude, Antigravity, Cursor, ChatGPT oder Claude.ai) einzubinden.

Es gibt zwei Wege:
1. **Live über den MCP-Server** (empfohlen): Der Assistent fragt den Graphen interaktiv ab.
2. **Statisch über Context Capsules**: Eine generierte Markdown-Datei wird als Kontext eingefügt.

---

## 🔌 Integration über den MCP-Server (Claude, Antigravity)

Shonkor implementiert das **Model Context Protocol (MCP)** über stdio (JSON-RPC). Damit kann ein KI-Assistent den Wissensgraphen **live** abfragen, statt auf eine statische Kapsel angewiesen zu sein.

### Einrichtung
```powershell
# Registriert Shonkor automatisch in den erkannten MCP-Clients
dotnet run --project src/Shonkor.CLI -- mcp install
```
Alternativ manuell in der Client-Konfiguration (z. B. `~/.claude.json` oder `~/.gemini/config/mcp_config.json`):
```json
{
  "mcpServers": {
    "shonkor": {
      "command": "C:\\Pfad\\zu\\Shonkor.CLI.exe",
      "args": ["mcp"]
    }
  }
}
```
Nach der Registrierung den Client **neu starten**, damit der Server geladen wird.

### Projekt-Kontext aus dem Arbeitsverzeichnis
Der MCP-Server bestimmt das aktive Projekt **aus seinem Arbeitsverzeichnis** – also dem Verzeichnis, in dem der Client läuft. Es gibt **kein** globales „aktives Projekt", das von außen (z. B. dem Web-Dashboard) beeinflusst wird. Override per Umgebungsvariable `SHONKOR_PROJECT`. Jedes Tool akzeptiert zusätzlich ein optionales `projectName`-Argument für projektübergreifende Abfragen.

### Verfügbare Tools (token-effizient)
| Tool | Zweck | Hinweis |
|------|-------|---------|
| `locate` | Reiner „Wo ist X?"-Lookup → `name -> datei:zeile` | Minimalste Ausgabe, ideal als erster Schritt |
| `search_graph` | FTS5-Suche; eine Zeile pro Treffer (`Typ  Name  datei:zeile`) | `verbose: true` für volles JSON inkl. Connections |
| `get_subgraph` | N-Hop-Traversierung um Seed-Knoten | Kompakter Text (`NODES`/`EDGES`); `verbose: true` für JSON |
| `generate_capsule` | Markdown-Kapsel mit Mermaid + Code | `maxChars` deckelt die Kapsel auf ein Token-Budget |
| `get_stats` | Knoten-/Kantenstatistik der DB | |
| `record_decision` / `record_milestone` / `record_task` / `record_question` | Persistiert Entscheidungen, Meilensteine, Aufgaben und offene Fragen als Knoten im Graph | Verknüpfbar mit `connectedNodeIds` |

### Beispiel-Workflow: Impact-Analyse
> „Welche Typen hängen an `GraphNode`?"

1. `locate` mit `query: "GraphNode"` → liefert die Definitionsdatei und -zeile.
2. `get_subgraph` mit dem gefundenen Node-Id und `hops: 1` → listet über `REFERENCES_TYPE`-Kanten alle Typen, die `GraphNode` verwenden – über Datei- und Modulgrenzen hinweg.

Das ist deutlich token-sparsamer und vollständiger als wiederholtes Durchsuchen + Dateien-Lesen, weil die Abhängigkeitskanten direkt im Graph stehen.

---

## 💻 Integration in IDE-Assistenten (Cursor, VS Code)

Moderne KI-Code-Editoren wie **Cursor** erlauben das direkte Einbinden von lokalen Dateien als Kontext. Shonkor eignet sich hierfür hervorragend, um die Ungenauigkeiten der automatischen RAG-Systeme zu umgehen.

### Workflow mit Cursor:
1. Generieren Sie vor Ihrer Coding-Session eine Kontextkapsel für Ihr Zielthema:
   ```powershell
   shonkor capsule "RoslynAstParser" --hops 2 --out docs/context_roslyn.md
   ```
2. Tippen Sie in Cursor-Chat oder Composer `@context_roslyn.md`.
3. Formulieren Sie Ihren Prompt, zum Beispiel:
   > "Erstelle eine neue Klasse `NewSpecializedParser` basierend auf dem geladenen Kontext. Beachte die Vererbungshierarchien im Mermaid-Diagramm und die Signatur von `IFileParser`."
4. Die KI liest nun ein perfektes, token-optimiertes Architekturdiagramm sowie exakt die relevanten Klassen- und Methodendefinitionen ein.

---

## 🌐 Integration in Web-LLMs (Claude.ai, ChatGPT)

Wenn Sie mit Web-Oberflächen arbeiten, ist die manuelle Bereitstellung von Kontext oft mühsam. Eine Shonkor-Kapsel löst dieses Problem durch eine einzige, strukturierte Datei.

### Warum Mermaid.js-Diagramme so mächtig sind:
Jede von Shonkor generierte Kontextkapsel beginnt mit einem detaillierten **Mermaid.js-Architekturdiagramm** (Knoten und Kanten).
1. Führende Modelle wie **Claude 3.5 Sonnet** oder **GPT-4o** können Mermaid-Code nativ lesen, interpretieren und visuell darstellen.
2. Das Modell sieht somit im **allerersten Token-Intake** die exakten mathematischen Verbindungen Ihres Codes (z. B. welche Methode welche andere Methode aufruft).
3. Dies eliminiert Halluzinationen über nicht existierende Klassen oder falsche Imports vollständig.

---

## 🛠️ Fortgeschrittener Workflow: CI/CD & Auto-Context

Sie können die Kapsel-Generierung in Ihre lokalen Git-Hooks einbinden, um bei jedem Wechsel des Feature-Branches oder vor Commits automatisch einen aktuellen Kontext zu erzeugen.

### Beispiel: Git Post-Checkout Hook
Erstellen Sie eine Datei `.git/hooks/post-checkout` in Ihrem Repository:
```bash
#!/bin/sh
echo "Updating local Shonkor context capsule..."
shonkor index .
shonkor capsule "CoreArchitecture" --hops 2 --out .cursor/context-architecture.md
```
Dies stellt sicher, dass Ihr KI-Assistent in Cursor immer die absolut fehlerfreie, aktuelle Struktur des Zweiges kennt, an dem Sie arbeiten.
