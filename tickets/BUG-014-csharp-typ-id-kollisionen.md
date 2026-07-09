# BUG-014 — C# type-ID collisions: identically named types in one file are merged into a single node

**Severity:** High · **Status:** Confirmed · **Area:** Node-ID scheme / C# parser

## Context

`CsharpNodeId.ForType = "{filePath}::{typeName}"` ([CsharpNodeId.cs:33](../src/Shonkor.Core/Services/CsharpNodeId.cs)) carries neither namespace nor generic arity nor the nesting chain. Collisions:

- `namespace A { class C {} } namespace B { class C {} }` in one file → a single node.
- `class Foo {}` + `class Foo<T> {}` (Identifier.Text loses the arity) → a single node.
- Two classes with an identically named nested `class Builder` → a single node; member IDs collide transitively ([RoslynAstParser.cs:152](../src/Shonkor.Core/Services/RoslynAstParser.cs) uses only the innermost type name).

Last upsert wins → incorrect call hierarchies, incorrect impact/rename results; content/lines of one type overwrite the other. The `CsharpNodeId` remarks document only the partial-type residual ambiguity — these collisions are undocumented.

Related (Medium, BUG-034): record primary constructors produce inconsistent ctor IDs between `RoslynSemantics.ToNodeId` and the parser → dangling `CALLS` edges. Solve alongside the scheme rework.

## Reproduction

Index a fixture file with `namespace A { class C {} } namespace B { class C {} }` → `search_graph C` returns a single node instead of two.

## Fix

Extend the ID with the nesting chain + generic arity (and ideally namespace), e.g. `{file}::{Namespace}.{Outer+Inner}`{`n}`; identical derivation in `RoslynSemantics.ToNodeId` (via `ContainingType` walk). **Bump `SchemeVersion`** (graphs under the old scheme are detected as stale on open → the re-index recommendation kicks in automatically).

## Acceptance Criteria

- [ ] The three collision cases above each produce separate nodes with correct members.
- [ ] The parser side and the semantics side produce identical IDs for the same symbols (round-trip test).
- [ ] `SchemeVersion` incremented; old graphs report a stale state.

## DoD

- Fix + tests merged; re-index mandatory, documented in the CHANGELOG.
