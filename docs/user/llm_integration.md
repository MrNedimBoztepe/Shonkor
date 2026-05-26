# LLM & IDE Integration Manual 🤖

Dieses Handbuch beschreibt Best Practices und Workflows, um die von LLMBrain generierten strukturellen Kontexte optimal in moderne KI-Assistenten (wie Cursor, Claude Desktop, ChatGPT oder Claude.ai) einzubinden.

---

## 💻 Integration in IDE-Assistenten (Cursor, VS Code)

Moderne KI-Code-Editoren wie **Cursor** erlauben das direkte Einbinden von lokalen Dateien als Kontext. LLMBrain eignet sich hierfür hervorragend, um die Ungenauigkeiten der automatischen RAG-Systeme zu umgehen.

### Workflow mit Cursor:
1. Generieren Sie vor Ihrer Coding-Session eine Kontextkapsel für Ihr Zielthema:
   ```powershell
   llmbrain capsule "RoslynAstParser" --hops 2 --out docs/context_roslyn.md
   ```
2. Tippen Sie in Cursor-Chat oder Composer `@context_roslyn.md`.
3. Formulieren Sie Ihren Prompt, zum Beispiel:
   > "Erstelle eine neue Klasse `NewSpecializedParser` basierend auf dem geladenen Kontext. Beachte die Vererbungshierarchien im Mermaid-Diagramm und die Signatur von `IFileParser`."
4. Die KI liest nun ein perfektes, token-optimiertes Architekturdiagramm sowie exakt die relevanten Klassen- und Methodendefinitionen ein.

---

## 🌐 Integration in Web-LLMs (Claude.ai, ChatGPT)

Wenn Sie mit Web-Oberflächen arbeiten, ist die manuelle Bereitstellung von Kontext oft mühsam. Eine LLMBrain-Kapsel löst dieses Problem durch eine einzige, strukturierte Datei.

### Warum Mermaid.js-Diagramme so mächtig sind:
Jede von LLMBrain generierte Kontextkapsel beginnt mit einem detaillierten **Mermaid.js-Architekturdiagramm** (Knoten und Kanten).
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
echo "Updating local LLMBrain context capsule..."
llmbrain index .
llmbrain capsule "CoreArchitecture" --hops 2 --out .cursor/context-architecture.md
```
Dies stellt sicher, dass Ihr KI-Assistent in Cursor immer die absolut fehlerfreie, aktuelle Struktur des Zweiges kennt, an dem Sie arbeiten.
