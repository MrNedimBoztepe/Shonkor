# Changelog

All notable changes to Shonkor are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and the project uses semantic versioning.

## [Unreleased]

### Fixed — The docs described a security model we no longer have (#153)
- **Three places claimed plugins are compiled from source at runtime** — an RCE surface that was **removed**
  when plugins became pre-built, installed assemblies. `docs/user/setup_guide.md`, `arc42/08_concepts.md` and
  `arc42/05_building_block_view.md` all still described `PluginLoader` / Roslyn runtime compilation.
- Worse, two of them described **`Security:EnablePlugins` as an opt-in that defaults to OFF** ("disabled by
  default; only activate consciously"). It is in fact a **kill switch that defaults to ON**
  (`EndpointHelpers.PluginsEnabled` → `GetValue("Security:EnablePlugins", true)`). A reader following the old
  docs would believe plugin loading was disabled on their machine when it was enabled. Corrected everywhere,
  and the *real* trust gate is now named explicitly: **per-plugin activation** — installing runs nothing,
  activating is equivalent to executing that plugin's code.
- **`arc42/05_building_block_view.md` rewritten** against the current code. It also gained the building block
  the chapter was missing: **`Core.Services.HybridRetrieval`**, the single retrieval entry point that the
  `search_hybrid` tool, `generate_capsule` seeding, `/api/search/hybrid` and `/api/capsule` all delegate to —
  written down as an invariant, because these four sites previously held three drifting copies and a fifth
  copy would be a defect. Also corrected: the MCP surface is an `IMcpTool` registry (not a monolithic
  `McpServer`), `VectorMath` lives in Infrastructure, `StandardPlugins/` no longer exists, and
  `Shonkor.Bench` supersedes `Shonkor.Eval`/`Shonkor.Benchmarks`.

### Fixed — The README's benchmark numbers were stale (#152)
- The vector/hybrid retrieval rows said *"nightly gate"* instead of a figure: the README argued that keyword
  search fails on plain-English intent and that hybrid retrieval is the fix — then never showed the number
  proving it. **Now measured and pinned** (2026-07-14, `nomic-embed-text`, 3.529 nodes): on 33 hand-labeled
  English queries, Recall@10 goes **0,212 (keyword) → 0,879 (hybrid)** and Precision@1 **0,121 → 0,455**.
  Hybrid is also the best on exact names (P@1 **0,935** vs 0,895 for keyword alone), so it is not a trade-off.
- **Two published numbers were simply wrong** on the current graph and are corrected:
  - Plain-English keyword retrieval was published as **0 % P@1 / 12 % Recall@10**; it actually measures
    **12,1 % / 21,2 %**. The "keyword search is *useless* at intent" framing overstated the case.
  - Token reduction was published as **85,7 %** (931.030 → 133.423); on the current graph it measures
    **96,2 %** (1.999.242 → 76.058 over 7 queries).
- Both runs are checked in as `bench/metrics-exactname.json` and `bench/metrics-agent-queries.json`, so the
  tables are traceable to raw harness output rather than an ad-hoc local run (`bench/metrics.json` and
  `bench/report.md` are per-run scratch output and remain gitignored).

### Added — MCP security hardening (TICKET-209, #103)
- **Path containment on every file-taking tool.** `McpToolHelpers.TryResolveContainedPath` resolves a
  caller-supplied path against the project base with `Path.GetRelativePath` and rejects any `..` escape or
  rooted path that leaves the workspace — so `get_source`/`outline`/`reindex_file`/`check_edit` can't be
  aimed outside the indexed tree.
- **Loopback bypass is opt-in and fail-loud.** The dev-only API-key bypass is now
  `env.IsDevelopment() && (flag ?? true)` with a startup warning when active; `/api/mcp` is added to the
  SaaS-endpoint exemption so the relay authenticates like the rest of `/api/*`.
- **`record` hardening** and **generic relay error messages** — the HTTP relay no longer leaks
  `ex.Message` to the caller.

### Added — MCP protocol conformance & backend hygiene (TICKET-210, #108)
- **JSON-RPC correctness**: `ping` → empty result; malformed JSON → `-32700` (id `null`); non-object
  request → `-32600`; `protocolVersion` negotiated against `SupportedProtocolVersions`; notifications
  return nothing. Tool-execution failures surface as `isError:true` results carrying the **tool name +
  `ex.GetType().Name`** — never the raw `ex.Message`.
- **Ollama retry hygiene**: `OllamaRetry` (transient/connect-error classification + jittered backoff) and a
  typed `OllamaResponseException`, so a flaky/slow embedding or summary backend degrades instead of throwing
  opaque errors.
- **Output clamps**: `MaxResultLimit`/`MaxHops`/`MaxPathHops` and a `DefaultOutputCapChars` (32 KiB) cap on
  tool output.

### Added — Markdown as first-class indexed content (TICKET-211, #109)
- **Section bodies with real line ranges.** `MarkdownHierarchyParser` now captures each section's body and
  a 1-based line range, detects headers **fence-aware** (no false headers inside code blocks), and splits
  sections over `MaxSectionChars` (4000) at paragraph boundaries into `::part::N` nodes.
- **Summaries are searchable**: the node `Summary` is indexed in `NodesFts` (with matching triggers), so an
  AI summary contributes to keyword recall.
- **Concept embeddings**: concept nodes get an embedding document (`EmbeddingTextBuilder.BuildConcept`) so
  they participate in vector/hybrid retrieval.

### Changed — Retrieval: capsule budget, vector scaling, one shared hybrid path
- **`generate_capsule` is budget-aware and hybrid-seeded** (TICKET-214, #121): the MCP tool seeds via
  hybrid retrieval and renders under the same seed-first / hub-capped budget the web capsule uses.
- **Vector scaling Stage 1** (TICKET-215, #127): embeddings are **L2-normalized on write**, scored by a
  **zero-copy dot product** (`MemoryMarshal.Cast` + `TensorPrimitives`), with a **similarity floor**
  (`score <= 0` excluded) and the over-scan factor removed; a one-time `Meta`-flagged migration normalizes
  pre-existing vectors. `VectorMath` moved to `Shonkor.Infrastructure`. Stage 2 (index/ANN over >20k nodes)
  is intentionally deferred — this graph is ~2–4k nodes.
- **One shared hybrid-retrieval path** (#122, #146): `HybridRetrieval.SearchAsync` in `Shonkor.Core.Services`
  is now the single implementation behind the `search_hybrid` tool, `generate_capsule` seeding, the web
  `/api/search/hybrid` endpoint, and `/api/capsule` — which previously seeded FTS-only and so retrieved
  worse than the tool for intent-phrased queries. Three near-duplicate copies collapsed into one.
- **Node-id scheme is now v6** (`CsharpNodeId.SchemeVersion`), triggering a clean reindex on upgrade.

### Fixed — Concept hygiene & honest benchmarking
- **Orphaned concept nodes are pruned** (#135, #141): concept ids are normalized at creation
  (`concept_` + lowercase-alphanumeric) and the enrichment worker deletes `Concept` nodes with no incoming
  `RELATES_TO` after a completed cycle. 1499 all-orphaned concepts had roughly **halved** semantic P@1;
  removing them restored it.
- **The benchmark corpus is de-contaminated** (#132/#133, #137): the golden/tickets/review/bench-prose meta
  files are excluded from the indexed corpus (`shonkor.json`) **and** ignored at measurement time
  (`IsEvalMetaNode`). The earlier "#110 doc-vs-code regression" was benchmark **self-contamination** (golden
  files containing the query strings verbatim), not a real ranking regression — documented in
  `bench/code-intent-decontamination.md` and `bench/eval-corpus-policy.md`.

### Changed — Benchmark harness unified
- `Shonkor.Eval` and `Shonkor.Benchmarks` are consolidated into the single **`Shonkor.Bench`** harness
  (token reduction + retrieval precision + RAG head-to-head + answer groundedness). Earlier roadmap entries
  below that name the `Shonkor.Eval`/`shonkor-eval`/`Shonkor.Benchmarks` projects refer to this now-unified
  harness; the commands in the README Benchmark section are the current ones.

### Changed — Honest, reproducible benchmark numbers in the docs
- Replaced the inflated "up to 87 % / 90 % / 92 % token reduction vs. the entire codebase" claims (a
  whole-repo strawman nobody would actually send) across the **README**, **sales presentation**, and
  **arc42 introduction** with the **measured, reproducible** figures from `Shonkor.Bench` on Shonkor's own
  graph (2026-07-06): token reduction **≈ 41 %** (up to ~88 % on hub-dense graphs) measured against dumping
  the *same* retrieved subgraph in full; retrieval **Precision@1 0,95 / Recall@10 1,00** (exact name, FTS5)
  and **Recall@10 0,37 → 0,97** (plain-English intent, keyword → code-embedding vector); RAG head-to-head
  **98 % vs 77 % coverage at a matched token budget** (+21 pp). The README Benchmark section was rewritten in
  plain language with a "what this means" note per metric. The ROI example is now anchored to the measured
  reduction band instead of a fabricated 95 % saving.

### Added — Insights panel in the dashboard
- New **Insights** station in the Atlas dashboard surfacing the graph-insight features that were previously
  MCP-only: **Hotspots** (change-risk god nodes), **Clusters** (modularity communities or connected
  components with a mode toggle; small clusters = likely-dead code), and **Surprising connections**
  (embedding-similar pairs with no edge, each with an on-demand LLM "explain"). Every listed node is
  clickable and focuses it in the graph.
- Backed by new REST endpoints **`GET /api/insights/hotspots`** and **`GET /api/insights/clusters`** (the
  REST twins of the `hotspots`/`clusters` MCP tools; surprising-connections already had its endpoint), gated
  behind the API-key middleware like the rest of `/api/*`.

### Added — Whole-graph insight MCP tools
- **`hotspots`** — ranks change-risk "god nodes" by betweenness centrality over the coupling subgraph
  (widest blast radius). Deterministic, no model.
- **`clusters`** — groups the graph into modularity communities (`mode=modularity`) or connected
  components (`mode=components`, where small clusters flag isolated / likely-dead modules). Deterministic.
- **`surprising_connections`** — node pairs whose embeddings are similar but that have no edge (candidate
  missing links / duplication). Requires an embedding pass; inferred hints only.

### Added — Editable AI/tool settings in the dashboard
- **`GET`/`POST /api/settings`**: read and change the Ollama endpoints/models, embedding source, answer
  streaming, semantic-C# default, and enrichment batch/parallelism from the Atlas dashboard's Settings →
  **AI** tab — no more editing `appsettings.json` by hand. Writes are **loopback-only** and opt-in outside
  Development (`Security:AllowSettingsWrite`, mirroring `/api/browse`); secrets are never exposed or written.
- Writes land in a machine-local, gitignored **`appsettings.Local.json`** overlay, loaded with
  `reloadOnChange` and inserted **below** environment variables (so deployment env still wins). Most settings
  apply on the next request/enrichment cycle; the drift-worker interval remains restart-only.
- Enrichment reads `Embedding:Source` / batch / parallelism **per cycle**, so dashboard edits take effect
  without a restart. Stored vectors record their **model** (`EmbeddingModel`) as well as dimension, so a
  same-dimension model swap is detected and re-embedded, not silently mixed into the vector search.

### Added — Precision roadmap #2 (retrieval reaches the main paths; grounding measured)
- **Semantic/hybrid search now works on the CLI and MCP paths, not just the web dashboard.**
  `shonkor index --embed` populates code embeddings at index time (opt-in; needs a reachable Ollama), and
  the stdio MCP server wires an embedding service when a backend is reachable — so `search_semantic` and the
  new **`search_hybrid`** MCP tool are usable by agents. Absent backend → clean FTS fallback (no startup delay).
- **`search_hybrid` MCP tool** and dashboard "Brain" mode now use Reciprocal Rank Fusion (FTS + vector).
- **Streaming answers**: `POST /api/ask/stream` streams the grounded answer token-by-token (`ISemanticAnalyzer.StreamRAGResponseAsync`); the dashboard renders incrementally. Toggle with `Features:StreamingAnswers=false`.
- **Grounding evaluation** (`shonkor-eval --answers`): citation validity, must-cite rate, and abstention
  recall over the RAG answer path — "grounded" is now measured, not just prompted.
- **Prompt-injection hardening**: the RAG prompt frames retrieved context as untrusted data; a
  `SuspiciousContentPostProcessor` flags injection-style text via `security.suspicious-instruction-in-content`.
- **Embedding coverage of large symbols**: `EmbeddingTextBuilder` embeds head + tail (not just the head), so
  a symbol's opening and closing logic are both represented; shared by the web worker and CLI embed pass.
- **Eval harness**: 40-case intent golden set (was 15), 95% confidence intervals in the report, a
  `--force-mode` switch for apples-to-apples graph-vs-semantic runs, and a direct-SQL embedding count
  (fixing a measurement bug where `GetAllNodesAsync` never loads the embedding BLOB).
- **New storage op** `UpdateNodeEmbeddingAsync` (embedding-only write, no summarization).
- Measured end-to-end on this repo's `src` (885 embedded nodes, 40 intent queries): natural-language
  Recall@10 **0.25 (FTS) → 0.98 (semantic)**, Precision@1 **0.25 → 0.73**. See `review/results.md`.

### Added — Precision & grounding roadmap (retrieval quality)
- **Semantic C# resolution is now the default.** Indexing resolves C# references with a Roslyn
  `SemanticModel` (exact `REFERENCES_TYPE`/`IMPLEMENTS`/`EXTENDS` + method-level `CALLS`), disambiguating
  same-named types across namespaces. It is **non-lossy**: references a partial/non-compiling checkout
  can't resolve fall back to name matching, so it is never worse than the syntactic resolver — only more
  precise. Measured on this repo's `src` (168 files): ~2.0 s → ~5.6 s indexing (~2.9×) for ~50 % more,
  more-precise edges. Opt out with `Indexing:SemanticCSharp=false` (global / per-project) or
  `SHONKOR_SEMANTIC_CSHARP=false` (CLI).
- **Embeddings are computed from code, not the AI summary** (`Embedding:Source=code|summary`, default
  `code`): a structured `type + name + signature + summary + bounded body` document. On intent queries
  over this repo, Recall@10 rose from ~0.27 (FTS-only) to ~0.93–1.0. Query/document embeddings are now
  kind-aware, with optional nomic task prefixes (`EmbeddingService:QueryPrefix`/`:DocumentPrefix`,
  default off — measured neutral on code).
- **Embedding versioning + re-embed trigger.** Nodes store `EmbeddingDim`; a model/dimension change now
  flags stale vectors for re-embedding (`MarkStaleEmbeddingsForReembedAsync`, run once per process by the
  enrichment worker) instead of silently dropping them from vector search.
- **Hybrid search endpoint** `GET /api/search/hybrid`: Reciprocal Rank Fusion of FTS (BM25) + vector
  similarity. Additive — existing `/api/search` and `/api/search/semantic` are unchanged; degrades to
  FTS-only when no embedding backend is reachable.
- **Budget-aware context capsule.** `ContextCapsuleSynthesizer` gained `CapsuleOptions` (seed ids, content
  budget, node cap): seeds render first and in full, the rest fills a bounded budget by structural
  relevance, and a hub cap prevents a 2-hop expansion from exploding the prompt. On a query mix, ~87.8 %
  fewer tokens than dumping the same retrieved subgraph in full, with the absolute size bounded. The
  legacy full-content rendering remains the default for the parameterless `Synthesize` overload.
- **Grounded RAG answers.** `GenerateRAGResponseAsync` now asks for per-claim citations
  `[Name @ file:lines]`, runs at `temperature=0` (reproducible), and truncates code at line boundaries.
- **Ambiguous-type diagnostic.** A first-party post-processor emits `csharp.ambiguous-type-reference`
  (Warning) for same-named C# types that are actually referenced, so name-based over-connection is visible
  via `get_diagnostics`.
- **`Shonkor.Eval` project** — a lean, repeatable precision harness (Precision@k / Recall@k / MRR for FTS,
  semantic and hybrid; golden set under `eval/`; baseline regression gate). See `review/` for the full
  analysis, measured results, and roadmap.
- **Honest token benchmark.** `Shonkor.Benchmarks` now compares the real shipped capsule path against a
  full-content dump of the *same* retrieved subgraph (replacing the previous whole-file-vs-summary
  comparison).

### Changed — Plugins are now installable assemblies (runtime C# compilation removed)
- A plugin is a **pre-built assembly installed from a ZIP** and is **inert until explicitly activated**.
  `PluginRegistry` validates the `plugin.json` manifest + host-API version, extracts the package
  (zip-slip guarded) into `plugins/{id}/`, and tracks the `Installed → Active → Disabled/Failed`
  lifecycle; `AssemblyPluginLoader` loads only Active plugins into a collectible `AssemblyLoadContext`
  (the host shares the `Shonkor.Core` contract for type identity). Installing a plugin runs nothing.
- **Removed the Roslyn source-compilation plugin path** (`PluginLoader`, `StandardPluginsInstaller`,
  the `.cs` scaffold/list/delete endpoints) — the arbitrary-source RCE surface is gone.
- CLI: `shonkor plugin install <zip> | activate <id> | deactivate <id> | list | uninstall <id>`.
  Web: `/api/plugins` (list), `POST /api/plugins/install` (ZIP upload), `.../activate`, `.../deactivate`,
  `DELETE /api/plugins/{id}` — loopback-only for state changes.
- `Security:EnablePlugins` is now an opt-OUT kill switch (default on); per-plugin activation is the gate.
- The first-party CMS parsers moved out of runtime-compiled embedded source into **three pre-built plugin
  projects** — `Shonkor.Plugin.Sitecore`, `Shonkor.Plugin.Kentico`, and `Shonkor.Plugin.Optimizely` — each
  building its own installable ZIP.

### Changed — MCP internals: tool registry (no behavior change)
- The ~2500-line `McpRequestHandler` god-class is decomposed into an `IMcpTool` registry. Each tool is
  now a small, independently testable class under `Services/Mcp/Tools/`; shared state and helpers live in
  `McpToolContext` and `McpToolHelpers`. The handler keeps only the stdio loop and the JSON-RPC envelope
  (initialize / tools/list / tools/call), shrinking from ~2500 to ~210 lines. The tool surface and all
  outputs are unchanged; 103 tests stay green.

### Changed — MCP tool surface slimmed (34 → 26 in the local CLI)
- **`references`** replaces `impact_of`, `depends_on`, `dependency_tree`, and `blast_radius`.
  `direction` (`used_by` default / `uses`) and `depth` (1 = flat list, >1 = transitive blast
  radius with `[test]` flags / dependency tree) select the behavior.
- **`freshness`** replaces `is_fresh` + `stale_files`: with a `path` it checks one file, without
  it returns the project-wide drift report.
- **`record`** replaces `record_decision` / `record_milestone` / `record_task` / `record_question`;
  `type` selects which (decision needs `content`, milestone needs `status`).
- **`search_semantic`** is now capability-gated: it is only listed when an embedding backend is
  wired (web server + Ollama), so the local stdio CLI no longer advertises an inert tool.
- **`set_project`** now switches the active project for the current session only (in-memory). It no
  longer writes the shared, persisted `ActiveProjectName`, so switching in one chat can never
  affect another session or client.

### Changed
- The repo-root `Directory.Build.props` is now the single source of truth for the
  assembly/package version. The MCP server reports this version at runtime in the
  `initialize` handshake instead of a separate hardcoded string.
- The MCP `initialize` handshake now echoes the client's requested `protocolVersion`
  (falling back to `2025-06-18` when none is sent) instead of a fixed protocol date.

### Fixed
- `generate_capsule` now advertises the optional `projectName` argument in its tool
  schema, matching every other tool (the argument was already honored by the
  implementation but was missing from the schema).

### Removed
- Deleted the untracked local `scratch/` throwaway projects (never part of the
  source tree, the solution, or the git history).
