# Concept: Language breadth (deeper graphs for more languages)

> **DECISION (2026-07-04) — superseded to Option B (Tree-sitter breadth).** The roadmap-synthesis
> review revised the earlier "A-minus / deepen-C#" default: we WILL pursue Tree-sitter-backed
> syntactic breadth as a plugin family, on the explicit condition that Tree-sitter-derived edges are
> tagged **`INFERRED`** (never `EXTRACTED`), so the precision/provenance moat stays intact even as
> coverage widens. Prerequisite: the provenance model (Phase 0.1/0.2) lands *first* — breadth before
> provenance would dilute the graph's trust signal. The depth-vs-breadth analysis below remains valid
> as the rationale for *why the INFERRED tag is non-negotiable*; only the go/no-go changed.

**Status:** Scoping (no code yet) · **Tension:** runs against the session's "A-minus / deepen C#" decision — read this before committing. · **Tracked in Shonkor (Brain graph):** `get_open_threads`

## Problem / goal
C# now has a **deep, semantic** graph (exact `REFERENCES_TYPE`/`IMPLEMENTS`/`EXTENDS`/`CALLS`, drift-maintained, `call_hierarchy`). The other languages are **shallow**:
- **JS/TS** (`JavaScriptParser`): imports, React components, backend API surface — no call graph, no type resolution.
- **PHP** (`PhpModuleParser`): regex module/Smarty extraction (OXID-specific).
- **Sitecore YAML / GraphQL / Markdown**: structural only.

"Language breadth" = decide whether/how to bring *semantic depth* (call graphs, exact references) to more languages, so the AI-acceleration story isn't C#-only.

## The strategic tension (don't skip)
Earlier this session we deliberately chose **A-minus**: deepen C# semantically and **stay self-contained**, rather than broaden shallowly. The reasons still hold:
- **Tree-sitter gives syntax, not semantics.** It would add more languages quickly but only at the *syntactic* level (the depth JS/PHP already have). It does **not** resolve calls/types across files — so no real `CALLS`/exact-`REFERENCES_TYPE`. It widens coverage without deepening it; the headline differentiator (precise call graph) wouldn't extend.
- **Real per-language semantics is per-language work.** TS needs the TypeScript compiler API; PHP needs a real PHP parser/resolver; Python needs Jedi/pyright. Each is a separate, sizable integration with its own "metadata references" problem (the same constraint C# had).
- **Self-contained is the moat.** Pulling in LSP servers / language toolchains reintroduces the build/runtime dependency we avoided for C# (R1 ref-assemblies), and starts competing with Sourcegraph-style tools.

So "breadth" is genuinely a **different bet** than the depth bet we've been executing.

## Options
- **A) Hold / decline (recommended default).** Keep depth in C#; keep the others shallow-but-useful (cross-tech linking already connects Next.js ↔ Sitecore ↔ C# ↔ GraphQL). Spend the next effort *deepening C# further* (see "Aligned alternatives") where ROI compounds on what exists.
- **B) Tree-sitter breadth (syntactic).** Add a tree-sitter-backed parser path for N languages → consistent syntactic graphs (types, methods, containment) but **no** semantic call/reference resolution. Cheap per language, shallow result. Good only if the goal is *coverage* for navigation, not precise impact analysis.
- **C) One more deep language (e.g. TypeScript).** Pick the highest-value second language and do it *properly* (TS compiler API → exact imports/refs/calls → reuse the `CALLS`/reverse-index/drift machinery, which was deliberately kept generic). Biggest effort, but extends the real differentiator. TS is the natural pick (largest overlap with the existing JS parser + Next.js/Sitecore cross-tech).

## Aligned alternatives (cheaper wins that compound on C# depth)
If the actual intent is "more value from the AI graph," these beat breadth on ROI and fit the chosen strategy:
1. **Fold `CALLS` into `impact_of` / `find_usages`** — method-level impact ("what breaks if I change this method?"), not just type-level. Small, high-value, reuses existing edges.
2. **More C# semantic edges** — `OVERRIDES`, interface-member implementation, event/property-accessor calls — richer control-flow/impact precision.
3. **Tooling polish** — `record_*` upsert-by-id (today it creates duplicate nodes; stale Done/Todo dupes accumulate in `get_open_threads`).

## Recommendation
Default to **A** (hold breadth) and take an **Aligned alternative** next — specifically #1 (`CALLS` into `impact_of`/`find_usages`), which turns the new call graph into precise method-level impact analysis. Revisit **C (TypeScript, deep)** only as a deliberate second-language investment when C# depth has been fully mined.

## Open questions
- Is the goal *coverage* (navigate more languages) or *depth* (precise impact in more languages)? They lead to opposite options (B vs C).
- Which second language carries real user demand — TS, Python, PHP? (TS has the most synergy here.)
- Are we willing to reintroduce a per-language toolchain dependency for depth (the self-contained trade-off)?
