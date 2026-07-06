# Changelog

All notable changes to Shonkor are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and the project uses semantic versioning.

## [Unreleased]

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
