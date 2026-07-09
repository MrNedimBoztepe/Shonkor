# TICKET-214 – Switch MCP `generate_capsule` to the Budget Synthesizer

**Severity ref:** H10 · **Effort:** S · **Risk:** low × low

## Context
The MCP tool `generate_capsule` (`ReadTools.cs:317-335`) seeds FTS-only (`SearchAsync(query, 5)`), calls the **legacy overload** `Synthesize(nodes, edges)` without `CapsuleOptions`, and then blindly truncates at the last `##` before `maxChars` (`McpToolHelpers.cs:186-202`). The unlimited renderer groups alphabetically by file path (`ContextCapsuleSynthesizer.cs:169`) — under truncation the very seed nodes that matched the query get dropped. The web endpoints do it right (`SearchEndpoints.cs:359-360`: `SeedIds`, `MaxContentChars=12000`, `MaxNodes=40` — seeds first, always complete, hub protection). The tool that agents actually call gets none of that.

## Acceptance Criteria
- [ ] `GenerateCapsuleTool` passes `CapsuleOptions { SeedIds, MaxContentChars = maxChars > 0 ? maxChars : 12000, MaxNodes = 40 }`.
- [ ] Seeding uses the hybrid path when an embedding backend is available (fallback to FTS as before).
- [ ] The downstream `TruncateAtBoundary` truncation is removed for the budgeted path (budget is already enforced), or kept only as a safety net.
- [ ] Test: query with an alphabetically late-sorted seed + a small `maxChars` → seed body complete in the capsule (fails on the old code).
- [ ] Bench: seed-survival rate (TICKET-202) for the MCP path == web path.

## Affected Areas
`ReadTools.cs`, possibly `McpToolHelpers.cs`, tests.

## Dependencies
None. Measurability via TICKET-202 (seed survival).

## Definition of Done
Test green; a sample with a real agent query documented (capsule contains seeds in full, omission notice instead of silent truncation).
