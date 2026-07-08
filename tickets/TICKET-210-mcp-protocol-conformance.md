# TICKET-210 – MCP-Protokoll-Konformität, Output-Clamps, Retry-Hygiene

**Schweregrad-Bezug:** H13, M7, M10 · **Aufwand:** M · **Risiko:** niedrig × niedrig

## Kontext
1. **Tool-Fehler als Protokollfehler:** `McpRequestHandler.cs:191-195` mappt Tool-Exceptions auf `-32603` statt `isError:true`-Result — Clients verstecken die actionable Message vorm Modell (MCP-Spec 2025-06-18: execution errors gehören ins Result).
2. **`ping` fehlt** (Spec: MUST antworten) → `-32601`; Parse-Fehler liefern gar keine Antwort statt `-32700` mit `id:null`; `initialize` echot jede angefragte Protokollversion (`:133-136`); id-lose Notifications → 400 über das Relay.
3. **Unbounded Outputs:** `limit` (`FindTools.cs:43,125`), `hops` (`ReadTools.cs:221`), `maxHops` (`AnalyzeTools.cs:473`) unclamped; `maxChars` default unlimitiert für `get_source`/`generate_capsule` (`ReadTools.cs:106,329`); `get_subgraph verbose` umgeht den Cap (`:250-255`).
4. **Retry-Hygiene:** beide Ollama-Services retryen Cancellation und deterministische 4xx (`OllamaEmbeddingService.cs:87-96`, `OllamaSemanticAnalyzer.cs:128-137`); Blocking-RAG retryt 3× die volle Generierung (bis ~6 min); Webhook-Hintergrund-Scan captured den Request-Token (`WebhookEndpoints.cs:241-245`) — Push-Indexierung kann still sterben.

## Akzeptanzkriterien
- [ ] Tool-Exceptions → `SendResponse(id, { content, isError: true })`; `-32602` bleibt für Parameter-Validierung.
- [ ] `ping` → leeres Result; Parse-Fehler → `-32700` mit `id:null`; Protokollversion gegen Supported-Set validiert (Fallback: Default); Notifications über das Relay → 202 statt 400.
- [ ] Clamps: `limit ≤ 100`, `hops ≤ 5`, `maxHops ≤ 10`; Default-Output-Cap 20–40 KB für `get_source`, `get_subgraph verbose`, `generate_capsule` mit „raise maxChars"-Hinweis.
- [ ] Retry nur bei transienten Fehlern (`HttpRequestException`/5xx/Timeout beim Embedding; beim Blocking-RAG nur Connect-Fehler, max. 1 Retry), nie bei ausgelöster Cancellation; Backoff mit Jitter — oder Umstellung der typed Clients auf `Microsoft.Extensions.Http.Resilience`.
- [ ] Webhook-Task nutzt `IHostApplicationLifetime.ApplicationStopping` statt Request-CT (mittel-fristig: Queue-BackgroundService).
- [ ] `bare throw new Exception` in den Ollama-Services durch typisierte Exceptions ersetzt (`IsBackendUnavailable`-Klassifikation funktioniert dann zuverlässig).

## Betroffene Bereiche
`McpRequestHandler.cs`, `McpToolHelpers.cs`, `McpEndpoints.cs`, `FindTools.cs`, `ReadTools.cs`, `AnalyzeTools.cs`, `OllamaEmbeddingService.cs`, `OllamaSemanticAnalyzer.cs`, `WebhookEndpoints.cs`.

## Abhängigkeiten
Keine. Verifikation mit MCP-Inspector/Claude Code als Client.

## Definition of Done
MCP-Inspector-Session ohne Protokollfehler (initialize, ping, tools/call mit provoziertem Tool-Fehler); Clamp-Tests grün; Webhook-Push-Szenario indiziert zuverlässig.
