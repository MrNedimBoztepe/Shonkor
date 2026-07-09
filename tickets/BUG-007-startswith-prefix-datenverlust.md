# BUG-007 — Stale-File-Cleanup löscht per Pfad-Präfix Daten fremder Verzeichnisse

**Schweregrad:** Hoch · **Status:** Bestätigt · **Bereich:** Ingestion / GraphIndexScanner · **Datenverlust**

## Kontext

Zwei Stellen vergleichen per `indexedFile.StartsWith(directoryPath, OrdinalIgnoreCase)` ohne Trailing-Separator-Guard ([GraphIndexScanner.cs:190](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs) im Scan-Cleanup, [:379-380](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs) in `DetectDriftAsync`):

- Scan von `C:\Repo` behandelt indizierte Dateien unter `C:\Repo2\…` als „unter diesem Verzeichnis"; da sie keine Kandidaten sind, werden sie **aus dem Graph gelöscht**.
- Umgekehrt wird `directoryPath` nie mit `Path.GetFullPath` normalisiert (Kandidaten schon) — ein relativer/abweichend geformter Pfad lässt den Vergleich nie matchen und deaktiviert das Cleanup still (Stale-Nodes überleben jeden Re-Index).

## Reproduktion

DB mit Dateien aus `C:\Repo` und `C:\Repo2` (z. B. via `reindex_file`); vollen Scan über `C:\Repo` laufen lassen → `C:\Repo2`-Knoten sind gelöscht.

## Fix

Gemeinsamer Helper: `directoryPath = Path.GetFullPath(directoryPath)`, dann `EnsureTrailingSeparator(…)` und erst danach `StartsWith`. Beide Stellen (Zeile 190 und 379) umstellen.

## Akzeptanzkriterien

- [ ] Scan eines Verzeichnisses löscht keine Knoten aus Namens-Präfix-Geschwistern (`Brain` vs. `Brainstorm`).
- [ ] Relativer/trailing-slash `directoryPath` liefert dasselbe Cleanup-Verhalten wie der kanonische Pfad.
- [ ] Unit-Tests für beide Randfälle (Geschwister-Präfix, nicht-normalisierter Input).

## DoD

- Fix + Tests gemerged.
