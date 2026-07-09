# BUG-003 — Unknown project name silently falls back to the active project (cross-project/cross-tenant leak)

**Severity:** Critical · **Status:** Confirmed · **Area:** MCP / ProjectManager · **Security-relevant**

## Context

`ResolveProject` ([ProjectManager.cs:309-323](../src/Shonkor.Infrastructure/Services/ProjectManager.cs), line 320: `project ??= GetActiveProject();`): If an explicitly requested project name is not found, there is no error — the active project is silently used. The active project is persisted globally and can be changed via the web dashboard.

Consequences: A typo in `projectName`/`X-Project-Name` → responses and `record` **write accesses** end up in the wrong graph. SaaS worst case: the project of a tenant-bound key disappears from the registry between auth and query → the request is redirected to another tenant's active project (data leak).

## Reproduction

`tools/call` with `projectName="Gibtsnicht"` → the result comes silently from the active project.

## Fix

In `ResolveProject` (or at the call boundary in `McpToolContext.GetStorageAsync`): if a name was explicitly requested and resolution fails → error (JSON-RPC `-32602` with a clear message), never a fallback. For tenant-bound sessions, additionally: if resolution of the authenticated tenant fails → hard error, no fallback.

## Acceptance Criteria

- [ ] Unknown explicit `projectName` → error response with the requested name; no access to another graph.
- [ ] Tenant-bound session whose project is missing → error, no fallback.
- [ ] Without an explicit name (session without binding) the previous behavior (active project) is preserved and named in the response.
- [ ] Tests for all three paths.

## Definition of Done

- Fix + tests merged; behavior documented in the tool description (`projectName` parameter).
