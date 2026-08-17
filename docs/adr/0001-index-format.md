# ADR 0001 — Keep shonkor's own graph schema; do not adopt SCIP as the persistence format

- **Status:** Accepted
- **Date:** 2026-08-17
- **Deciders:** Nedim Boztepe (stakeholder)
- **Context source:** Phase-0 verification run, question F6 (prior art)

## Context

shonkor persists its code graph in a bespoke SQLite schema (`Nodes`, `Edges`, `TypeReferences`,
`Diagnostics`, `Meta`; see `SqliteSchema`). Every edge carries a `Provenance` trust tier
(`Extracted` / `Inferred` / `Ambiguous`, see `Shonkor.Core/Models/Provenance.cs`) — the property the
product is built around.

Before extending that schema with a typed `ProvenanceReason`, the question was raised whether an
established code-intelligence index format could carry the model instead, which would remove a large
part of the schema- and migration-maintenance burden. The candidate was **SCIP** (Sourcegraph),
together with its .NET indexer `scip-dotnet`; LSIF, CodeQL, NDepend/CQLinq, Stack Graphs and Glean
were surveyed as context.

Prior to this ADR the repository contained **no** discussion of any of these formats — a `grep` over
`docs/`, `review/`, `bench/`, `README.md` and `CHANGELOG.md` for SCIP/LSIF/CodeQL/NDepend/Stack
Graphs/Glean/Sourcegraph returned zero hits. The question had never been answered in writing.

## Decision

**shonkor keeps its own schema.** SCIP is not adopted as the persistence format.

This is a decision about *persistence*. It does not decide the separate question of whether SCIP is
useful as an *import or export boundary*; that is tracked in its own issues and is explicitly out of
scope here.

## Evidence

All findings below were verified against primary sources on 2026-08-17. `scip.proto` was fetched
twice from `https://raw.githubusercontent.com/sourcegraph/scip/main/scip.proto` with two different
extraction prompts; the results agreed.

### E1 — SCIP has no extension mechanism

`scip.proto` is `syntax = "proto3"`, `package scip;`. It contains:

- no `map<...>` field anywhere,
- no `google.protobuf.Any`,
- no `extend` blocks and no `extensions` ranges,
- exactly one `reserved` declaration (`reserved 1, 3, 6;` in `Signature`),
- no free-form string or bytes field on `Occurrence` or `SymbolInformation` that is not positional
  or documentation.

There is therefore no conforming place to put a provenance tier or reason.

### E2 — the relation model is a closed set of four booleans

```protobuf
message Relationship {
  string symbol = 1;
  bool is_reference = 2;
  bool is_implementation = 3;
  bool is_type_definition = 4;
  bool is_definition = 5;
  // Update registerInverseRelationships on adding a new field here.
}
```

The trailing comment shows the set is protocol-maintained: extending it is a protocol change, not a
consumer-side extension.

### E3 — roughly a third of the edge inventory has no SCIP representation

Measured against a cold full scan of a real Sitecore solution (`sitecoreMuM`, 7 983 files,
52 900 edges) produced by the current code:

| shonkor edge type | count | SCIP representation |
|---|---:|---|
| `REFERENCES_TYPE` | 11 056 | `Occurrence` + `is_type_definition` |
| `CONTAINS` | 14 043 | `enclosing_symbol` / document structure |
| `IMPLEMENTS` / `EXTENDS` | 3 078 | `is_implementation` |
| `IMPLEMENTS_MEMBER` / `OVERRIDES` | 5 090 | `is_implementation` (+ `is_reference`) |
| `CALLS` | 4 331 | **derivable only, not typed** |
| `INSTANTIATES` | 730 | **none** |
| `BELONGS_TO_MODULE` / `BELONGS_TO_CONCEPT` | 13 281 | **none** |
| `REGISTERS_PROCESSOR` / `_SERVICE` / `_CONFIGURATOR`, `HANDLES_EVENT`, `RESOLVES_TO` | 273 | **none** |
| `RELATES_TO` (LLM concept links, live graph) | 28 026 | **none** |

About **35 %** of the inventory (18 615 of 52 900) has no representation at all — and the unrepresented
part is not a random slice. It is precisely the differentiating one: the CMS-specific registration
edges, the module/concept layer, and the edges planned in AP5 (DI resolution, ASP.NET routing, the EF
model, the MSBuild project graph), none of which any standard format models.

### E4 — `CALLS` is not recoverable from a real scip-dotnet index

SCIP can in principle express a call as a reference occurrence located inside the `enclosing_range`
of a method definition. Measured on actual indexes produced by `scip-dotnet` v0.2.14:

| index | documents | occurrences | occurrences carrying an enclosing-range field |
|---|---:|---:|---:|
| `Shonkor.Core` + `Shonkor.Infrastructure` | 87 | 22 405 | **0** |
| `sitecoreMuM` | 650 | 66 766 | **0** |

`enclosing_range` is never populated, so the derivation is not available in practice. Recovering
`CALLS` would mean re-running Roslyn over the sources — which is what shonkor already does.

### E5 — scip-dotnet does not cover the target codebase

On `sitecoreMuM`, `scip-dotnet` indexed **650 of 1 620 C# files (40 %)**. 65 of the solution's 156
projects failed to load, every one of them with the same MSBuild error: the classic .NET Framework
web projects import `$(VSToolsPath)\WebApplications\Microsoft.WebApplication.targets`, which the .NET
SDK alone does not provide.

The failure mode matters as much as the number: the tool exits **0** and writes an index anyway. An
earlier run of the same command produced a 164-byte index containing zero documents, also with exit
code 0. shonkor's own source-only scan covers 100 % of the same files without a build.

### E6 — the index does not reliably identify its producer

Both indexes report `ToolInfo` as `scip-dotnet 0.1.0-SNAPSHOT`, although the binary is v0.2.14. Even
the one metadata field that could have anchored trust in a producing tool version is unreliable here.

## Consequences

### Accepted

- Adopting SCIP would require a side channel for provenance (E1, E2) **and** a second side channel for
  ~35 % of the edge inventory (E3). The result is still a bespoke schema — now in addition to SCIP
  rather than instead of it. That is strictly worse than the status quo.
- The migration cost is not a format swap. `SqliteSchema`, `SqliteRowMapper`,
  `SqliteGraphStorageProvider` (~1 700 lines), all 34 MCP tools (via `GraphEdge`/`Provenance`), all 12
  parsers and 4 plugin assemblies (via `IFileParser`), plus the FTS and embedding columns SCIP has no
  notion of, would all be touched.
- shonkor keeps full control over its own edge types, which is what makes AP5 possible at all.

### Costs we take on knowingly

- No interoperability with Sourcegraph/Glean out of the box.
- We maintain our own extraction for the base layer that `scip-dotnet` would otherwise provide —
  although E4/E5 show that layer would have been incomplete for our target codebases anyway.
- We own our schema migrations.

### Related decisions, deliberately not taken here

- **SCIP import** for the languages where shonkor's own parsers are weakest (JS/TS) is a separate
  question with a different answer, tracked separately.
- **SCIP export** of the representable subset is tracked as a backlog item, blocked until a concrete
  consumer exists.
- **A Datalog engine of our own** (the AP4 "variant B" option) is closed by this survey: Glean already
  provides a Datalog-like query language (Angle) over code facts, including a `scip-to-glean`
  converter and a `glean index dotnet-scip` path. Building our own is not justified.

## Sources

- `scip.proto`: https://raw.githubusercontent.com/sourcegraph/scip/main/scip.proto
- scip-dotnet: https://github.com/sourcegraph/scip-dotnet (API: `archived: false`, `pushed_at`
  2026-08-17, latest release v0.2.14 of 2026-05-05)
- CodeQL C# library: https://codeql.github.com/docs/codeql-language-guides/codeql-library-for-csharp/
- CodeQL build modes: https://docs.github.com/en/code-security/code-scanning/creating-an-advanced-setup-for-code-scanning/codeql-code-scanning-for-compiled-languages
- NDepend analysis inputs: https://www.ndepend.com/docs/ndepend-analysis-inputs-explanation
  ("The bulk of data used by NDepend comes from assemblies themselves" — NDepend requires a build,
  shonkor does not)
- Stack graphs: https://github.blog/open-source/introducing-stack-graphs/
- Glean: https://glean.software/docs/introduction/ and https://glean.software/docs/indexer/scip-dotnet/
