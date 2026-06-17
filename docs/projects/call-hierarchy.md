# Project: True `call_hierarchy` (semantic CALLS edges)

**Status:** ✅ SHIPPED. The `CALLS`-emitting indexer (semantic core) and incremental maintenance (drift P3) were built first; this project added the `call_hierarchy` MCP tool on top. · **Tracked in Shonkor (Brain graph):** `milestone::253712aa`

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
1. ✅ **Spike** — `SemanticCsharpSpikeTests` (compilation → symbol → node id round-trip).
2. ✅ **Indexer** — `SemanticCsharpLinker` emits `CALLS` (caller → callee), config-gated by `Indexing:SemanticCSharp`.
3. ✅ **Storage/model** — `CALLS` is just a relationship (no schema change). Single-file story solved by drift P3 (incremental semantic relink on reconcile batches).
4. ✅ **MCP tool** — `call_hierarchy(symbol, direction=callers|callees, depth)` traversing `CALLS`, reusing the `dependency_tree` indented-walk with cycle marking.
5. ✅ **Tests** — `CallHierarchy_ResolvesCallersAndCallees_OverCallsEdges` (tool), plus the linker/spike CALLS tests; an **extension-method** regression (`ExtensionMethod_CallsEdge_ResolvesToUnreducedArity`).
6. ✅ **Perf** — a singleton `SemanticCompilationCache` holds the per-directory compilation and swaps only the edited tree, so incremental reconciles and the semantic `reindex_file` reuse it instead of rebuilding (an O(repo) parse) per edit.
7. ✅ **Docs** — tool reference (README, llm_integration) + arc42 8.6.

## Resolved questions
- **Metadata references** — solved via the runtime ref-assembly set (R1, TPA list); no MSBuild build needed.
- **Incremental reindex** — solved by drift P3 (reconcile batches relink the changed files + referencers semantically).
- **Extension methods** — a reduced extension-method call symbol drops the `this` parameter; `RoslynSemantics.ToNodeId` unreduces (`ReducedFrom`) so the arity matches the parser's node id (caught live during the first FPM call_hierarchy query).

## Acceptance — met
`call_hierarchy` resolves callers/callees across files to a configurable depth; validated live on FPM (e.g. `ConfigureServices` → its real registration calls), with extension-method calls resolving to the correct method node.
