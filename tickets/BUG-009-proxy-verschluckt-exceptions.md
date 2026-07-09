# BUG-009 — MCP proxy swallows transport exceptions → host hangs indefinitely

**Severity:** High · **Status:** Confirmed · **Area:** CLI / MCP proxy

## Context

In the exception path of `McpProxyClient`, only stderr is logged, but **no** JSON-RPC response is written to stdout ([McpProxyClient.cs:162-165](../src/Shonkor.CLI/McpProxyClient.cs)) — the MCP host waits forever for the response to the request ID; typical clients thereby block the entire conversation. The non-2xx branch (lines 139-159) correctly synthesizes an error. Affected are network errors, DNS — and above all the **default `HttpClient.Timeout` of 100 s**, which slow tools (`audit`, `generate_capsule` on large graphs) actually hit.

## Reproduction

Stop the backend (or let a tool run > 100 s), send `tools/call` through the proxy → the host never receives a response.

## Fix

In the `catch`: parse the `id` from the request line and emit a `-32603` error response (with a brief cause) to stdout — same mechanics as the HTTP error branch. Make `httpClient.Timeout` configurable, or increase it significantly.

## Acceptance Criteria

- [ ] Every request with an `id` is guaranteed exactly one response — even on timeout, DNS error, connection drop.
- [ ] Notifications (without `id`) still produce no response.
- [ ] Test: backend unreachable → well-formed `-32603` on stdout.

## Definition of Done

- Fix + test merged; timeout configuration documented.
