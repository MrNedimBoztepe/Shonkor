# TICKET-215 – Vektor-Suche skalieren (Normalisierung, Zero-Copy, Matrix-Cache)

**Schweregrad-Bezug:** H12, M6 (SQL-Teil), M3 (Score-Sichtbarkeit) · **Aufwand:** S (Stufe 1) / M (Stufe 2) · **Risiko:** niedrig × niedrig (Stufe 1), mittel × niedrig (Cache-Invalidierung)

## Kontext
`SearchSemanticAsync` (`SqliteGraphStorageProvider.cs:353-435`) liest pro Query **jeden** Embedding-Blob der DB und allokiert pro Zeile ein frisches `float[]` (`:387-388`) vor `TensorPrimitives.CosineSimilarity`. Bei 100k Knoten ≈ 300 MB Page-I/O + 100k Allokationen pro Query; `search_hybrid` fordert `limit*2` an. Der Top-K-Heap ist exakt — der `overscanFactor=4` (`:366-367`) bringt daher nichts. Kein Similarity-Floor; MCP-Output blendet Scores aus (`FindTools.cs:197-202`) — Rausch-Treffer bei Cosine ~0,3 sehen aus wie echte.

## Akzeptanzkriterien — Stufe 1 (sofort)
- [ ] Vektoren werden beim Schreiben L2-normalisiert (Bestand: beim Re-Embed bzw. einmalige Migration); Scoring per Dot-Product.
- [ ] `MemoryMarshal.Cast<byte, float>(blob)` statt per-Row-Kopie; `overscanFactor` entfernt.
- [ ] Konfigurierbarer Similarity-Floor (Default ~0,5 für nomic); gefilterte Treffer werden als „schwache Treffer unterhalb der Schwelle ausgeblendet" vermerkt; MCP-Output zeigt den gerundeten Score pro Zeile.
- [ ] Subgraph-CTE als zwei UNION-Branches (`e.SourceId = s.Id` / `e.TargetId = s.Id`) → nutzt `idx_edges_source`/`idx_edges_target` (kann alternativ in TICKET-213 landen — nicht doppelt).

## Akzeptanzkriterien — Stufe 2 (Trigger: > ~20k Knoten oder Latenz > 200 ms)
- [ ] In-Memory-(Id, Vektor)-Matrix pro Projekt mit Generation-Counter, invalidiert bei Upsert/Delete; Fallback auf DB-Scan bei Cache-Miss.
- [ ] Speicher-Budget dokumentiert (float32 ~3 KB/Knoten; optional fp16-Ablage).
- [ ] Benchmark: Query-Latenz auf synthetischem 100k-Graph < 100 ms (vorher/nachher im PR).

## Alternative (bewusst vertagt)
`sqlite-vec` (ANN, bleibt SQLite-only) — evaluieren, falls Stufe 2 nicht reicht; externer Vektorstore bricht das Offline-Versprechen und ist erst weit jenseits 1M Knoten diskutabel.

## Betroffene Bereiche
`SqliteGraphStorageProvider.cs`, `OllamaEmbeddingService.cs`/`SemanticEnrichmentService.cs` (Normalisierung beim Schreiben), `FindTools.cs`, Bench.

## Abhängigkeiten
Stufe 1: keine. Stufe 2: nach TICKET-212 (Cache-Invalidierung braucht dessen saubere Upsert-Pfade). Floor-Kalibrierung via TICKET-202-`negatives.json`.

## Definition of Done
Stufe-1-Änderungen gemerged, Retrieval-Metriken unverändert (Exaktheit bleibt), Latenz/Allokationen vorher/nachher gemessen; Stufe 2 nur bei Trigger, mit eigenem Benchmark.
