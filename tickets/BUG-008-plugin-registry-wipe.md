# BUG-008 — Plugin-Registry wird bei transientem Lesefehler komplett geleert

**Schweregrad:** Hoch · **Status:** Bestätigt · **Bereich:** Plugins · **Datenverlust**

## Kontext

`PluginRegistry.Load()` verschluckt alle Exceptions und gibt eine leere Liste zurück ([PluginRegistry.cs:261-273](../src/Shonkor.Infrastructure/Services/PluginRegistry.cs)). Schreibpfade wie `InstallFromZip` (Zeile 133-134) machen `Load().Where(…).Append(entry)` und `Save(updated)` — war die Datei gerade gesperrt (AV, Backup, zweite Instanz), persistiert der nächste Save den leeren Zustand: **alle installierten Plugins verschwinden aus der Registry**. Verschärfend: `Save()` schreibt nicht-atomar (`File.WriteAllText`, Zeile 278), und `AssemblyPluginLoader.LoadActive` baut eine **eigene** Registry-Instanz mit eigenem Lock ([AssemblyPluginLoader.cs:71](../src/Shonkor.Infrastructure/Services/AssemblyPluginLoader.cs)) — Interleaving zweier Instanzen auf derselben Datei ist möglich.

## Reproduktion

`registry.json` mit exklusivem Handle sperren (z. B. Test-Prozess), währenddessen ein Plugin installieren → Registry enthält danach nur noch das neue Plugin.

## Fix

1. In `Load()` „Datei fehlt" (→ leere Liste ist korrekt) von „Lesen fehlgeschlagen" unterscheiden — Letzteres wirft, die Operation schlägt sauber fehl.
2. Atomar schreiben: Temp-Datei + `File.Replace`.
3. Eine gemeinsame Registry-Instanz injizieren (oder prozess-/instanzübergreifendes `Mutex` keyed auf den Registry-Pfad).

## Akzeptanzkriterien

- [ ] Gesperrte/halb geschriebene Registry-Datei führt zu einem Fehler der laufenden Operation, nie zu Datenverlust.
- [ ] Crash mid-write hinterlässt eine gültige (alte) Registry.
- [ ] Parallel-Test: Install + MarkFailed aus zwei Instanzen verliert keine Einträge.

## DoD

- Fix + Tests gemerged.
