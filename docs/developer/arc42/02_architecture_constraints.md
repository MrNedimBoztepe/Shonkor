# arc42 Kapitel 2: Randbedingungen ⚙️

Dieses Kapitel beschreibt die technischen und organisatorischen Vorgaben, die das Design von Shonkor beeinflussen.

---

## 2.1 Technische Randbedingungen

| Randbedingung | Beschreibung | Auswirkung auf die Architektur |
| :--- | :--- | :--- |
| **.NET 10 (C#)** | Das System muss auf der neuesten .NET-Laufzeitumgebung lauffähig sein. | Verwendung moderner C#-Features wie File-Scoped Namespaces, Records, Pattern Matching und Native AOT-Kompatibilität. |
| **SQLite (FTS5 + CTE)** | Verwendung von SQLite als einziges Datenbank-Backend. | Datenbankoperationen müssen über performante SQL-Befehle gelöst werden. Rekursive CTEs und FTS5-Trigger müssen manuell im Setup erstellt werden. |
| **Keine externen Server** | Keine Abhängigkeiten zu Cloud-RAG-Systemen oder externen SaaS-Datenbanken. | Sämtliche Logik (Parser, Storage, CLI und Web-Host) wird lokal auf dem Rechner des Anwenders ausgeführt. |
| **Plattformunterstützung** | Unterstützung für Windows-Systeme (und Linux/macOS via dotnet-Core). | Verwendung von plattformunabhängigen Pfad-Separatoren und standardisierten Dateisystem-Zugriffen. |

---

## 2.2 Organisatorische Randbedingungen

* **GitFlow-Modell**: Konsequente Trennung von Feature-Entwicklungen über kurzlebige `feature/*` Branches, die in einen `develop` Branch und schließlich in einen stabilen `main`/`master` Branch gemergt werden.
* **arc42 Dokumentationsstandard**: Verpflichtung zur Führung und kontinuierlichen Pflege der Systemarchitektur in separaten, versionierten Kapiteln.
* **Dokumentenintegrität**: Jede Code-Änderung erfordert eine unmittelbare Überprüfung der dazugehörigen Architekturdokumentation (Pre-Commit-Richtlinie).

---

## 2.3 Konventionen

* **.NET Code Guidelines**: Einhaltung offizieller Microsoft-Codierrichtlinien (PascalCase für öffentliche Member, camelCase für Parameter, Unterstrich-Präfix `_` für private Felder).
* **SOLID, KISS, DRY**: Vermeidung von Code-Duplikaten durch zentrale Abstraktionen (z. B. `IGraphStorageProvider`) und Trennung von Parser- und Persistenzlogiken.
* **Nullable Reference Types**: Zwingende Aktivierung von `<Nullable>enable</Nullable>` in allen Projektdateien zur Vermeidung von NullReferenceExceptions.
