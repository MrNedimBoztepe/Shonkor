# LLMBrain Setup & Onboarding Guide ⚙️

Dieses Handbuch beschreibt die Erstinstallation, die Konfiguration und den schnellen Einstieg mit LLMBrain in Ihrem lokalen Projekt-Workspace.

---

## 🚀 Erste Schritte & Installation

Da LLMBrain als **100% self-contained** Lösung konzipiert ist, benötigt es weder einen externen Datenbank-Server noch komplexe Docker-Container. Alles, was Sie brauchen, ist das .NET 10 SDK.

### Schritt 1: Kompilieren
Wechseln Sie in das Root-Verzeichnis des Projekts und führen Sie den Build-Befehl aus:
```powershell
dotnet build
```
Nach erfolgreichem Build stehen Ihnen das CLI-Tool und das Web Dashboard zur Verfügung.

---

## 🛠️ Konfiguration (`llmbrain.json`)

Der erste Schritt in jedem neuen Projekt-Workspace ist die Initialisierung der Konfigurationsdatei. Öffnen Sie Ihr Terminal im Root-Verzeichnis Ihres Zielprojekts und führen Sie aus:

```powershell
# Erstellt eine Standard llmbrain.json im aktuellen Verzeichnis
llmbrain init
```

### Die Struktur der `llmbrain.json`

Die erzeugte Datei hat folgendes Format:
```json
{
  "databasePath": "llmbrain.db",
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
1. **`databasePath`**: Der Pfad zu der lokalen SQLite-Datenbank. Standardmäßig wird `llmbrain.db` direkt im aktuellen Verzeichnis angelegt. Sie können diesen Pfad beliebig ändern (z. B. in ein verstecktes Verzeichnis `.llmbrain/brain.db`), um Ihren Workspace sauber zu halten.
2. **`excludePatterns`**: Eine Liste von Glob-Mustern für Dateien und Verzeichnisse, die der Crawler ignorieren soll. 
   > [!TIP]
   > **Performanz-Tipp**: Schließen Sie Build-Ordner (`bin`, `obj`), Abhängigkeiten (`node_modules`, `vendor`) und Versionskontrollordner (`.git`) konsequent aus. Dies beschleunigt den Crawler massiv und verhindert unnötigen Ballast in der Graphdatenbank.

---

## 🔍 Erstmalige Indexierung

Nachdem Sie Ihre `llmbrain.json` konfiguriert haben, führen Sie die Indexierung aus:

```powershell
llmbrain index .
```

Der Crawler analysiert nun rekursiv alle unterstützten Dateien, extrahiert die syntaktischen Strukturen und speichert das Ergebnis ab. Sie sehen am Ende eine detaillierte Zusammenfassung der gescannten Dateien, erzeugten Knoten (Klassen, Methoden) und Kanten (Abhängigkeiten, Implementierungen).

### Inkrementelle Updates (SHA256)
Bei jedem weiteren Aufruf von `llmbrain index` verwendet das System SHA256-Content-Hashes, um geänderte Dateien zu erkennen. Nur modifizierte Dateien werden gelöscht und neu geparst – unveränderte Dateien werden übersprungen. Dies spart wertvolle Rechenzeit bei großen Codebasen.
