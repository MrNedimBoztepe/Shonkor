# TICKET-210 – MCP Protocol Conformance, Output Clamps, Retry Hygiene

**Severity ref:** H13, M7, M10 · **Effort:** M · **Risk:** low × low

## Context
1. **Tool errors as protocol errors:** `McpRequestHandler.cs:191-195` maps tool exceptions to `-32603` instead of an `isError:true` result — clients hide the actionable message from the model (MCP spec 2025-06-18: execution errors belong in the result).
2. **`ping` missing** (spec: MUST respond) → `-32601`; parse errors return no response at all instead of `-32700` with `id:null`; `initialize` echoes back any requested protocol version (`:133-136`); id-less notifications → 400 over the relay.
3. **Unbounded outputs:** `limit` (`FindTools.cs:43,125`), `hops` (`ReadTools.cs:221`), `maxHops` (`AnalyzeTools.cs:473`) unclamped; `maxChars` defaults to unlimited for `get_source`/`generate_capsule` (`ReadTools.cs:106,329`); `get_subgraph verbose` bypasses the cap (`:250-255`).
4. **Retry hygiene:** both Ollama services retry on cancellation and deterministic 4xx (`OllamaEmbeddingService.cs:87-96`, `OllamaSemanticAnalyzer.cs:128-137`); blocking RAG retries the full generation 3× (up to ~6 min); the webhook background scan captures the request token (`WebhookEndpoints.cs:241-245`) — push indexing can die silently.

## Acceptance Criteria
- [ ] Tool exceptions → `SendResponse(id, { content, isError: true })`; `-32602` stays for parameter validation.
- [ ] `ping` → empty result; parse errors → `-32700` with `id:null`; protocol version validated against the supported set (fallback: default); notifications over the relay → 202 instead of 400.
- [ ] Clamps: `limit ≤ 100`, `hops ≤ 5`, `maxHops ≤ 10`; default output cap 20–40 KB for `get_source`, `get_subgraph verbose`, `generate_capsule` with a "raise maxChars" hint.
- [ ] Retry only on transient errors (`HttpRequestException`/5xx/timeout for embedding; for blocking RAG only connect errors, max. 1 retry), never on triggered cancellation; backoff with jitter — or migrate the typed clients to `Microsoft.Extensions.Http.Resilience`.
- [ ] Webhook task uses `IHostApplicationLifetime.ApplicationStopping` instead of the request CT (medium-term: queue BackgroundService).
- [ ] `bare throw new Exception` in the Ollama services replaced with typed exceptions (`IsBackendUnavailable` classification then works reliably).

## Affected Areas
`McpRequestHandler.cs`, `McpToolHelpers.cs`, `McpEndpoints.cs`, `FindTools.cs`, `ReadTools.cs`, `AnalyzeTools.cs`, `OllamaEmbeddingService.cs`, `OllamaSemanticAnalyzer.cs`, `WebhookEndpoints.cs`.

## Dependencies
None. Verification with MCP Inspector/Claude Code as the client.

## Definition of Done
MCP Inspector session without protocol errors (initialize, ping, tools/call with a provoked tool error); clamp tests green; webhook push scenario indexes reliably.
