# Shonkor 🧠 - Precision GraphRAG & Structural Context Engine

Shonkor is a highly precise, locally executed indexing and query system for code and documentation, developed in **.NET 10 (C#)**. It uses a **Knowledge Graph (GraphRAG)** approach based on SQLite (FTS5 + recursive CTEs) to capture the logical context of software architectures completely offline and deterministically, and to prepare it for Large Language Models (LLMs).

Unlike probabilistic vector databases, Shonkor guarantees **100% precise and structural context**. It extracts compiler-accurate syntax trees (AST) using **Roslyn (C#)** as well as dependencies for **JavaScript/TypeScript**, **PHP**, **Sitecore configurations (YAML)**, **GraphQL**, and **Markdown hierarchies**.

New: Shonkor natively integrates with **Ollama (local)** to transform the raw source code in the background through small, efficient models (e.g., `qwen2.5-coder`) into highly condensed AI summaries. This reduces the token requirement for downstream RAG queries by up to **87%**!

---

## 🌟 Features

* **Multi-Language AST Parsing**:
  * **C# (.cs)**: Full Roslyn integration for extracting namespaces, classes, interfaces, records, structs, enums, properties, constructors, and methods – including inheritance (`IMPLEMENTS`/`EXTENDS`) and **type reference edges** (`REFERENCES_TYPE`) for true impact analysis.
  * **JavaScript/TypeScript (.js, .jsx, .ts, .tsx)**: Extraction of ES imports, React components, and backend APIs.
  * **PHP (.php, .tpl)**: Regex-based module parser for OXID eShop with module extends and Smarty template blocks.
  * **Sitecore SCS (.yml, .yaml)**: Template and layout dependencies (Unicorn/SCS).
  * **GraphQL (.graphql)**: Queries, fragments, and referenced templates.
  * **Markdown (.md)**: Segments documents by headings and links relative links.
* **Cross-Technology Linking**: A post-scan linker connects Next.js components ↔ Sitecore renderings ↔ C# controllers ↔ GraphQL templates and assigns everything to Helix modules (`BELONGS_TO_MODULE`).
* **100% Offline & Self-Contained**: Local SQLite database (`shonkor.db`) with FTS5 full-text search and recursive CTE subgraph queries. No external API dependencies.
* **Token-Optimized Context Capsule Synthesizer**: Generates prompt-ready Markdown files including automatic **Mermaid.js** architecture diagrams.
* **MCP Server (Model Context Protocol)**: Provides the graph directly to AI assistants like **Claude** and **Antigravity** with a token-efficient toolset that closes the agentic edit loop:
  * **Find**: `search_graph` (FTS5), `search_semantic` (vector/meaning), `locate`.
  * **Read**: `get_source` (exact symbol body + `file:start-end`), `get_subgraph`, `generate_capsule`.
  * **Analyze**: `impact_of` (who references it), `depends_on` (what it uses), `find_usages` (call sites with code snippets), `find_path` (shortest connection between two symbols), `implementations_of` (interface/base subtypes), `verify_exists` (anti-hallucination fact-check).
  * **Plan & apply**: `edit_plan` (a concrete edit checklist), `related_tests` (what to run after a change), `reindex_file` (refresh one file after editing).
  * **Memory**: `get_open_threads`, `record_decision`/`milestone`/`task`/`question`.
  * See the [LLM Integration Manual](docs/user/llm_integration.md) for the full reference.
* **Visual Web Dashboard**: A glassmorphic web interface with interactive 2D force-directed graph visualization (`force-graph`, WebGL Canvas), live physics, code preview (Prism.js), capsule creator, project and plugin management.
  * **Dual Search Modes**: Toggle between FTS5 Keyword Search (Network icon) and Vector-based Semantic Search (Brain icon).
  * **Ask AI (GraphRAG)**: Instantly generate AI answers based on the retrieved code context nodes using a local Ollama model directly in the dashboard UI.
  * **Impact & Dependencies panel**: Selecting a node shows its authoritative "Referenced by" / "Depends on" lists (with AI summaries), and a **Find Path** tool traces the shortest connection to any other symbol.
* **Multi-Project Registry**: Manage multiple codebases in parallel (`projects.json`), each with its own database.
* **Powerful CLI**: Automation via `init`, `index`, `search`, `capsule`, and `mcp`.

---

## ⚡️ Benchmark: AI Graphs vs. Classic RAG

In a commercial C# test codebase (50 classes), this benchmark compares the performance of a conventional search query (Fulltext RAG) with Shonkor's pre-generated semantic graph:

* **Token Requirement:** ~1,200 tokens (Shonkor) vs. ~9,800 tokens (Classic RAG) ➡️ **87.7% saved**
* **Context Latency:** ~6 seconds (Shonkor) vs. ~50 seconds (Classic RAG) ➡️ **7.6x faster**

Shonkor thus allows a **highly profitable operation** of LLM chatbots, since the expensive context is reduced to an absolute minimum without the LLM losing the architectural overview.

---

## 📁 System Structure

The project follows a clean **Clean Architecture** structure:

```
src/
  ├── Shonkor.Core/          # Domain models, interfaces, AST parser & capsule synthesizer
  ├── Shonkor.Infrastructure/# SQLite graph storage, crawler (SHA256), plugin loader, cross-tech linker
  ├── Shonkor.CLI/           # Console interface (init, index, search, capsule, mcp) + MCP server
  └── Shonkor.Web/           # Minimal APIs, API key middleware & glassmorphic web dashboard (wwwroot)
tests/
  └── Shonkor.Tests/         # Unit tests for parser, SQLite CTE, concurrency & type reference linking
docs/
  ├── developer/arc42/        # Developer documentation according to arc42 standard (Chapters 1-8)
  ├── user/                   # User manuals (setup, CLI, LLM integration)
  └── architecture/           # Architecture reviews
```

---

## 🚀 Quickstart

### Prerequisites
* [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### 1. Clone & Compile
```powershell
dotnet build
```

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
* **Dynamic Plugins** (runtime compilation of C#) are an RCE vector and are therefore **disabled by default** – opt-in via `Security:EnablePlugins=true`.
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
