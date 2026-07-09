# TICKET-209 – MCP-Sicherheit: Pfad-Containment, set_project-Session, AllowLocalBypass, record-Härtung

**Schweregrad-Bezug:** H8, H9, M11, M12 · **Aufwand:** M · **Risiko:** niedrig × mittel (Containment kann Out-of-Root-Workflows brechen — Fehlermeldung nennt den Root)

## Kontext
1. **Path Traversal:** Alle fünf datei-akzeptierenden Tools (`reindex_file`, `check_edit`, `outline`, `freshness`, `review` — Muster `EditLoopTools.cs:46-50`) lassen absolute Pfade ungeprüft durch; `FromHandle` (`McpToolHelpers.cs:174-180`) konkateniert `@/../…` ohne Normalisierung. Kein Check gegen den Projekt-Root. Kette: `reindex_file` indiziert beliebige Dateien → `search_graph`/`get_source` exfiltrieren sie.
2. **set_project über HTTP-Relay wirkungslos:** pro POST neuer Handler/Context (`McpEndpoints.cs:68-70`); `SessionProjectOverride` (`MetaTools.cs:118`) stirbt mit dem Request; der stdio-Proxy sendet jede Zeile als eigenen POST. Der Agent bekommt „Erfolg" und arbeitet auf dem falschen Graph.
3. **`Security:AllowLocalBypass=true`** re-aktiviert den Loopback-Bypass in Produktion — hinter Reverse-Proxy ist jede Anfrage 127.0.0.1 (`ApiKeyMiddleware.cs:26-43`).
4. **record:** unbegrenzte Content-Länge, Kanten zu unverifizierten `connectedNodeIds` (`McpToolContext.cs:226-241`), persistenter Cross-Session-Injection-Kanal; Exception-Messages im `-32603` können Pfade leaken.

## Akzeptanzkriterien
- [ ] Gemeinsamer Helper `ResolveContainedPath(raw, basePath)`: `GetFullPath` + `StartsWith(basePath + separator, OrdinalIgnoreCase)`; `..` in Handles abgelehnt; Fehlermeldung nennt den erlaubten Root. In allen fünf Tools verwendet; `TryReadSourceSlice` prüft ebenfalls.
- [ ] `set_project` über das Relay: entweder echter Session-Store (`Mcp-Session-Id`-Header, serverseitig gehaltener Override) **oder** expliziter Fehler „über das HTTP-Relay nicht unterstützt — setze X-Project-Name / projectName pro Call". Kein False-Success mehr.
- [ ] Loopback-Bypass nur wenn Flag **und** `env.IsDevelopment()`; andernfalls lauter Startup-Fehler/Warnung; `/api/mcp` vom Bypass ausgenommen wie `/api/rag`.
- [ ] `record`: Content-Länge gecappt (z. B. 8 KB), `connectedNodeIds` gegen Existenz validiert (keine hängenden Kanten), Inhalt in `get_open_threads`/`orient` als Daten gefenced.
- [ ] Exception-Messages in Relay-Antworten generisch; Details nur ins Server-Log.
- [ ] Tests: Traversal-Versuche (`C:\Windows\...`, `@/../../x`) → Fehler; Relay-Doppel-POST mit set_project → dokumentiertes Verhalten.

## Betroffene Bereiche
`McpToolHelpers.cs`, `EditLoopTools.cs`, `ReadTools.cs`, `MetaTools.cs`, `McpEndpoints.cs`, `McpToolContext.cs`, `ApiKeyMiddleware.cs`, Tests.

## Abhängigkeiten
Keine. Unabhängig von TICKET-210 mergebar.

## Definition of Done
Traversal-Tests grün; manuelle Verifikation mit einem MCP-Client über Relay (set_project-Verhalten) und stdio (unverändert funktionsfähig).
