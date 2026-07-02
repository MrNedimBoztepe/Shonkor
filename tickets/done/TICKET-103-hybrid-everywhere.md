# TICKET-103 — Hybrid-Retrieval dorthin bringen, wo abgefragt wird
> **STATUS: ✅ DONE (2026-07-02)** — search_hybrid als MCP-Tool + Dashboard-Brain-Modus nutzt /api/search/hybrid; RRF-Unit-Tests + MCP-Tests. Build grün, 151 Tests grün.

**Findings:** H1 · **Risiko:** Niedrig × Mittel · **Aufwand:** S–M · **Abhängigkeiten:** TICKET-101

## Kontext
Die RRF-Fusion existiert nur als REST-Endpoint `/api/search/hybrid` **ohne Aufrufer**: die Dashboard-UI toggelt FTS/Semantik ([app.js:797](../src/Shonkor.Web/wwwroot/app.js:797)), und es gibt kein `search_hybrid` MCP-Tool ([McpToolRegistryFactory.cs:25](../src/Shonkor.Infrastructure/Services/Mcp/McpToolRegistryFactory.cs:25)). Der Nutzen erreicht keine reale Abfrage.

## Akzeptanzkriterien
- [ ] `search_hybrid` als MCP-Tool (nutzt `HybridFusion.ReciprocalRankFusion`); degradiert ohne Embeddings sauber auf FTS.
- [ ] Dashboard bietet Hybrid als Suchmodus (bzw. Default, wenn Embeddings vorhanden) und ruft `/api/search/hybrid`.
- [ ] `Shonkor.Eval`-Modus `hybrid` läuft gegen einen Graphen mit Embeddings; RRF-Parameter über die Eval kalibriert (Hybrid muss die Einzelmodi schlagen, sonst nicht als Default).

## Betroffene Bereiche
`Services/Mcp/Tools/FindTools.cs` + Registry, `wwwroot/app.js`, `SearchEndpoints` (vorhanden), `HybridFusion`.

## Definition of Done
Hybrid ist über MCP und UI nutzbar, per Eval als ≥ Einzelmodi belegt, und degradiert ohne Embeddings ohne Fehler auf FTS.
