# TICKET-214 – MCP `generate_capsule` auf den Budget-Synthesizer umstellen

**Schweregrad-Bezug:** H10 · **Aufwand:** S · **Risiko:** niedrig × niedrig

## Kontext
Das MCP-Tool `generate_capsule` (`ReadTools.cs:317-335`) seeded FTS-only (`SearchAsync(query, 5)`), ruft den **Legacy-Overload** `Synthesize(nodes, edges)` ohne `CapsuleOptions` auf und kappt danach blind am letzten `##` vor `maxChars` (`McpToolHelpers.cs:186-202`). Der unlimitierte Renderer gruppiert alphabetisch nach Dateipfad (`ContextCapsuleSynthesizer.cs:169`) — unter Truncation fliegen ausgerechnet die Seed-Knoten raus, die die Query getroffen haben. Die Web-Endpoints machen es richtig (`SearchEndpoints.cs:359-360`: `SeedIds`, `MaxContentChars=12000`, `MaxNodes=40` — Seeds zuerst, immer vollständig, Hub-Schutz). Das Tool, das Agenten tatsächlich aufrufen, hat davon nichts.

## Akzeptanzkriterien
- [ ] `GenerateCapsuleTool` übergibt `CapsuleOptions { SeedIds, MaxContentChars = maxChars > 0 ? maxChars : 12000, MaxNodes = 40 }`.
- [ ] Seeding nutzt den Hybrid-Pfad, wenn ein Embedding-Backend verfügbar ist (Fallback FTS wie bisher).
- [ ] Die nachgelagerte `TruncateAtBoundary`-Kappung entfällt für den budgetierten Pfad (Budget ist bereits enforced) bzw. bleibt nur als Sicherheitsnetz.
- [ ] Test: Query mit alphabetisch spät sortiertem Seed + kleinem `maxChars` → Seed-Body vollständig in der Capsule (schlägt auf altem Code fehl).
- [ ] Bench: Seed-Survival-Rate (TICKET-202) für den MCP-Pfad == Web-Pfad.

## Betroffene Bereiche
`ReadTools.cs`, ggf. `McpToolHelpers.cs`, Tests.

## Abhängigkeiten
Keine. Messbarkeit via TICKET-202 (Seed-Survival).

## Definition of Done
Test grün; Stichprobe mit einem echten Agenten-Query dokumentiert (Capsule enthält Seeds vollständig, Omission-Notice statt stiller Kappung).
