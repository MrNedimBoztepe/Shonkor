# TICKET-101 — Embeddings am Hauptpfad: CLI-Index + stdio-MCP-Server
> **STATUS: ✅ DONE (2026-07-02)** — CLI `index --embed` erzeugt Code-Embeddings (verifiziert: 885 geschrieben); stdio-MCP injiziert Embedding-Service bei erreichbarem Backend → search_semantic verfügbar. Build grün, 151 Tests grün.

**Findings:** K1 · **Risiko:** Mittel × Hoch · **Aufwand:** M · **Abhängigkeiten:** keine

## Kontext
Der gemessene Intent-Recall-Sprung (0,27 → 0,93) hängt an Code-Embeddings, die es auf den Hauptpfaden nicht gibt: CLI `index` erzeugt keine Embeddings ([ParseAndRunIndexAsync](../src/Shonkor.CLI/Program.cs:203)), und der stdio-MCP-Server wird ohne Embedding-Service gebaut ([Program.cs:544](../src/Shonkor.CLI/Program.cs:544)) → `search_semantic` ist dort nicht verfügbar ([MetaTools.cs:50](../src/Shonkor.Infrastructure/Services/Mcp/Tools/MetaTools.cs:50)). Agenten/Offline erhalten faktisch nur FTS + Graph.

## Akzeptanzkriterien
- [ ] `shonkor index --embed` (opt-in) erzeugt Code-Embeddings wie der Web-Worker (gleiche `BuildEmbeddingText`-Logik), inkl. `EmbeddingDim`-Stempel.
- [ ] Der CLI-`mcp`-Server injiziert einen `OllamaEmbeddingService`, wenn ein Backend konfiguriert/erreichbar ist; sonst sauberer FTS-Fallback (unverändertes Verhalten).
- [ ] `search_semantic` wird im stdio-MCP gelistet, sobald ein Embedding-Backend vorhanden ist.
- [ ] Doku macht explizit: semantische/hybride Präzision **erfordert erzeugte Embeddings**; ohne = FTS.
- [ ] `Shonkor.Eval` läuft gegen einen so erzeugten Graphen und reproduziert den semantischen Recall-Gewinn.

## Betroffene Bereiche
`Shonkor.CLI/Program.cs` (index + mcp), `SemanticEnrichmentService.BuildEmbeddingText` (Wiederverwendung), `McpRequestHandler`/`McpToolContext`, Doku.

## Definition of Done
Ein per CLI (mit `--embed`) gebauter Graph liefert über den stdio-MCP `search_semantic`; ohne Backend unverändertes FTS-Verhalten; Eval belegt den Gewinn; „100 % offline" bleibt der Default (Embeddings opt-in).
