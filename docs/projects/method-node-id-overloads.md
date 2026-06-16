# Concept: Method node-id scheme for overloads (semantic C# core, Task 3)

**Status:** Planned · **Part of:** Semantic C# core (`milestone::41058af4`) · **Tracked in Shonkor:** `get_open_threads`

## Problem
Method (and constructor) node ids are `{filePath}::{TypeName}::{MethodName}` — **no signature**. So overloads collide:
- `Foo(int)` and `Foo(string)` → the same id `file::T::Foo`.
- The parser upserts both with that id → `INSERT OR REPLACE` keeps only one.
- The semantic `CALLS` edges can't say *which* overload is called; `find_usages`/`impact_of`/`call_hierarchy` conflate overloads.

This is a pre-existing limitation of the graph model that semantic `CALLS` merely makes visible.

## Why it's hard (the core constraint)
Nodes are created by the **syntactic** parser (`RoslynAstParser`, no symbol info); edges are created by the **semantic** linker (`SemanticCsharpLinker`, full symbols). For ids to match, **both must derive the same discriminator from their different inputs.**
- A discriminator from **parameter type names** is precise but the two sides disagree: the parser sees source text (`Thing`), the linker sees the resolved symbol (`A.Thing`) — they'd diverge → orphaned edges.
- A discriminator both sides produce **identically** is **arity** (parameter count): the parser counts syntax params, the linker counts symbol params — always equal.

## Decision: phased discriminator
1. **Phase 1 (now, low-risk, high-coverage): arity.** Method/constructor id becomes `{filePath}::{Type}::{Method}#{arity}` (the `Name` stays `Foo` for display; the discriminator is internal to the id and invisible in tool output). Parser and linker agree by construction. Resolves **different-arity** overloads — the common case. Same-arity overloads (`Foo(int)` vs `Foo(string)`) still collide; documented and accepted for v1.
2. **Phase 2 (full precision, later): symbol-derived ids.** Move C# node *extraction* onto the `SemanticModel` (the "D2" path) so the id uses the symbol's canonical form (`GetDocumentationCommentId()` or `Parameters.Select(p => p.Type.ToDisplayString())`). Precise for all overloads, but couples node creation to the compilation (only when `Indexing:SemanticCSharp` is on); the syntactic/default path keeps arity.

Rationale: arity is the only discriminator both the syntactic parser and the semantic linker can produce **identically without** semantic node-extraction — so Phase 1 is safe and self-consistent; Phase 2 buys full precision when we're ready to make node extraction semantic.

## Scope
- Applies to **methods and constructors** only. Properties use the same id shape but can't be overloaded (indexers are a rare exception, ignored for now) — left unchanged.
- The discriminator is **internal** to the id. User-facing resolution stays **name-based**: `ResolveDefinitionAsync` resolves `get_source("Foo")` by name (search), so the tools are unaffected by the id format; addressing a *specific* overload is still by `file:line` (or first-match, as today).

## Migration impact
- **Existing graphs:** all method/constructor ids change → a **full re-index regenerates them** (ids are deterministic; no data-migration script needed). Bump an index/schema version and surface a "re-index recommended" signal on mismatch so stale method nodes/edges (old scheme) don't silently coexist.
- **Handles (`@/…`):** method ids appear as handles in tool output. Handles are ephemeral (per session), so impact is low; a stale handle simply won't resolve.
- **`reindex_file`:** recreates the file's method nodes under the new scheme; cross-file `CALLS` ids must stay consistent — **couples to the drift-remediation project** (the relink uses the new scheme).
- **Backward-compatible UX:** because tool resolution is name-based, the AI-facing behaviour is unchanged; only edge precision improves. Low user-facing risk.

## Work breakdown
1. `CsharpNodeId.ForMember` gains an optional arity; methods/constructors include `#{arity}` (single source of truth used by parser **and** `RoslynSemantics.ToNodeId`).
2. `RoslynAstParser`: pass the syntactic parameter count when building method/constructor ids.
3. `RoslynSemantics.ToNodeId`: append `methodSymbol.Parameters.Length` for methods/constructors.
4. Index/schema version bump + a freshness signal recommending a full re-index on mismatch (ties into drift Layer 0).
5. Tests: two different-arity overloads get **distinct** nodes and `CALLS` to the **correct** one; same-arity collision is asserted as the known v1 limit.
6. Docs: note the scheme + the same-arity caveat; record Phase 2 (semantic node-extraction) as the follow-up.

## Open questions
- Separator: `#{arity}` vs another token — must never appear in a method name (safe) and must round-trip if anything parses ids.
- Same-arity overloads in Phase 1: accept the collision, or add a cheap secondary key (e.g. ordinal of the declaration in the type) that both sides can agree on? (Ordinal is fragile across edits — likely no.)

## Definition of done
- `Foo(int)` and `Foo(int,int)` produce **distinct** method nodes; a call to each yields a `CALLS` edge to the **right** node.
- Same-arity overloads collide, and that is the **only** documented residual ambiguity.
- A scheme/version mismatch surfaces a clear "re-index recommended" signal (no silent old/new id coexistence).
