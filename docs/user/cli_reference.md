# Shonkor CLI-Referenzhandbuch 💻

Dieses Handbuch beschreibt die Syntax und Verwendung aller Kommandozeilenbefehle von Shonkor.

---

## ⌨️ Globale Syntax

```powershell
shonkor <command> [arguments] [options]
```

---

## 🛠️ Befehlsreferenz

### 1. `init`
Initialisiert eine Standard-Konfigurationsdatei im aktuellen Arbeitsverzeichnis.

* **Syntax:** `shonkor init`
* **Beschreibung:** Prüft, ob bereits eine `shonkor.json` existiert. Falls nicht, wird eine neue Datei mit Standardwerten (Ignorierung von `bin`, `obj`, `.git`, `node_modules` und Ablage der Datenbank in `shonkor.db`) erstellt.
* **Beispiel:**
  ```powershell
  shonkor init
  ```

---

### 2. `index`
Durchsucht das angegebene Verzeichnis und baut den semantischen Wissensgraphen auf.

* **Syntax:** `shonkor index [directory] [options]`
* **Argumente:**
  * `[directory]` *(Optional)*: Der Pfad zum zu scannenden Verzeichnis. Standardmäßig das aktuelle Verzeichnis (`.`).
* **Optionen:**
  * `-c, --config <file>`: Pfad zur Konfigurationsdatei (Standard: `shonkor.json`).
* **Beispiel:**
  ```powershell
  # Indexiert das aktuelle Verzeichnis mit Standard-Konfiguration
  shonkor index
  
  # Indexiert ein anderes Verzeichnis mit einer spezifischen Konfiguration
  shonkor index C:\Projects\MyProject --config MyProjectConfig.json
  ```

---

### 3. `search`
Führt eine blitzschnelle Volltextsuche (FTS5) auf den Inhalt und Namen aller indizierten Knoten aus.

* **Syntax:** `shonkor search <query> [options]`
* **Argumente:**
  * `<query>`: Der Suchbegriff. Unterstützt SQLite FTS5-Syntax (z. B. Wildcards mit `*`).
* **Optionen:**
  * `-l, --limit <number>`: Maximale Anzahl der zurückgegebenen Ergebnisse (Standard: `10`).
  * `-c, --config <file>`: Pfad zur Konfigurationsdatei (Standard: `shonkor.json`).
* **Beispiel:**
  ```powershell
  # Sucht nach Definitionen, die 'Roslyn' enthalten
  shonkor search "Roslyn"
  
  # Sucht nach Klassen, die mit 'Parser' enden, und limitiert die Ausgabe auf 5
  shonkor search "Parser*" --limit 5
  ```

---

### 4. `capsule`
Generiert eine hochpräzise, token-optimierte Markdown-Kontextkapsel (Context Capsule) für LLMs.

* **Syntax:** `shonkor capsule <query> [options]`
* **Argumente:**
  * `<query>`: Der Suchbegriff zur Identifikation der Startknoten (Seeds) für die Graph-Abfrage.
* **Optionen:**
  * `-h, --hops <number>`: Die Tiefe der Graph-Expansion (Standard: `2`). Höhere Werte ziehen indirekte Abhängigkeiten mit ein.
  * `-o, --out <path>`: Pfad zur zu erzeugenden Markdown-Datei (Standard: `shonkor-capsule.md`).
  * `-c, --config <file>`: Pfad zur Konfigurationsdatei (Standard: `shonkor.json`).
* **Beispiel:**
  ```powershell
  # Erzeugt eine 2-Hop-Kapsel für 'SqliteGraphStorage'
  shonkor capsule "SqliteGraphStorage" --hops 2 --out SqliteCapsule.md
  ```

---

### 5. `mcp`
Startet den **Model Context Protocol (MCP)**-Server über stdio (JSON-RPC). Hierüber binden KI-Assistenten wie **Claude** und **Antigravity** den Wissensgraphen direkt ein.

* **Syntax:** `shonkor mcp [options]`
* **Optionen:**
  * `-c, --config <file>`: Pfad zur Konfigurationsdatei (Standard: `shonkor.json`).
* **Verhalten:** Der Server läuft, bis der Eingabestrom geschlossen wird (EOF). Normalerweise wird er **nicht manuell** gestartet, sondern vom MCP-Client (Claude/Antigravity) automatisch als Subprozess.
* **Projekt-Auflösung:** Das aktive Projekt wird **aus dem Arbeitsverzeichnis** abgeleitet (das Verzeichnis, in dem der Client den Server startet) – nicht aus einem globalen Flag. Override per Umgebungsvariable `SHONKOR_PROJECT`.

#### `mcp install`
Registriert Shonkor automatisch in den MCP-Konfigurationsdateien der erkannten Clients (Claude Desktop, Antigravity).

* **Syntax:** `shonkor mcp install`
* **Beispiel:**
  ```powershell
  dotnet run -- mcp install
  ```

> [!TIP]
> Für einen reproduzierbaren Betrieb empfiehlt sich ein `dotnet publish` und ein Verweis auf die veröffentlichte `.exe` in der Client-Konfiguration, statt auf `bin/Debug` zu zeigen.

---

## 📊 Interpretation der Ausgaben

Bei der Indexierung (`index`) gibt Shonkor detaillierte Metriken aus:
* **Files Scanned**: Anzahl der physischen Dateien, die von den registrierten Parsern analysiert wurden (Binärdateien werden anhand von NUL-Bytes erkannt und übersprungen).
* **Nodes Created**: Anzahl der erzeugten Code- und Dokumentsignaturen (z. B. Klassen, Methoden).
* **Edges Created**: Anzahl der logischen Beziehungen, z. B. `CONTAINS` (Datei→Typ→Member), `IMPLEMENTS`/`EXTENDS` (Vererbung), `REFERENCES_TYPE` (Typ-Verwendung), `IMPORTS`, `BINDS_TO`, `BELONGS_TO_MODULE`.
* **Composition by Type**: Übersicht aller Knotentypen in Ihrer Datenbank (wichtig zur Validierung der Abdeckung).
