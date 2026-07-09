# BUG-004 — All C# citations off by one line (0-based StartLine emitted as 1-based)

**Severity:** High · **Status:** Confirmed · **Area:** Parser / Tool output

## Context

`RoslynAstParser` stores `StartLinePosition.Line` (0-based) in `GraphNode.StartLine` ([RoslynAstParser.cs:128,163,198,234,329](../src/Shonkor.Core/Services/RoslynAstParser.cs)). All output sites (`signature`, `locate`, `find_usages`, `edit_plan`, `implementations_of`, CLI) print the value raw as `file:line` — readers interpret it as 1-based. `CSharpDiagnostics.cs:90`, by contrast, explicitly computes `+ 1 // 1-based for humans/agents`; only `TryReadSourceSlice` ([McpToolContext.cs:109-123](../src/Shonkor.Infrastructure/Services/Mcp/McpToolContext.cs)) treats the values correctly as 0-based. Every line reference for C# symbols points one line above the real declaration — the core currency of a "precise" Graph-RAG.

## Reproduction

Call `signature` for a known class, compare the emitted line with the file → one too low.

## Fix

Establish a convention and document it on `GraphNode.StartLine`/`EndLine`. Recommendation: **store 1-based** (`Line + 1` in the parsers), switch `TryReadSourceSlice` to `-1`. Check all parsers (JS/GraphQL/Markdown — Markdown sets no lines at all today, see BUG-055). No `SchemeVersion` bump needed (IDs unchanged), but a re-index is required for correct values.

## Acceptance Criteria

- [ ] `signature`/`locate`/`find_usages` emit exactly the declaration line (spot-check test over known symbols).
- [ ] The `get_source` fallback (`TryReadSourceSlice`) still returns the correct slice.
- [ ] Convention documented as XML doc on the model; a test that checks parser output against a fixture file with known lines.

## Definition of Done

- Fix + tests merged; note in the CHANGELOG that a re-index is recommended.
