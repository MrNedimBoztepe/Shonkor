# #174 — the #172 nesting change, read per case

Follow-up to #172 (#111, #112). Nesting `MarkdownSection` nodes by heading level moved the aggregate
retrieval numbers, and #174 asked the only question the aggregate cannot answer: **which individual cases
flipped their rank-1 hit, and is the new rank-1 a `MarkdownSection` that displaced the correct code symbol?**
If yes, the exact-name slip is a real regression and the type-aware RRF weighting declined in #110 would be
justified. If no, the aggregate reading ("within the interval — noise") stands.

The per-case diff tool (`--diff`, #283) exists to answer this in one command instead of an argument. This is
that run.

## Method — same corpus, only the parser differs

Two worktrees across the exact #172 boundary:

| | commit | change |
|---|---|---|
| **before** | `f63c001` (`e90c9e5^`) | markdown sections flat |
| **after**  | `e90c9e5` (#172)       | sections nested by heading level, budgeted in tokens |

Both indexers were pointed at the **same** fixed corpus (the `e90c9e5` tree) so the *only* variable is the
markdown parser, then embedded with the same model (`nomic-embed-text`) and scored with the **same** current
bench binary. The after-graph carries **7 more embedded nodes (2078 vs 2071)** — consistent with #172's
premise that nesting produces more `MarkdownSection` nodes.

Reproduce:

```sh
# 1. two worktrees across the boundary
git worktree add ../brain-172-before f63c001     # e90c9e5^  (flat sections)
git worktree add ../brain-172-after  e90c9e5      # #172       (nested sections)

# 2. build the indexer in each, index the SAME corpus with embeddings (needs a reachable Ollama)
( cd ../brain-172-before && dotnet build src/Shonkor.CLI -m:1 \
    && dotnet run --project src/Shonkor.CLI --no-build -- index ../brain-172-after --embed )
( cd ../brain-172-after  && dotnet build src/Shonkor.CLI -m:1 \
    && dotnet run --project src/Shonkor.CLI --no-build -- index ../brain-172-after --embed )

# 3. score each db with the CURRENT bench (emits bench/cases.json), keep the two files
dotnet run --project src/Shonkor.Bench -- ../brain-172-before/shonkor.db --set bench/golden/agent-queries.json
cp bench/cases.json cases-before.json
dotnet run --project src/Shonkor.Bench -- ../brain-172-after/shonkor.db  --set bench/golden/agent-queries.json
cp bench/cases.json cases-after.json

# 4. the per-case rank-1 diff (#174)
dotnet run --project src/Shonkor.Bench -- --diff cases-before.json cases-after.json
```

`agent-queries.json` is the exact-name set: every case's expectation is a bare symbol name
(`"expected": ["ApiKeyMiddleware"]`), which is exactly what "exact-name P@1" measures.

> **Read the aggregate absolutes with care.** This environment embeds with `nomic-embed-text`, not the
> backend behind the originally-published `0,945 → 0,930` exact-name P@1, so the absolute P@1 here is lower.
> What is faithful is the **diff**: the same model on both sides, so a rank-1 flip is caused by the parser
> change, not the model. As a fidelity check, hybrid **Recall@10 reproduced the published Intent Recall@10
> exactly: `0,788 → 0,818`.**

## Aggregate (hybrid, RRF)

| | Precision@1 | Recall@10 | MRR |
|---|--:|--:|--:|
| before | 0,455 | 0,788 | 0,589 |
| after  | 0,455 | 0,818 | 0,579 |

Exact-name P@1 **did not move** in this reproduction; Recall@10 improved. A flat P@1 can still hide equal
regressions and fixes, so the per-case diff is what settles it.

## Per-case rank-1 diff (hybrid) — 5 cases flipped

| | Query | Before rank-1 | After rank-1 |
|---|---|---|---|
| REGRESSED | middleware that authenticates incoming requests by API key | `…/ApiKeyMiddleware.cs` **(File)** ✓ | `…/WebPipelineTests.cs::ApiEndpoint_WithoutKey_Returns401` **(Method)** ✗ |
| REGRESSED | shared state available to every MCP tool | `…/McpToolContext.cs` **(File)** ✓ | `…/MetaTools.cs::SetProjectTool::ExecuteAsync` **(Method)** ✗ |
| FIXED | emit exact C# reference edges from a compilation | `docs/user/setup_guide.md::section::…Exact C# resolution` **(MarkdownSection)** ✗ | `…/SemanticCsharpLinkerTests.cs::ObjectCreation_EmitsInstantiatesEdge…` **(Method)** ✓ |
| FIXED | install and activate a plugin from a zip package | `CHANGELOG.md::section::…Plugins are now installable assemblies` **(MarkdownSection)** ✗ | `…/PluginRegistryTests.cs` **(File)** ✓ |
| same-verdict | parse JavaScript and TypeScript imports | `…/JavaScriptParser.cs` **(File)** | `…/JavaScriptParser.cs::ParseImports` **(Method)** |

(✓ = satisfies the case's expected symbol, ✗ = does not.)

## Finding — the feared mechanism is not present

**No case's correct code symbol was displaced from rank-1 by a `MarkdownSection`.** In fact the two flips
that involve a `MarkdownSection` go the *opposite* way to the hypothesis: after nesting, a section that had
been rank-1 was pushed **below** a code symbol — nesting moved sections *away* from rank-1, not toward it.

The two genuine rank-1 regressions are **code-vs-code**: a test `Method` outranking the source `File` that
answers the query (`ApiKeyMiddleware`, `McpToolContext`). Nesting can reorder non-markdown nodes indirectly —
it reshapes the FTS corpus and the vector neighbourhood that RRF fuses over — but the displacer is a code
node, not a section. Crucially, this means the #110 **type-weight (down-weighting `MarkdownSection` in
exact-name RRF) would not address these regressions at all** — there is no `MarkdownSection` at rank-1 to
down-weight.

**Verdict (answers #174):** the exact-name movement across #172 is **not** "a `MarkdownSection` displacing
the correct symbol". The aggregate reading stands, and this run provides **no evidence** for the fusion-level
type-weight declined in #110. If a code-vs-code rank-1 effect (a test outranking its source file on
exact-name queries) is worth pursuing, that is a separate question from #172 and from #110, and belongs in
its own ticket.
