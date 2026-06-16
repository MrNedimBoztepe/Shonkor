# Project: Semantic C# core ("A-minus") — Roslyn SemanticModel, exact edges, CALLS

**Status:** Planned · **Owner:** TBD · **Tracked in Shonkor (Brain graph):** see `get_open_threads`

## Thesis
Make the C# core **semantically exact** by resolving references through a Roslyn `CSharpCompilation` + `SemanticModel`, replacing today's **name-based** resolution. This (1) closes the "Precision GraphRAG" credibility gap, (2) unlocks `CALLS` / `call_hierarchy` as a by-product, and (3) stays **self-contained** — Roslyn is already a dependency, no new runtime. Deliberately does **not** chase language breadth; depth on the one language that's already first-class.

## Current state (what exists)
- `RoslynAstParser` (Core/Services): **purely syntactic**, per-file (`CSharpSyntaxTree.ParseText`). Emits type/method/property nodes + `CONTAINS`, plus `IMPLEMENTS`/`EXTENDS` edges whose **TargetId is the base type NAME** (string), and a `referencedTypes` property (comma-separated NAMES collected syntactically).
- `CrossTechLinker` (Infrastructure/Services): post-scan pass that resolves `referencedTypes` **by name** against definition nodes → `REFERENCES_TYPE`. Also does the cross-tech links (BINDS_TO/CONTROLLER_OF/QUERIES_TEMPLATE/BELONGS_TO_MODULE).
- No compilation, no `SemanticModel`, no metadata references.
- **Problem:** name matching is ambiguous (two `Foo` in different namespaces → wrong/duplicate edges); `IMPLEMENTS`/`EXTENDS` point at names, not symbols. The engine behind "Precision" is a heuristic.

## Design

### D1 (chosen): keep the parser for nodes, add a semantic linker for edges
Semantic analysis is inherently **whole-project** (needs the compilation), so it doesn't fit the per-file `IFileParser` contract. Least-invasive design:
- **Keep `RoslynAstParser`** for fast per-file node extraction (types/members) + `CONTAINS`.
- **Add `SemanticCsharpLinker`** (post-scan, like `CrossTechLinker`): build the compilation over all C# files, and per file use the `SemanticModel` to emit the **semantic** edges:
  - base types/interfaces → exact `INamedTypeSymbol` → `IMPLEMENTS`/`EXTENDS` (to **node ids**, not names);
  - type references (fields, params, returns, generics, `new`, locals) → exact symbols → `REFERENCES_TYPE` (correct across namespaces/overloads);
  - invocations → `IMethodSymbol` → `CALLS` (caller method node → callee method node) [= `call_hierarchy`].
- **Retire** the C# name-matching in `CrossTechLinker`; keep it for the non-C# / cross-tech parts (JS/PHP/GraphQL/Sitecore have no SemanticModel).

(D2 — a full semantic indexer replacing the parser — is more invasive and abandons the per-file model for C#; rejected for risk.)

### The metadata-references question (the genuinely fiddly bit)
A `CSharpCompilation` needs references. **Key insight that dissolves most of the anxiety:** for impact analysis *within the indexed codebase*, you do **not** need the project's NuGet references — the codebase's own types are all in the compilation's syntax trees, so the `SemanticModel` resolves intra-codebase references correctly even with minimal external references. References only matter for resolving edges *into* external symbols, which Shonkor doesn't graph anyway.
- **R1 (first):** reference the .NET ref-assembly pack + the loaded AppDomain assemblies (exactly what `PluginLoader` already does). No build, no NuGet. Intra-codebase edges resolve fully; edges into un-referenced third-party types are skipped (acceptable — they have no nodes). Document the limitation.
- **R2 (opt-in "deep" mode, later):** resolve the target's references from `obj/project.assets.json` / a design-time build. Accurate incl. NuGet, but reintroduces a build dependency (friction, SDK-version fragility).

### Symbol → node-id mapping
Map a resolved symbol to a Shonkor node id via `symbol.DeclaringSyntaxReferences[0]` → SyntaxTree.FilePath + the type/member name path, reproducing the parser's id scheme (`{filePath}::{Type}` / `{filePath}::{Type}::{Member}`). Symbols with no declaring syntax in the indexed set (external/metadata) → skipped.
- **Overload collision (resolved):** method node ids now encode `#{arity}` and, for same-arity overloads, `@{declarationSpan}`, so `Foo(int)` and `Foo(string)` get distinct nodes and `CALLS` resolves to the right one. See [method-node-id-overloads.md](method-node-id-overloads.md) (Phase 1 + 2, scheme version 3, force-reparse on drift). Residual: same-arity overloads of a partial type split across files.

## Performance & drift coupling
Building a compilation is O(repo) and cannot run on every single-file `reindex_file`. So the semantic linker is a **whole-graph / full-scan concern** — the **same shape** as cross-tech and `CALLS`. This couples directly to the **drift-remediation project**: a single-file edit must **rebind the changed file + its dependents** (Roslyn supports incremental compilation: reuse the compilation, swap one syntax tree). The three projects (semantic-core, call_hierarchy, drift) are one coherent thrust; **semantic-core is the foundation**, `CALLS` falls out of it, and drift must maintain its edges incrementally.

## Work breakdown
1. **Spike:** compilation over a sample project (R1 refs) → resolve a base type, a type reference, and an invocation to symbols → map each to the parser's node id → validate round-trip (incl. the overload-id collision).
2. **`SemanticCsharpLinker`** post-scan pass emitting `IMPLEMENTS`/`EXTENDS`/`REFERENCES_TYPE`/`CALLS` from the `SemanticModel`; replaces C# name-matching.
3. ✅ **Method node-id scheme** — arity + declaration-span discriminator resolves overloads (incl. same-arity); scheme version persisted + force-reparse on drift. See [method-node-id-overloads.md](method-node-id-overloads.md).
4. **Metadata references R1** (ref-assemblies + AppDomain), documented limitation; R2 (assets.json deep mode) as opt-in later.
5. **Retire `referencedTypes` name-matching for C#** in `CrossTechLinker`; keep it for non-C#; drop or keep the property accordingly.
6. **Wire into `GraphIndexScanner`** (full scan) + coordinate with drift for incremental (incremental compilation: reuse + swap tree + rebind dependents).
7. **Config gate** (`Indexing:SemanticCSharp`, on by default; opt-out for very large repos / perf).
8. **Tests:** same-name types in different namespaces resolve correctly; overloads; cross-namespace `REFERENCES_TYPE`; `CALLS` caller/callee; a regression that the old ambiguity is gone.
9. **Docs/positioning:** make the "Precision" claim true; concept + tool reference updates.

## Open questions / risks
- ✅ **Method node-id change** (arity + span) — resolved via a persisted scheme version (`PRAGMA user_version`) that force-reparses stale graphs and surfaces a `ReindexRecommended` hint, so no migration script is needed.
- **Incremental semantic relink:** how much to rebind on a single-file edit (file + dependents)? Couples to drift.
- **R1 completeness:** is skipping edges into un-referenced externals acceptable? (Likely yes — no nodes for them.)
- **Perf budget** on very large monorepos; when to fall back to syntactic-only.

## Definition of done
- Same-name types in different namespaces produce **correct, non-duplicated** `REFERENCES_TYPE`/`IMPLEMENTS`/`EXTENDS` edges (to node ids).
- `CALLS` edges connect caller→callee methods accurately on a sample project.
- The C# path no longer relies on name-matching; the "Precision GraphRAG" claim is backed by semantic resolution.
- Stays self-contained (no new runtime; R1 references, no mandatory build).
