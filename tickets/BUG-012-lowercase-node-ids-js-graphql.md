# BUG-012 — JS/GraphQL parsers produce lowercased node IDs → entire edge families point into the void

**Severity:** High · **Status:** Confirmed · **Area:** Parser (JS/TS, GraphQL)

## Context

`JavaScriptParser` ([JavaScriptParser.cs:48,119](../src/Shonkor.Core/Services/JavaScriptParser.cs)) and `GraphQLParser` ([GraphQLParser.cs:48](../src/Shonkor.Core/Services/GraphQLParser.cs)) build node IDs with `filePath.ToLowerInvariant()`; the scanner creates file nodes with the original-case path ([GraphIndexScanner.cs:169](../src/Shonkor.Infrastructure/Services/GraphIndexScanner.cs)). `Nodes.Id` is case-sensitive:

- Windows paths (`C:\Projects\…`): **all** `IMPORTS` and `DEFINED_IN` edges point to IDs without nodes — the JS/GraphQL subgraphs are structurally dead.
- Fully lowercased paths (Linux): the component ID collides with the file-node ID; the winner is nondeterministic due to `ConcurrentBag` ordering — if the file node loses, its `ContentHash` is gone and the file is re-indexed on every scan.

Related (fix in the same pass): relative imports without extension/index resolution (`./Button` ≠ `Button.tsx`, lines 133-142) and Esprima cannot handle TypeScript (imports of most `.ts/.tsx` files are silently discarded, lines 88-99). Note: the planned JS/TS plugin family (Node sidecar) will eventually replace this parser — but until then the existing parser should not produce dead edges.

## Reproduction

Index a repo with `.tsx` files on Windows; `get_subgraph` on a JS component seed → `IMPORTS` edges point to nonexistent targets.

## Fix

Remove `ToLowerInvariant()`; adopt the scanner's canonical path form (shared `PathNodeId` helper). Import resolution: probe `source + {.ts,.tsx,.js,.jsx}` and `source/index.*` against candidates.

## Acceptance Criteria

- [ ] Every `IMPORTS`/`DEFINED_IN` edge references existing nodes (integrity test after an index run over a fixture repo).
- [ ] No ID collision between component and file node; the file node's `ContentHash` stays stable across scans.
- [ ] `import './Button'` connects to the `Button.tsx` file node.

## DoD

- Fix + fixture test merged; re-index note in the CHANGELOG.
