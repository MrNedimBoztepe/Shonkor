# BUG-005 — `INSERT OR REPLACE` löscht Embedding-Versionierung und Enrichment bei jedem Re-Upsert

**Schweregrad:** Hoch · **Status:** Bestätigt · **Bereich:** Storage / Enrichment

## Kontext

Die REPLACE-Spaltenliste in `UpsertNodesAsync` ([SqliteGraphStorageProvider.cs:104-105](../src/Shonkor.Infrastructure/Storage/SqliteGraphStorageProvider.cs)) enthält weder `EmbeddingDim` noch `EmbeddingModel` → beide werden bei jedem Upsert NULL. `MarkStaleEmbeddingsForReembedAsync` (Zeilen 1509-1517) überspringt `EmbeddingDim IS NULL` gezielt → überlebende Embeddings sind für den Stale-Detektor unsichtbar; ein dimensionsgleicher Modellwechsel mischt still Vektoren zweier Modelle. Zusätzlich: `NeedsSemanticAnalysis = 1` hartkodiert (Zeile 148), `Summary`/`Embedding` werden mit den (meist leeren) Werten des eingehenden Knotens überschrieben → jeder Re-Index wirft bezahltes LLM-Enrichment weg.

Verschärfung: `POST /api/interactions/status` ([StatsEndpoints.cs:96-109](../src/Shonkor.Web/Endpoints/StatsEndpoints.cs)) lädt einen Knoten (Mapper liest kein Embedding, [SqliteRowMapper.cs:51-63](../src/Shonkor.Infrastructure/Storage/SqliteRowMapper.cs)) und upsertet ihn zurück → zerstört das Embedding; akzeptiert beliebige Node-IDs.

## Reproduktion

1. Node mit Embedding + `EmbeddingDim/Model` anlegen; Datei unverändert re-indizieren → `EmbeddingDim/Model` sind NULL, `NeedsSemanticAnalysis = 1`.
2. `POST /api/interactions/status` auf eine beliebige Node-ID → `Embedding` ist NULL.

## Fix

`INSERT … ON CONFLICT(Id) DO UPDATE SET …` mit COALESCE-Semantik: `Summary`/`Embedding`/`EmbeddingDim`/`EmbeddingModel` nur überschreiben, wenn der eingehende Knoten Werte liefert; `NeedsSemanticAnalysis = 1` nur bei geändertem `ContentHash`. Den `interactions/status`-Endpoint auf ein gezieltes `UPDATE Nodes SET Metadata = …` umstellen statt Full-Node-Roundtrip. (Gleiche Änderung wie BUG-002 — zusammen umsetzen.)

## Akzeptanzkriterien

- [ ] Re-Index einer unveränderten Datei erhält Summary, Embedding, Dim, Modell und setzt `NeedsSemanticAnalysis` nicht.
- [ ] Re-Index einer geänderten Datei invalidiert wie bisher.
- [ ] `MarkStaleEmbeddingsForReembedAsync` erkennt einen Modellwechsel auch nach zwischenzeitlichen Upserts.
- [ ] Status-Update eines Knotens lässt sein Embedding unangetastet.

## DoD

- Fix + Tests gemerged; Enrichment-Kostenverhalten (kein Re-Queue unveränderter Knoten) im CHANGELOG vermerkt.
