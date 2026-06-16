# Concept: Method node-id scheme for overloads (semantic C# core, Task 3)

**Status:** Done — Phase 1 (arity discriminator) + Phase 2 (declaration-span discriminator for same-arity overloads). · **Part of:** Semantic C# core (`milestone::41058af4`) · **Tracked in Shonkor:** `get_open_threads`

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
1. **Phase 1 (done): arity.** Method/constructor id becomes `{filePath}::{Type}::{Method}#{arity}` (the `Name` stays `Foo` for display; the discriminator is internal to the id and invisible in tool output). Parser and linker agree by construction. Resolves **different-arity** overloads — the common case.
2. **Phase 2 (done): declaration span.** When a same-arity overload sibling exists, the id additionally appends `@{declarationSpanStart}` — the source offset of the method/constructor declaration. Both sides compute it **identically from the same source text**: the parser from `node.Span.Start`, the linker from `methodSymbol.DeclaringSyntaxReferences[0].Span.Start`. So `Foo(int)` and `Foo(string)` get **distinct** ids and `CALLS` resolves to the right overload — no node collapse, no orphaned edges.

Rationale: we considered the "D2 path" (moving C# node *extraction* onto the `SemanticModel` for symbol-canonical ids like `GetDocumentationCommentId()`). The declaration-span discriminator achieves the same user-visible precision with **no parser/linker divergence and no node-extraction refactor** — the span is a property of the source text, so the two sides cannot disagree. It applies in **both** modes (the parser change is mode-independent; it simply stops same-arity nodes from collapsing). The id is only span-suffixed when a same-arity sibling actually exists, so the common (non-overloaded) method keeps a stable `name#arity` id — important for the incremental edit loop.

**Residual ambiguity:** same-name/same-arity overloads of a **partial** type split across files — the parser sees only one part's members (so treats it as a singleton), while the symbol sees all parts (so adds a span). This is rarer than the same-file same-arity case Phase 2 fixes; full symbol-canonical ids (D2) would close it if it ever matters.

## Scope
- Applies to **methods and constructors** only. Properties use the same id shape but can't be overloaded (indexers are a rare exception, ignored for now) — left unchanged.
- The discriminator is **internal** to the id. User-facing resolution stays **name-based**: `ResolveDefinitionAsync` resolves `get_source("Foo")` by name (search), so the tools are unaffected by the id format; addressing a *specific* overload is still by `file:line` (or first-match, as today).

## Migration impact
- **Existing graphs:** all method/constructor ids change → a **full re-index regenerates them** (ids are deterministic; no data-migration script needed). ✅ Implemented: the persisted `PRAGMA user_version` (= `CsharpNodeId.SchemeVersion`) lets a scan detect the old scheme and **force-reparse** (since unchanged content would otherwise be hash-skipped), and `get_stats` surfaces a "re-index recommended" signal so stale method nodes/edges don't silently coexist.
- **Handles (`@/…`):** method ids appear as handles in tool output. Handles are ephemeral (per session), so impact is low; a stale handle simply won't resolve.
- **`reindex_file`:** recreates the file's method nodes under the new scheme; cross-file `CALLS` ids must stay consistent — **couples to the drift-remediation project** (the relink uses the new scheme).
- **Backward-compatible UX:** because tool resolution is name-based, the AI-facing behaviour is unchanged; only edge precision improves. Low user-facing risk.

## Work breakdown
1. ✅ `CsharpNodeId.ForMethod(filePath, type, method, arity)` → `…::{method}#{arity}` (single source of truth used by parser **and** `RoslynSemantics.ToNodeId`); `ForMember` retained for non-overloadable members (properties).
2. ✅ `RoslynAstParser`: passes `node.ParameterList.Parameters.Count` for methods and constructors.
3. ✅ `RoslynSemantics.ToNodeId`: appends `methodSymbol.Parameters.Length` for methods/constructors.
4. ✅ Scheme-version marker + freshness signal: `CsharpNodeId.SchemeVersion` (now `2`) is persisted per graph via SQLite `PRAGMA user_version`. A full scan **force-reparses** every file when the stored version is older (a scheme change doesn't alter file content, so the hash check would otherwise skip them) and re-stamps the version on completion. `get_stats` exposes `SchemeVersion`/`CurrentSchemeVersion` and a `ReindexRecommended` hint when a non-empty graph is stale.
5. ✅ **Phase 2 — declaration span:** `CsharpNodeId.ForMethod` gains an optional `overloadSpanStart` → `…#{arity}@{span}`. `RoslynAstParser` detects same-file same-arity siblings (`MethodOverloadSpan`/`ConstructorOverloadSpan`) and passes `node.Span.Start`; `RoslynSemantics.ToNodeId` mirrors it from the resolved symbol's declaring syntax (`OverloadSpan`). `SchemeVersion` bumped to **3**.
6. ✅ Tests: different-arity overloads get **distinct** nodes + correct `CALLS` (`DifferentArityOverloads_…`); **same-arity** overloads now also get **distinct** nodes + correct `CALLS` (`SameArityOverloads_GetDistinctNodes_AndCallResolvesToTheRightOne`, `ToNodeId_SameArityOverloads_GetDistinctIds_ViaDeclarationSpan`).
7. ✅ Docs: scheme + the (now narrow) partial-class residual documented here and in `CsharpNodeId` XML doc.

## Resolved questions
- Separator: `#{arity}` plus `@{span}` — neither token appears in a method name, so ids are unambiguous.
- Same-arity discrimination: solved by **declaration span** (a deterministic key both sides derive from the same source text), not ordinal. Span is only appended when a same-arity sibling exists, so non-overloaded methods keep a stable id; the edit-fragility concern is therefore confined to actual overload groups (and re-index regenerates all ids consistently).

## Definition of done
- ✅ `Foo(int)` and `Foo(int,int)` produce **distinct** method nodes; a call to each yields a `CALLS` edge to the **right** node.
- ✅ `Foo(int)` and `Foo(string)` (same arity) now also produce **distinct** nodes with correct `CALLS`; the **only** residual ambiguity is same-arity overloads of a **partial** type split across files.
- ✅ A scheme/version mismatch surfaces a clear "re-index recommended" signal and force-reparses (no silent old/new id coexistence).
