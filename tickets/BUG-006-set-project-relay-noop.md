# BUG-006 — `set_project` ist über das HTTP-Relay ein stiller No-op, meldet aber Erfolg

**Schweregrad:** Hoch · **Status:** Bestätigt · **Bereich:** MCP-Relay

## Kontext

Das Relay baut **pro POST** einen neuen `McpRequestHandler`/`McpToolContext` ([McpEndpoints.cs:68-70](../src/Shonkor.Web/Endpoints/McpEndpoints.cs)). `SetProjectTool` setzt `ctx.SessionProjectOverride` ([MetaTools.cs:117-121](../src/Shonkor.Infrastructure/Services/Mcp/Tools/MetaTools.cs)) auf diesem request-lokalen Objekt und antwortet „Active project for this session is now 'X'" — der Zustand ist mit dem Request weg. Der Client glaubt an den Wechsel und liest/schreibt (`record`) anschließend in den falschen Graph. Kombiniert mit BUG-003 besonders tückisch.

## Reproduktion

Über `shonkor mcp-proxy`: `set_project name=Foo`, danach `get_stats` → Statistiken des alten/aktiven Projekts.

## Fix

Entweder (a) `set_project` über das Relay ehrlich machen: wenn keine persistente Session existiert, klare Fehlermeldung „über HTTP-Relay nicht unterstützt — `projectName` pro Aufruf übergeben"; oder (b) Sessions über einen `Mcp-Session-Id`-Header persistieren (Kontext-Cache keyed by Session-ID, mit TTL). Variante (a) ist der sichere Sofort-Fix.

## Akzeptanzkriterien

- [ ] `set_project` über das Relay behauptet keinen Erfolg mehr, den es nicht leisten kann.
- [ ] Über stdio (persistente Session) funktioniert `set_project` unverändert.
- [ ] Test: Relay-Aufruf `set_project` + Folge-Tool → entweder Fehlermeldung (a) oder korrektes Projekt (b).

## DoD

- Fix + Test gemerged; Tool-Beschreibung von `set_project` entsprechend angepasst.
