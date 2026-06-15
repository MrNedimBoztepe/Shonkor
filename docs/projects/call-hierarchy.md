# Project: True `call_hierarchy` (semantic CALLS edges)

**Status:** Planned · **Owner:** TBD · **Tracked in Shonkor (Brain graph):** `milestone::253712aa` (+ `decision::2ef4c302`, 7 tasks, 2 questions — all queryable via the `get_open_threads` MCP tool)

## Goal
Add a method-level `call_hierarchy` MCP tool that answers **"who calls method X?"** (callers) and **"what does X call?"** (callees) across files, to a configurable depth. This requires the indexer to emit **`CALLS`** edges (caller method → callee method).

## Why
The current graph is **type/reference-level**: the syntactic Roslyn parser emits `CONTAINS`, `IMPLEMENTS`/`EXTENDS`, and `REFERENCES_TYPE` — but **no `CALLS` edges** (confirmed: `EdgesByRelation` has no `CALLS`). The Tier-3 `dependency_tree` approximates control flow at the type level, but cannot answer method-call questions. A real call hierarchy is a top dev-acceleration capability: understanding control flow, and making method-signature refactors safe.

## Why it's hard
`RoslynAstParser` is **purely syntactic** — one file at a time via `CSharpSyntaxTree`, no symbol resolution. A method invocation (`InvocationExpressionSyntax`) is easy to see syntactically, but resolving it to the **defining** method needs a **`SemanticModel`** backed by a `CSharpCompilation` over the whole project **with metadata references**.

## Approach
- **A) Compilation-based linker (recommended).** Build a `CSharpCompilation` from all `.cs` files + metadata references; for each `InvocationExpression`, use `SemanticModel.GetSymbolInfo()` to resolve the target `IMethodSymbol`, map it to the existing method node id (`{filePath}::{type}::{method}`), and emit a `CALLS` edge. Runs as a new post-scan pass (like `CrossTechLinker`). Most accurate; heavier (needs references; slower).
- **B) Heuristic name-matching (fallback).** Match invoked names syntactically against indexed method nodes by name. Fast, no compilation, but ambiguous (overloads, same-named methods) → false edges. Use only where A can't resolve.

## Work breakdown
1. **Spike** — build a `CSharpCompilation` over a project, resolve a sample `InvocationExpression` to its `IMethodSymbol`, and map that symbol back to a Shonkor method node id. Validate the id round-trip.
2. **Indexer** — add a `SemanticCallLinker` post-scan pass emitting `CALLS` edges (caller → callee). Config-gated (compilation is expensive).
3. **Storage/model** — `CALLS` is just a relationship; no schema change. Decide the single-file `reindex_file` story (calls span files; like cross-tech, treat as a whole-graph concern recomputed on full scan).
4. **MCP tool** — `call_hierarchy(symbol, direction=callers|callees, depth)` traversing `CALLS` (reuse the `dependency_tree` traversal restricted to `CALLS`). Optionally fold `CALLS` into `impact_of` / `find_usages`.
5. **Tests** — unit (compilation→symbol→edge), integration (small project, callers/callees resolved), MCP tool output.
6. **Perf** — measure compilation cost on a large repo; cache the compilation; gate behind config; document.
7. **Docs** — tool reference + a concept section; remove the `dependency_tree` "no call edges" caveat once shipped.

## Open questions / risks
- **Metadata references**: a `CSharpCompilation` needs the project's referenced assemblies. How to obtain them without a full MSBuild build? (Best-effort against the running .NET ref assemblies + restore output, or require a build.)
- **Performance** on large monorepos — gate + cache the compilation.
- **Incremental reindex**: editing one file changes callers/callees in *other* files; like cross-tech links, `CALLS` is a whole-graph concern that single-file `reindex_file` can't fully maintain.
- **Language scope**: C# first; JS/PHP call graphs are separate efforts.

## Acceptance
`call_hierarchy("FindPathAsync", direction="callers")` returns the methods that call it across files, to a configurable depth, verified against a known sample — with zero false edges on the sample set.
