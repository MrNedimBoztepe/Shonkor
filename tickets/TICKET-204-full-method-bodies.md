# TICKET-204 – Store full method bodies + emit `signature` property

**Severity ref:** K2, M9 · **Effort:** S · **Risk:** low × low (DB grows; full re-index required)

## Context
`RoslynAstParser.GetTruncatedContent` (`RoslynAstParser.cs:462-470`) caps method/constructor content at 500 characters. Consequences: `EmbeddingTextBuilder` (MaxBodyChars 1500, head+tail from TICKET-105) never sees more — the tail window is dead for C#; FTS cannot match the second half of a method; `get_source` prefers the stored (truncated) body over the file slice (`ReadTools.cs:91-94`). Additionally, `EmbeddingTextBuilder.cs:39` reads `Properties["signature"]`, but no parser writes the key; class nodes have no content at all (`RoslynAstParser.cs:323-332`).

## Acceptance Criteria
- [ ] Method/constructor nodes store the full body (`ToFullString()`), bounding happens exclusively in `EmbeddingTextBuilder`.
- [ ] `EndLine` is set for methods/constructors.
- [ ] `get_source` falls back to `TryReadSourceSlice` when `Content` ends with a truncation marker (transition case for existing DBs).
- [ ] All Roslyn symbol nodes receive `Properties["signature"]` (modifiers + return type + name + parameter list).
- [ ] Class/interface/record/struct nodes receive a member-signature skeleton as `Content`.
- [ ] Test: method > 500 characters → FTS hit on a string from the second half; `get_source` returns the full body.

## Affected Areas
`RoslynAstParser.cs`, `EmbeddingTextBuilder.cs` (verification only), `ReadTools.cs`, tests.

## Dependencies
None. After merge: full re-index of existing databases (bundle with TICKET-207/208). Measure effect via the TICKET-202 suite (NL retrieval should improve).

## Definition of Done
Tests green; bench before/after documented; DB size growth measured on Shonkor itself and noted in the PR.
