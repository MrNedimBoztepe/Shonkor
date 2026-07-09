# Shonkor 🧠 - Precision GraphRAG & Structural Context Engine

Shonkor is a highly precise, locally executed indexing and query system for code and documentation, developed in **.NET 10 (C#)**. It uses a **Knowledge Graph (GraphRAG)** approach based on SQLite (FTS5 + recursive CTEs) to capture the logical context of software architectures completely offline and deterministically, and to prepare it for Large Language Models (LLMs).

Unlike probabilistic vector databases, Shonkor guarantees **100% precise and structural context**. It extracts compiler-accurate syntax trees (AST) using **Roslyn (C#)** as well as dependencies for **JavaScript/TypeScript**, **PHP**, **Sitecore configurations (YAML)**, **GraphQL**, and **Markdown hierarchies**.

Shonkor also integrates natively with **Ollama (local)** to enrich nodes in the background through small, efficient models — code embeddings (`nomic-embed-text`) for meaning-based search and short AI summaries (e.g., `qwen2.5-coder`). In the reproducible benchmark below, the budget-aware context capsule cuts the tokens sent to an LLM by **≈ 41 %** on Shonkor's own graph — measured honestly against dumping the *same* retrieved subgraph in full (not against the whole repo). The saving grows with graph size and hub density.

---

## 🌟 Features

* **Multi-Language AST Parsing**:
  * **C# (.cs)**: Full Roslyn integration for extracting namespaces, classes, interfaces, records, structs, enums, properties, constructors, and methods – including inheritance (`IMPLEMENTS`/`EXTENDS`) and **type reference edges** (`REFERENCES_TYPE`) for true impact analysis. **Semantic resolution is ON by default** (`Indexing:SemanticCSharp` / opt out with `SHONKOR_SEMANTIC_CSHARP=false`): a Roslyn `SemanticModel` resolves references **exactly** (disambiguating same-named types across namespaces) and adds method-level `CALLS`, `OVERRIDES` (override→base member), `IMPLEMENTS_MEMBER` (concrete member→interface member) and `INSTANTIATES` (`new T()` call site→constructed type) edges. Exactly-resolved edges are tagged `EXTRACTED` provenance; heuristic name-based ones fall back to `INFERRED`.
  * **JavaScript/TypeScript (.js, .jsx, .ts, .tsx)**: Extraction of ES imports, React components, and backend APIs.
  * **PHP (.php, .tpl)**: Regex-based module parser for OXID eShop with module extends and Smarty template blocks.
  * **Sitecore SCS (.yml, .yaml)**: Template and layout dependencies (Unicorn/SCS).
  * **GraphQL (.graphql)**: Queries, fragments, and referenced templates.
  * **Markdown (.md)**: Segments documents by headings and links relative links.
* **Cross-Technology Linking**: A post-scan linker connects Next.js components ↔ Sitecore renderings ↔ C# controllers ↔ GraphQL templates and assigns everything to Helix modules (`BELONGS_TO_MODULE`).
* **100% Offline & Self-Contained**: Local SQLite database (`shonkor.db`) with FTS5 full-text search and recursive CTE subgraph queries. No external API dependencies.
* **Token-Optimized Context Capsule Synthesizer**: Generates prompt-ready Markdown files including automatic **Mermaid.js** architecture diagrams.
* **MCP Server (Model Context Protocol)**: Provides the graph directly to AI assistants like **Claude** and **Antigravity** with a token-efficient toolset that closes the agentic edit loop:
  * **Start here**: `orient` (one-call session bootstrap — graph size, tool palette, the edit loop). Run `shonkor agents` to print an AGENTS.md/CLAUDE.md snippet so assistants reach for the graph reflexively.
  * **Find**: `search_graph` (FTS5), `locate`, `search_semantic` (vector/meaning — only listed when an embedding backend is wired).
  * **Read**: `signature` (signature only), `get_source` (exact symbol body + `file:start-end`), `outline` (file structure), `get_subgraph`, `generate_capsule`, `architecture` (arc42 building-block view + Mermaid module-dependency diagram, for docs/onboarding).
  * **Analyze**: `references` (`direction=used_by` = who references it / `uses` = what it uses; `depth=1` flat, `depth>1` transitive — ranked blast radius with affected tests flagged, or a dependency tree; `provenance=extracted|inferred|all` trust filter), `call_hierarchy` (method-level callers/callees over `CALLS`; semantic mode), `find_usages` (call sites with code snippets), `find_path` (shortest connection between two symbols), `implementations_of` (interface/base subtypes), `verify_exists` (anti-hallucination fact-check).
  * **Topology & onboarding** (embedding-free, deterministic): `audit` (one-call briefing: size, EXTRACTED/INFERRED trust mix, god nodes, modules, dead clusters, suggested next calls), `hotspots` (change-risk nodes by betweenness centrality), `clusters` (`mode=modularity` cohesive communities / `mode=components` isolated dead code), `surprising_connections` (embedding-similar-but-unlinked pairs — INFERRED hints).
  * **Plan & apply**: `edit_plan` (a concrete edit checklist), `rename_plan` (overload-precise rename sites from the graph's exact edges, not name-grep), `related_tests` (transitive test impact — exactly what to run after a change), `reindex_file` (refresh one file after editing — relinks its `REFERENCES_TYPE` edges), `check_edit` (compile-check a C# file after editing — Roslyn syntax + semantic errors, self-contained, no `dotnet build`).
  * **Review**: `review` (a code-review briefing for a set of changed files — per-file compile check + aggregated transitive impact + the tests to run + top risks).
  * **Freshness (anti-drift)**: `freshness` (with a `path` = one file's sync state; without = project-wide drift report: changed / new / deleted). The analysis/read tools also **auto-flag** a result whose underlying file changed since indexing (`⚠ … EDITED since indexing — run reindex_file`), so an agent never silently trusts stale data.
  * **Memory**: `get_open_threads`, `record` (`type` = decision / milestone / task / question).
  * See the [LLM Integration Manual](docs/user/llm_integration.md) for the full reference.
* **Visual Web Dashboard**: A glassmorphic web interface with interactive 2D force-directed graph visualization (`force-graph`, WebGL Canvas), live physics, code preview (Prism.js), capsule creator, project and plugin management.
  * **Search Modes**: Keyword (FTS5) or, in "Brain" mode, **Hybrid** search — Reciprocal Rank Fusion of FTS + vector similarity (falls back to keyword when no embeddings/backend are present).
  * **Ask AI (GraphRAG)**: Generate AI answers grounded in the retrieved code context nodes using a local Ollama model, **streamed token-by-token** with per-claim source citations.
  * **AI Settings**: Configure the Ollama endpoints/models, embedding source, answer streaming, and the semantic-C# default from the dashboard's Settings → **AI** tab (loopback-only writes; applied without a restart).
  * **Impact & Dependencies panel**: Selecting a node shows its authoritative "Referenced by" / "Depends on" lists (with AI summaries), and a **Find Path** tool traces the shortest connection to any other symbol.
* **Multi-Project Registry**: Manage multiple codebases in parallel (`projects.json`), each with its own database.
* **Powerful CLI**: Automation via `init`, `index` (`--embed` for semantic search), `search`, `capsule`, `mcp`, `agents` (print an AGENTS.md/CLAUDE.md snippet), and `plugin`.

---

## ⚡️ Benchmark

All numbers below come from one reproducible harness, `src/Shonkor.Bench`, over a built graph DB. Reproduce them yourself:

```powershell
dotnet run --project src/Shonkor.Bench -- shonkor.db                              # exact-symbol set + token reduction
dotnet run --project src/Shonkor.Bench -- shonkor.db --set bench/golden/agent-queries.json   # FTS/vector/hybrid on hand-labeled NL queries
dotnet run --project src/Shonkor.Bench -- shonkor.db --set bench/golden/agent-queries.json --compare-rag
dotnet run --project src/Shonkor.Bench -- shonkor.db --answers bench/golden/answers.json      # answer groundedness (needs Ollama)
dotnet run --project src/Shonkor.Bench -- shonkor.db --check-circularity bench/golden/agent-queries.json  # circularity guard
```

It writes `bench/report.md` (human) and `bench/metrics.json` (machine); `--baseline <metrics.json>` gates **P@1 / MRR / Recall@k** against a stored run (a >5 % relative drop in any retriever exits 2). The FTS rows run without Ollama; the **vector** and **hybrid (RRF)** rows need a reachable embedding backend and are gated in a nightly CI job (a missing backend there is a hard fail, not a silent skip). `--check-circularity` flags any golden case whose query shares more than N content words with its target's embedding document — so a set can't quietly become self-matching.

`--answers` measures the **answer path**, not retrieval: each golden case pins a fixed context (node ids/symbol names) and asks the production RAG prompt (temperature 0, fixed seed) a question. Metrics: citation validity (does every `[Name @ file:lines]` reference a context node?), must-cite recall, abstention recall/precision (does the model say "This is not supported by the current graph data." exactly when the context doesn't cover the question?), and the uncited-paragraph rate. Writes `bench/answers-report.md` + `bench/answers-metrics.json`; `--baseline bench/answers-baseline.json` gates the four headline metrics (>5 % relative drop → exit 2). Greedy decoding on a GPU is near- but not bit-deterministic (~1 in 40 answers can flip a borderline token across runs); the 5 % gate tolerance absorbs that noise.

**1. Can it find the right symbol? (retrieval precision)**

_Measured run: `shonkor.db`, 200 self-retrieval + 33 agent-query cases, 2026-07-09 (post-TICKET-204, full method bodies)._

| Search task | Retriever | Precision@1 | Recall@10 |
|---|---|---:|---:|
| **Exact name** ("`SqliteGraphStorageProvider`") — 200 auto-bootstrapped self-retrieval cases | FTS5 keyword | **0,90** | **0,99** |
| **Plain-English intent** — 33 hand-labeled agent queries (`bench/golden/agent-queries.json`), validated non-circular by `--check-circularity` | FTS5 keyword | **0,00** | **0,12** |
| same intent set | **vector / hybrid (RRF)** | _nightly gate_ | _nightly gate_ |

*In plain terms:* keyword search is strong when you already know the name (top hit 90 % of the time) and **useless at plain-English intent** (top hit 0 % — the target is in the top 10 but rarely rank 1, because full method bodies + class skeletons now give many nodes the same keywords). That is exactly why meaning-based vector + hybrid retrieval exist; those rows need an embedding backend and are measured in the nightly CI gate. The deterministic PR gate therefore uses the exact-name self-retrieval set, where FTS is meaningful; the NL set is a vector/hybrid benchmark.

> **Note on an earlier "~88 % vector P@1" claim (removed).** It came from `doc-intent.json`, whose query is the target symbol's own doc-comment summary — which is embedded verbatim in that symbol's vector document. That is **circular**: the hit is trivial, so 0,88 was an upper bound, not real NL→code recall. The new `agent-queries.json` set uses independently-worded queries and is guarded by `--check-circularity`; the honest FTS baseline above is correspondingly lower.

**2. How much context does it save? (token reduction)**

The budget-aware capsule (seed-first, hub-capped) vs a **full dump of the *same* retrieved subgraph** — a fair baseline, not a whole-repo strawman: **85,7 % fewer tokens** (7 queries, 931.030 → 133.423) on Shonkor's own graph (2026-07-09, post-TICKET-204). The saving rose from ~41 % once methods stored their full bodies and class nodes gained a member skeleton — the naive full-dump is now much larger, which is exactly what the budget caps. The figure is DB-dependent — reproduce it on yours with the first command above. _(An earlier "~88 % on a denser graph" anecdote was unstored and has been removed; this number is from a checked-in run.)_

**3. Is it better than plain RAG? (head-to-head, `--compare-rag`)**

Against a **chunked-RAG-without-graph** baseline at a **matched token budget** (both start from the same embedding search, the baseline then takes as many top text chunks as fit into Shonkor's per-query token count), so this compares *coverage at equal cost*. Coverage is now measured **symmetrically on the delivered text** for both sides (the baseline's chunks and Shonkor's budgeted capsule string), plus a **seed-survival** rate (how many semantic seeds survive the capsule budget) — the earlier Shonkor figure measured the pre-budget subgraph, which was over-optimistic. Current numbers land in `bench/report.md` from the `--compare-rag` run (needs Ollama); they are intentionally not pinned here until the nightly gate stores them.

> **Honest by construction.** The token comparison is the budgeted capsule vs the *same retrieved nodes* dumped in full (no whole-repo strawman). The RAG head-to-head matches token budgets and measures delivered-text coverage symmetrically. Golden query sets are guarded against circularity (`--check-circularity`). All numbers are DB-dependent — they differ on your codebase; reproduce with the commands above.

---

## 📁 System Structure

The project follows a clean **Clean Architecture** structure:

```
src/
  ├── Shonkor.Core/            # Domain models, interfaces, AST parser, capsule synthesizer, hybrid fusion
  ├── Shonkor.Infrastructure/  # SQLite graph storage, crawler (SHA256), assembly-plugin registry/loader,
  │                            #   cross-tech + semantic C# linker, embedding/enrichment services
  ├── Shonkor.Plugin.Sitecore/ # First-party CMS plugins (Sitecore / Kentico / Optimizely content-model
  ├── Shonkor.Plugin.Kentico/  #   parsers) — each built & installed as a ZIP
  ├── Shonkor.Plugin.Optimizely/
  ├── Shonkor.CLI/             # Console interface (init, index, search, capsule, mcp) + MCP server
  ├── Shonkor.Bench/           # Unified benchmark harness: token reduction + retrieval precision
  └── Shonkor.Web/             # Minimal APIs, API key middleware & glassmorphic web dashboard (wwwroot)
tests/
  └── Shonkor.Tests/           # Unit tests for parser, SQLite CTE, concurrency, linking & enrichment
docs/
  ├── developer/arc42/         # Developer documentation following the arc42 standard (chapters 1-6, 8)
  ├── user/                    # User manuals (setup, CLI, LLM integration)
  └── architecture/            # Architecture reviews
```

---

## 🚀 Quickstart

### Prerequisites
* [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### 1. Clone & Compile
```powershell
dotnet build
```

### 1a. Install `shonkor` as a command (recommended)
`shonkor` needs to be on your PATH so MCP clients can launch it. Two ways:

**A. Prebuilt binary (no .NET SDK needed)** — self-contained release binaries are published per OS:
```bash
# macOS / Linux
curl -fsSL https://raw.githubusercontent.com/MrNedimBoztepe/Shonkor/main/scripts/install.sh | sh
```
```powershell
# Windows
irm https://raw.githubusercontent.com/MrNedimBoztepe/Shonkor/main/scripts/install.ps1 | iex
```

**B. .NET global tool** (needs the .NET 10 SDK):
```powershell
dotnet pack src/Shonkor.CLI -c Release -o ./nupkg
dotnet tool install --global --add-source ./nupkg Shonkor
```

Then, anywhere:
```powershell
shonkor mcp install     # register the MCP server in detected clients (Claude Desktop/Code, Antigravity)
shonkor mcp status      # show what's detected/registered
```

> The release binaries are produced by a tag-triggered GitHub Actions pipeline — see `docs/ci/release.yml` (add it under `.github/workflows/` and push a `vX.Y.Z` tag).

### 2. Initialize and Index CLI
```powershell
cd src/Shonkor.CLI

# Create default configuration (shonkor.json)
dotnet run -- init

# Index own project
dotnet run -- index ../../

# Search for a class or method
dotnet run -- search "RoslynAstParser"

# Create token-optimized context capsule
dotnet run -- capsule "RoslynAstParser" --hops 2 --out capsule.md
```

### 3. Register MCP Server in Claude/Antigravity
```powershell
# Registers Shonkor automatically in the available MCP clients
dotnet run -- mcp install
```
Details and manual configuration: see [LLM Integration Manual](docs/user/llm_integration.md).

### 4. Start Web Dashboard
```powershell
cd ../Shonkor.Web
dotnet run

# Open browser at: http://localhost:5290
```

---

## 🔐 Security (Brief Overview)

Shonkor is primarily designed as a **local** tool. For operation behind a proxy / as SaaS, the following applies:

* **API keys / user tokens are stored SHA-256 hashed**, never in plaintext — `projects.json` holds only the hash, comparison is constant-time (`FixedTimeEquals`), and any legacy plaintext is auto-migrated to a hash on load. A new user's token is shown **once** at creation.
* **API Keys & Secrets** do **not** belong in `appsettings.json` or `projects.json` (both are gitignored), but in user secrets / environment variables (`ApiKeys__<key>=<projectName>`, `GitHub__WebhookSecret=…`).
* The **Loopback Auth Bypass** is only active in `Development`; in production, the API key check always applies.
* **Plugins are pre-built assemblies**, installed from a ZIP and **inert until explicitly activated** — there is no runtime compilation of source (the old C#-source/Roslyn plugin path, an RCE vector, has been removed). Manage them with `shonkor plugin install <zip> | activate <id> | deactivate <id> | list | uninstall <id>` (or the loopback-only web API). `Security:EnablePlugins` is now an opt-OUT kill switch (default on); per-plugin activation is the trust gate. See the first-party CMS plugins in `src/Shonkor.Plugin.Sitecore`, `src/Shonkor.Plugin.Kentico`, and `src/Shonkor.Plugin.Optimizely` (CMS content-model parsers, each built & installed as a ZIP).
* **Webhooks** verify `X-Hub-Signature-256` (HMAC) and fail without a configured secret (fail-closed).
* `/api/browse` (file system browser) is only accessible locally/in development.

### 🩺 Operations & CI/CD
* **Health probes** (public): `/health` & `/health/live` (liveness) and `/health/ready` (readiness — workspace writable + active graph store reachable). Structured **JSON logs** in Production.
* **CI/CD**: GitHub Actions builds & tests every PR to `main`; on `main`/version tags it publishes the hardened (non-root, healthchecked) Linux container image to GHCR.

---

## 📚 Documentation Architecture

1. **Developer Documentation (arc42)**: [docs/developer/arc42/README.md](docs/developer/arc42/README.md)
2. **User Manuals**: [docs/user/README.md](docs/user/README.md)
   * [Setup Guide](docs/user/setup_guide.md): Onboarding, configuration, security, multi-project.
   * [CLI Reference](docs/user/cli_reference.md): All CLI commands with examples.
   * [LLM Integration Manual](docs/user/llm_integration.md): Connection to Claude/Antigravity (MCP), Cursor, and Web LLMs.

---

## ⚖️ License
This project is licensed under the MIT License. See the `LICENSE` file for further details.
