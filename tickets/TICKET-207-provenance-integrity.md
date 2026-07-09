# TICKET-207 – Enforce provenance integrity

**Severity ref:** K5, M5 · **Effort:** S · **Risk:** low × low (existing graphs keep wrong stamps until re-index — document this)

## Context
`Provenance.cs:7-9` declares the trust tiers the core differentiator ("heuristic sources never claim Extracted"). Violated by: (a) the name-based fallback of `SemanticCsharpLinker` — heuristic, multi-candidate (`SemanticCsharpLinker.cs:168-175`), persisted without provenance (`:120-122`) → default `Extracted`; the linker's edge tuple structurally cannot carry provenance (`:135`); (b) LLM `RELATES_TO` edges via raw SQL without a provenance column (`SqliteGraphStorageProvider.cs:1443-1445`, column default 0 = Extracted); (c) regex parsers `GraphQLParser` and `SitecoreXmCloudPlugin` without a `DefaultProvenance` override (`IFileParser.cs:37`). Additionally, `INSERT OR IGNORE` freezes stale provenance on edge upsert, and `GraphEdge.Properties` is never persisted (`SqliteSchema.cs:60-68`).

## Acceptance Criteria
- [ ] The linker edge tuple carries provenance; the name fallback stamps `Inferred` (unique) / `Ambiguous` (multiple candidates) — analogous to `CrossTechLinker.cs:150`.
- [ ] The `RELATES_TO` insert explicitly sets `Provenance = Inferred`.
- [ ] `GraphQLParser` and `SitecoreXmCloudPlugin` override `DefaultProvenance => Inferred` (structural CONTAINS/DEFINED_IN per-edge Extracted if applicable).
- [ ] Edge upsert: `ON CONFLICT(SourceId,TargetId,RelationType) DO UPDATE SET Provenance = MIN(excluded.Provenance, Provenance)` — an exact resolution may upgrade trust.
- [ ] `GraphEdge.Properties` is persisted as a JSON column and materialized on read (or — if decided against — removed from the model; justify the decision in the PR).
- [ ] Guard test: iterate over all write paths (parser, linker, enrichment, record, plugins) — no path persists `Extracted` unless it is deterministically whitelisted.

## Affected Areas
`SemanticCsharpLinker.cs`, `SqliteGraphStorageProvider.cs`, `SqliteSchema.cs`, `GraphQLParser.cs`, `SitecoreXmCloudPlugin.cs`, tests.

## Dependencies
None. Bundle the re-index note with TICKET-204/208.

## Definition of Done
Guard test green; after a full re-index of Shonkor itself: share of `Extracted` on RELATES_TO/fallback edges = 0 (backed by an SQL sample in the PR).
