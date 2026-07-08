# BUG-002 — FTS5-Index-Korruption: `INSERT OR REPLACE` + externe-Content-FTS ohne `recursive_triggers`

**Schweregrad:** Kritisch · **Status:** Bestätigt · **Bereich:** Storage / Volltextsuche

## Kontext

`UpsertNodesAsync` nutzt `INSERT OR REPLACE` ([SqliteGraphStorageProvider.cs:104](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs)); die FTS5-Synchronisation hängt an `AFTER INSERT/DELETE/UPDATE`-Triggern ([SqliteSchema.cs:112-150](../src/Shonkor.Infrastructure/Storage/SqliteSchema.cs)). SQLite feuert bei REPLACE den DELETE-Trigger der verdrängten Zeile **nur** mit `PRAGMA recursive_triggers = ON` — das wird nirgends gesetzt. Jedes Re-Upsert eines existierenden Knotens hinterlässt daher einen Geister-FTS-Eintrag (alte rowid) und legt einen neuen an.

`NodesFts` ist external-content (`content=Nodes`): Geister-Einträge lesen über die stale rowid — bei rowid-Wiederverwendung matcht alter Suchtext den **falschen Knoten**; sonst verschwinden Treffer. Der Count-basierte Rebuild-Guard repariert erst beim nächsten Prozessstart.

## Reproduktion

1. Datei indizieren, editieren, `reindex_file`.
2. `SELECT COUNT(*) FROM Nodes` vs. `SELECT COUNT(*) FROM NodesFts` → FTS-Zähler höher.
3. Volltextsuche nach dem alten Inhalt → liefert Treffer, obwohl der Inhalt nicht mehr existiert.

## Fix

`INSERT OR REPLACE` durch `INSERT … ON CONFLICT(Id) DO UPDATE SET …` ersetzen (erhält rowid, feuert den UPDATE-Trigger). Alternativ/zusätzlich `PRAGMA recursive_triggers = ON` in `OpenConnectionAsync`. **Hinweis:** Der `ON CONFLICT`-Umbau ist auch der Fix für BUG-005 — zusammen umsetzen.

## Akzeptanzkriterien

- [ ] Nach n-fachem Re-Upsert desselben Knotens gilt `COUNT(Nodes) == COUNT(NodesFts)` ohne Rebuild.
- [ ] Volltextsuche nach altem (ersetztem) Inhalt liefert keine Treffer mehr; neuer Inhalt wird gefunden.
- [ ] Integrationstest: Index → Edit → Reindex → FTS-Konsistenz-Assertion.

## DoD

- Fix + Test gemerged; bestehende Datenbanken werden beim ersten Start einmalig per FTS-Rebuild saniert (Count-Guard greift) — im CHANGELOG dokumentiert.
