# Shonkor Setup & Onboarding Guide ⚙️

Dieses Handbuch beschreibt die Erstinstallation, die Konfiguration und den schnellen Einstieg mit Shonkor in Ihrem lokalen Projekt-Workspace.

---

## 🚀 Erste Schritte & Installation

Da Shonkor als **100% self-contained** Lösung konzipiert ist, benötigt es weder einen externen Datenbank-Server noch komplexe Docker-Container. Alles, was Sie brauchen, ist das .NET 10 SDK.

### Schritt 1: Kompilieren
Wechseln Sie in das Root-Verzeichnis des Projekts und führen Sie den Build-Befehl aus:
```powershell
dotnet build
```
Nach erfolgreichem Build stehen Ihnen das CLI-Tool und das Web Dashboard zur Verfügung.

---

## 🛠️ Konfiguration (`shonkor.json`)

Der erste Schritt in jedem neuen Projekt-Workspace ist die Initialisierung der Konfigurationsdatei. Öffnen Sie Ihr Terminal im Root-Verzeichnis Ihres Zielprojekts und führen Sie aus:

```powershell
# Erstellt eine Standard shonkor.json im aktuellen Verzeichnis
shonkor init
```

### Die Struktur der `shonkor.json`

Die erzeugte Datei hat folgendes Format:
```json
{
  "databasePath": "shonkor.db",
  "excludePatterns": [
    "**/bin/**",
    "**/obj/**",
    "**/.git/**",
    "**/.vs/**",
    "**/.idea/**",
    "**/node_modules/**",
    "**/*.db",
    "**/*.log"
  ]
}
```

### Erklärung der Parameter:
1. **`databasePath`**: Der Pfad zu der lokalen SQLite-Datenbank. Standardmäßig wird `shonkor.db` direkt im aktuellen Verzeichnis angelegt. Sie können diesen Pfad beliebig ändern (z. B. in ein verstecktes Verzeichnis `.shonkor/brain.db`), um Ihren Workspace sauber zu halten.
2. **`excludePatterns`**: Eine Liste von Glob-Mustern für Dateien und Verzeichnisse, die der Crawler ignorieren soll. 
   > [!TIP]
   > **Performanz-Tipp**: Schließen Sie Build-Ordner (`bin`, `obj`), Abhängigkeiten (`node_modules`, `vendor`) und Versionskontrollordner (`.git`) konsequent aus. Dies beschleunigt den Crawler massiv und verhindert unnötigen Ballast in der Graphdatenbank.

---

## 🔍 Erstmalige Indexierung

Nachdem Sie Ihre `shonkor.json` konfiguriert haben, führen Sie die Indexierung aus:

```powershell
shonkor index .
```

Der Crawler analysiert nun rekursiv alle unterstützten Dateien, extrahiert die syntaktischen Strukturen und speichert das Ergebnis ab. Sie sehen am Ende eine detaillierte Zusammenfassung der gescannten Dateien, erzeugten Knoten (Klassen, Methoden) und Kanten (Abhängigkeiten, Implementierungen).

### Inkrementelle Updates (SHA256)
Bei jedem weiteren Aufruf von `shonkor index` verwendet das System SHA256-Content-Hashes, um geänderte Dateien zu erkennen. Nur modifizierte Dateien werden gelöscht und neu geparst – unveränderte Dateien werden übersprungen. Dies spart wertvolle Rechenzeit bei großen Codebasen. Binärdateien werden anhand von NUL-Bytes im Header erkannt und übersprungen.

---

## 🖥️ Web Dashboard

Für die visuelle Exploration starten Sie das Dashboard:
```powershell
cd src/Shonkor.Web
dotnet run
# -> http://localhost:5290
```
Das Dashboard bietet Graph-Visualisierung, Suche, Kapsel-Erstellung sowie die Verwaltung mehrerer Projekte und (optionaler) Plugins.

---

## 🗂️ Multi-Projekt-Registry (`projects.json`)

Shonkor kann mehrere Codebasen parallel verwalten. Die Registry liegt im Workspace-Root als `projects.json`:
```json
{
  "Projects": [
    { "Name": "MeinProjekt", "Path": "C:\\Projects\\MeinProjekt", "DatabasePath": "C:\\Projects\\MeinProjekt\\shonkor.db", "ApiKey": "" }
  ],
  "ActiveProjectName": "MeinProjekt"
}
```
> [!IMPORTANT]
> `projects.json` kann API-Keys enthalten und ist daher **gitignored**. Niemals committen.

* **Web-Dashboard**: nutzt `ActiveProjectName` als angezeigtes Projekt (umschaltbar in der UI).
* **MCP-Server**: ignoriert `ActiveProjectName` und leitet das Projekt **aus dem Arbeitsverzeichnis** ab. Beide sind entkoppelt – das Dashboard beeinflusst nicht, welches Projekt der KI-Assistent sieht.

---

## 🔐 Sicherheit & Secrets

Shonkor ist primär ein **lokales** Werkzeug. Für Proxy-/SaaS-Betrieb beachten:

* **Secrets niemals in Dateien**: API-Keys und Webhook-Secrets gehören in User-Secrets oder Umgebungsvariablen, nicht in `appsettings.json`/`projects.json`:
  ```text
  ApiKeys__sk-dein-key=ProjektName
  GitHub__WebhookSecret=<dein-secret>
  SaaS__TenantRoot=C:\Projects\SaaS   # optional
  ```
* **Loopback-Bypass**: Das lokale Dashboard darf den API-Key nur in `Development` überspringen. In Produktion (hinter Proxy) wird immer ein gültiger Key verlangt. Override: `Security:AllowLocalBypass`.
* **Dynamische Plugins (RCE-Risiko)**: Die Laufzeit-Kompilierung von C#-Plugins ist **standardmäßig deaktiviert**. Aktivierung nur bewusst über `Security:EnablePlugins=true`; der Plugin-Wizard-Endpoint ist zusätzlich auf lokale/Development-Zugriffe beschränkt.
* **Dateisystem-Browser**: `/api/browse` ist nur lokal/in Development erreichbar (`Security:AllowFilesystemBrowse`).
* **Webhooks**: `/api/webhooks/github/*` verifizieren `X-Hub-Signature-256` (HMAC-SHA256) gegen `GitHub:WebhookSecret` und schlagen ohne Secret fehl (fail-closed).

---

## 🤖 MCP-Server registrieren

Damit KI-Assistenten (Claude, Antigravity) den Graphen live abfragen können:
```powershell
dotnet run --project src/Shonkor.CLI -- mcp install
```
Anschließend den Client neu starten. Details: [LLM Integration Manual](llm_integration.md).
