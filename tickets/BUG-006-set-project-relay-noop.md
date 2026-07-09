# BUG-006 — `set_project` is a silent no-op over the HTTP relay but reports success

**Severity:** High · **Status:** Confirmed · **Area:** MCP relay

## Context

The relay builds a new `McpRequestHandler`/`McpToolContext` **per POST** ([McpEndpoints.cs:68-70](../src/Shonkor.Web/Endpoints/McpEndpoints.cs)). `SetProjectTool` sets `ctx.SessionProjectOverride` ([MetaTools.cs:117-121](../src/Shonkor.Infrastructure/Services/Mcp/Tools/MetaTools.cs)) on this request-local object and replies "Active project for this session is now 'X'" — the state is gone with the request. The client believes the switch happened and subsequently reads/writes (`record`) into the wrong graph. Combined with BUG-003, this is especially treacherous.

## Reproduction

Via `shonkor mcp-proxy`: `set_project name=Foo`, then `get_stats` → statistics of the old/active project.

## Fix

Either (a) make `set_project` honest over the relay: when no persistent session exists, return a clear error "not supported over the HTTP relay — pass `projectName` per call"; or (b) persist sessions via an `Mcp-Session-Id` header (context cache keyed by session ID, with TTL). Variant (a) is the safe immediate fix.

## Acceptance Criteria

- [ ] `set_project` over the relay no longer claims a success it cannot deliver.
- [ ] Over stdio (persistent session), `set_project` works unchanged.
- [ ] Test: relay call `set_project` + follow-up tool → either an error message (a) or the correct project (b).

## Definition of Done

- Fix + test merged; `set_project` tool description adjusted accordingly.
