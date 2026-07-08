# TICKET-207 – Provenance-Integrität durchsetzen

**Schweregrad-Bezug:** K5, M5 · **Aufwand:** S · **Risiko:** niedrig × niedrig (Bestandsgraphen behalten falsche Stempel bis zum Re-Index — dokumentieren)

## Kontext
`Provenance.cs:7-9` erklärt die Trust-Tiers zum Kern-Differenzierungsmerkmal („heuristic sources never claim Extracted"). Verletzt von: (a) name-basiertem Fallback des `SemanticCsharpLinker` — heuristisch, multi-Kandidat (`SemanticCsharpLinker.cs:168-175`), persistiert ohne Provenance (`:120-122`) → Default `Extracted`; das Edge-Tupel des Linkers kann Provenance strukturell nicht tragen (`:135`); (b) LLM-`RELATES_TO`-Kanten per Raw-SQL ohne Provenance-Spalte (`SqliteGraphStorageProvider.cs:1443-1445`, Spalten-Default 0 = Extracted); (c) Regex-Parser `GraphQLParser` und `SitecoreXmCloudPlugin` ohne `DefaultProvenance`-Override (`IFileParser.cs:37`). Zusätzlich friert `INSERT OR IGNORE` beim Edge-Upsert veraltete Provenance ein, und `GraphEdge.Properties` wird nie persistiert (`SqliteSchema.cs:60-68`).

## Akzeptanzkriterien
- [ ] Linker-Edge-Tupel trägt Provenance; Name-Fallback stempelt `Inferred` (eindeutig) / `Ambiguous` (mehrere Kandidaten) — analog `CrossTechLinker.cs:150`.
- [ ] `RELATES_TO`-Insert setzt explizit `Provenance = Inferred`.
- [ ] `GraphQLParser` und `SitecoreXmCloudPlugin` überschreiben `DefaultProvenance => Inferred` (strukturelle CONTAINS/DEFINED_IN ggf. per-Edge Extracted).
- [ ] Edge-Upsert: `ON CONFLICT(SourceId,TargetId,RelationType) DO UPDATE SET Provenance = MIN(excluded.Provenance, Provenance)` — exakte Auflösung darf Trust upgraden.
- [ ] `GraphEdge.Properties` wird als JSON-Spalte persistiert und beim Lesen materialisiert (oder — falls dagegen entschieden — aus dem Modell entfernt; Entscheidung im PR begründen).
- [ ] Guard-Test: Iteration über alle Schreibpfade (Parser, Linker, Enrichment, record, Plugins) — kein Pfad persistiert `Extracted`, außer er ist als deterministisch whitelisted.

## Betroffene Bereiche
`SemanticCsharpLinker.cs`, `SqliteGraphStorageProvider.cs`, `SqliteSchema.cs`, `GraphQLParser.cs`, `SitecoreXmCloudPlugin.cs`, Tests.

## Abhängigkeiten
Keine. Re-Index-Hinweis mit TICKET-204/208 bündeln.

## Definition of Done
Guard-Test grün; nach Voll-Re-Index von Shonkor selbst: Anteil `Extracted` an RELATES_TO/Fallback-Kanten = 0 (per SQL-Stichprobe im PR belegt).
