# BUG-015 — Drift-Reconcile konvergiert nie: ausgeschlossene Dateien werden wiederbelebt; neue Binär-/Übergröße-Dateien loopen

**Schweregrad:** Hoch · **Status:** Bestätigt · **Bereich:** Ingestion / Drift-Reconciler

## Kontext

Zwei unabhängige Ursachen, gleiche Wirkung (`DriftReport.IsClean` wird nie `true`, Hintergrund-Reconciler arbeitet endlos):

**(a) Ausgeschlossene Dateien werden re-indiziert statt entfernt.** `DetectDriftAsync` klassifiziert eine indizierte, auf Disk vorhandene, aber jetzt exclude-gematchte Datei als `Deleted`; `ReconcileDriftAsync` schickt `drift.Deleted` durch `ScanFileAsync` ([GraphIndexScanner.cs:395-408](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs)), das Exclude-Patterns nicht kennt und nur bei „kein Parser / Datei fehlt / zu groß / binär" löscht (Zeilen 572-585). Die Datei wird **re-indiziert**, der nächste Drift-Pass meldet sie wieder `Deleted` — explizit ausgeschlossener Inhalt bleibt dauerhaft im Graph und wird jeden Zyklus neu verarbeitet.

**(b) Neue Binär-/Übergröße-Dateien loopen.** In `DetectDriftAsync` greift der Größen-/Binär-Check nur im `changed`-Zweig ([GraphIndexScanner.cs:345-361](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs)) — eine neue >5-MB- oder Binär-Datei mit parsebarer Extension landet in `New`, `ScanFileAsync` verwirft sie, nächster Pass: wieder `New`.

## Reproduktion

(a) Ordner indizieren, dann Exclude-Pattern dafür setzen, `ReconcileDriftAsync` zweimal laufen lassen → beide Läufe verarbeiten dieselben Dateien, Graph enthält sie weiterhin. (b) 6-MB-`.md`-Datei anlegen → jeder Drift-Pass meldet sie als `New`.

## Fix

(a) `drift.Deleted` direkt über `DeleteByFilePathAsync` + `MaintainReferencersAsync` abwickeln, oder Exclude-Patterns in `ScanFileAsync` durchreichen und „excluded" wie „kein Parser" behandeln. (b) Größen-/Binär-Filter auch auf den `added`-Zweig anwenden.

## Akzeptanzkriterien

- [ ] Nach Exclude eines indizierten Ordners: erster Reconcile entfernt die Knoten, zweiter Reconcile ist clean.
- [ ] Neue Binär-/Übergröße-Datei erscheint in keinem Drift-Report (oder einmalig mit explizitem Skip-Status).
- [ ] `IsClean` erreicht in beiden Szenarien `true`.

## DoD

- Fix + Tests gemerged.
