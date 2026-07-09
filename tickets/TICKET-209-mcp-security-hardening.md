# TICKET-209 – MCP Security: Path Containment, set_project Session, AllowLocalBypass, record Hardening

**Severity ref:** H8, H9, M11, M12 · **Effort:** M · **Risk:** low × medium (containment may break out-of-root workflows — the error message names the root)

## Context
1. **Path Traversal:** All five file-accepting tools (`reindex_file`, `check_edit`, `outline`, `freshness`, `review` — pattern `EditLoopTools.cs:46-50`) pass absolute paths through unchecked; `FromHandle` (`McpToolHelpers.cs:174-180`) concatenates `@/../…` without normalization. No check against the project root. Chain: `reindex_file` indexes arbitrary files → `search_graph`/`get_source` exfiltrate them.
2. **set_project has no effect over the HTTP relay:** a new handler/context per POST (`McpEndpoints.cs:68-70`); `SessionProjectOverride` (`MetaTools.cs:118`) dies with the request; the stdio proxy sends every line as its own POST. The agent gets "success" and works on the wrong graph.
3. **`Security:AllowLocalBypass=true`** re-enables the loopback bypass in production — behind a reverse proxy every request is 127.0.0.1 (`ApiKeyMiddleware.cs:26-43`).
4. **record:** unbounded content length, edges to unverified `connectedNodeIds` (`McpToolContext.cs:226-241`), a persistent cross-session injection channel; exception messages in `-32603` can leak paths.

## Acceptance Criteria
- [ ] Shared helper `ResolveContainedPath(raw, basePath)`: `GetFullPath` + `StartsWith(basePath + separator, OrdinalIgnoreCase)`; `..` in handles rejected; error message names the allowed root. Used in all five tools; `TryReadSourceSlice` checks as well.
- [ ] `set_project` over the relay: either a real session store (`Mcp-Session-Id` header, server-side held override) **or** an explicit error "not supported over the HTTP relay — set X-Project-Name / projectName per call". No more false success.
- [ ] Loopback bypass only when the flag **and** `env.IsDevelopment()`; otherwise a loud startup error/warning; `/api/mcp` exempted from the bypass like `/api/rag`.
- [ ] `record`: content length capped (e.g. 8 KB), `connectedNodeIds` validated against existence (no dangling edges), content fenced as data in `get_open_threads`/`orient`.
- [ ] Exception messages in relay responses generic; details only to the server log.
- [ ] Tests: traversal attempts (`C:\Windows\...`, `@/../../x`) → error; relay double-POST with set_project → documented behavior.

## Affected Areas
`McpToolHelpers.cs`, `EditLoopTools.cs`, `ReadTools.cs`, `MetaTools.cs`, `McpEndpoints.cs`, `McpToolContext.cs`, `ApiKeyMiddleware.cs`, tests.

## Dependencies
None. Mergeable independently of TICKET-210.

## Definition of Done
Traversal tests green; manual verification with an MCP client over the relay (set_project behavior) and stdio (still functional unchanged).
