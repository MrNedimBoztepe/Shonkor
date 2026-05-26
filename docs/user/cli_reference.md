# LLMBrain CLI-Referenzhandbuch 💻

Dieses Handbuch beschreibt die Syntax und Verwendung aller Kommandozeilenbefehle von LLMBrain.

---

## ⌨️ Globale Syntax

```powershell
llmbrain <command> [arguments] [options]
```

---

## 🛠️ Befehlsreferenz

### 1. `init`
Initialisiert eine Standard-Konfigurationsdatei im aktuellen Arbeitsverzeichnis.

* **Syntax:** `llmbrain init`
* **Beschreibung:** Prüft, ob bereits eine `llmbrain.json` existiert. Falls nicht, wird eine neue Datei mit Standardwerten (Ignorierung von `bin`, `obj`, `.git`, `node_modules` und Ablage der Datenbank in `llmbrain.db`) erstellt.
* **Beispiel:**
  ```powershell
  llmbrain init
  ```

---

### 2. `index`
Durchsucht das angegebene Verzeichnis und baut den semantischen Wissensgraphen auf.

* **Syntax:** `llmbrain index [directory] [options]`
* **Argumente:**
  * `[directory]` *(Optional)*: Der Pfad zum zu scannenden Verzeichnis. Standardmäßig das aktuelle Verzeichnis (`.`).
* **Optionen:**
  * `-c, --config <file>`: Pfad zur Konfigurationsdatei (Standard: `llmbrain.json`).
* **Beispiel:**
  ```powershell
  # Indexiert das aktuelle Verzeichnis mit Standard-Konfiguration
  llmbrain index
  
  # Indexiert ein anderes Verzeichnis mit einer spezifischen Konfiguration
  llmbrain index C:\Projects\MyProject --config MyProjectConfig.json
  ```

---

### 3. `search`
Führt eine blitzschnelle Volltextsuche (FTS5) auf den Inhalt und Namen aller indizierten Knoten aus.

* **Syntax:** `llmbrain search <query> [options]`
* **Argumente:**
  * `<query>`: Der Suchbegriff. Unterstützt SQLite FTS5-Syntax (z. B. Wildcards mit `*`).
* **Optionen:**
  * `-l, --limit <number>`: Maximale Anzahl der zurückgegebenen Ergebnisse (Standard: `10`).
  * `-c, --config <file>`: Pfad zur Konfigurationsdatei (Standard: `llmbrain.json`).
* **Beispiel:**
  ```powershell
  # Sucht nach Definitionen, die 'Roslyn' enthalten
  llmbrain search "Roslyn"
  
  # Sucht nach Klassen, die mit 'Parser' enden, und limitiert die Ausgabe auf 5
  llmbrain search "Parser*" --limit 5
  ```

---

### 4. `capsule`
Generiert eine hochpräzise, token-optimierte Markdown-Kontextkapsel (Context Capsule) für LLMs.

* **Syntax:** `llmbrain capsule <query> [options]`
* **Argumente:**
  * `<query>`: Der Suchbegriff zur Identifikation der Startknoten (Seeds) für die Graph-Abfrage.
* **Optionen:**
  * `-h, --hops <number>`: Die Tiefe der Graph-Expansion (Standard: `2`). Höhere Werte ziehen indirekte Abhängigkeiten mit ein.
  * `-o, --out <path>`: Pfad zur zu erzeugenden Markdown-Datei (Standard: `llmbrain-capsule.md`).
  * `-c, --config <file>`: Pfad zur Konfigurationsdatei (Standard: `llmbrain.json`).
* **Beispiel:**
  ```powershell
  # Erzeugt eine 2-Hop-Kapsel für 'SqliteGraphStorage'
  llmbrain capsule "SqliteGraphStorage" --hops 2 --out SqliteCapsule.md
  ```

---

## 📊 Interpretation der Ausgaben

Bei der Indexierung (`index`) gibt LLMBrain detaillierte Metriken aus:
* **Files Scanned**: Anzahl der physischen Dateien, die von den registrierten Parsern analysiert wurden.
* **Nodes Created**: Anzahl der erzeugten Code- und Dokumentsignaturen (z. B. Klassen, Methoden).
* **Edges Created**: Anzahl der logischen Beziehungen (z. B. `CALLS`, `IMPLEMENTS`).
* **Composition by Type**: Übersicht aller Knotentypen in Ihrer Datenbank (wichtig zur Validierung der Abdeckung).
