# Shonkor Setup & Onboarding Guide ⚙️

This manual describes the initial installation, configuration, and quick start with Shonkor in your local project workspace.

---

## 🚀 First Steps & Installation

Since Shonkor is designed as a **100% self-contained** solution, it requires neither an external database server nor complex Docker containers. All you need is the .NET 10 SDK.

### Step 1: Compile
Navigate to the root directory of the project and execute the build command:
```powershell
dotnet build
```
After a successful build, the CLI tool and the Web Dashboard will be available to you.

### Step 2: Local LLM Setup (Ollama)
For semantic search and the built-in "Ask AI" GraphRAG feature to work, Shonkor requires a local Ollama instance.
1. Install [Ollama](https://ollama.com/).
2. Run it locally (it defaults to port `11434`).
3. Pull the default coder model:
   ```powershell
   ollama run qwen2.5-coder
   ```
*(If you do not install Ollama, Shonkor will still work with FTS5 Keyword Search, but Semantic Search and Ask AI will be disabled).*

---

## 🐳 Docker Deployment (Alternative)

Instead of compiling Shonkor locally, you can run the entire stack (Shonkor Web Dashboard + Ollama) using Docker Compose.

### Step 1: Configure Workspace
Rename `.env.example` to `.env` in the root directory.
Edit `.env` to point `TARGET_PROJECTS_DIR` to your primary projects folder (e.g., `C:\Projects` or `~/workspace`). This folder will be mounted into the container at `/projects`.

### Step 2: Start the Stack
Run the following command from the repository root:
```bash
docker compose up -d --build
```
This will:
1. Build the Shonkor .NET container (runs as a non-root user, with a `HEALTHCHECK` on `/health/ready`).
2. Spin up an Ollama container and automatically pull **both** models: `qwen2.5-coder` (summaries) and `nomic-embed-text` (embeddings for semantic search). The web container waits until Ollama is healthy before starting.
3. Expose the dashboard at `http://localhost:5290`.

**Health probes** (also used by container/Kubernetes orchestration):
* `GET /health` and `/health/live` — liveness (the process is up). Public, no API key.
* `GET /health/ready` — readiness (the project workspace is writable and the active graph store answers). Gate traffic on this one.

*Note: If you have an NVIDIA GPU, edit `docker-compose.yml` and uncomment the `deploy` section under the `ollama` service for massive performance gains.*

### Prebuilt image (CI/CD)
Every push to `main` builds and publishes the Linux image to the GitHub Container Registry via the `.github/workflows/cd.yml` pipeline, so you can also pull `ghcr.io/<owner>/shonkor:latest` instead of building locally.

---

## 🛠️ Configuration (`shonkor.json`)

The first step in any new project workspace is the initialization of the configuration file. Open your terminal in the root directory of your target project and run:

```powershell
# Creates a default shonkor.json in the current directory
shonkor init
```

### The Structure of `shonkor.json`

The generated file has the following format:
```json
{
  "databasePath": "shonkor.db",
  "excludePatterns": [
    "**/bin/**",
    "**/obj/**",
    "**/.git/**",
    "**/.vs/**",
    "**/.idea/**",
    "**/node_modules/**",
    "**/*.db",
    "**/*.log"
  ]
}
```

### Explanation of Parameters:
1. **`databasePath`**: The path to the local SQLite database. By default, `shonkor.db` is created directly in the current directory. You can change this path as desired (e.g., to a hidden directory `.shonkor/brain.db`) to keep your workspace clean.
2. **`excludePatterns`**: A list of glob patterns for files and directories that the crawler should ignore. 
   > [!TIP]
   > **Performance Tip**: Consistently exclude build folders (`bin`, `obj`), dependencies (`node_modules`, `vendor`), and version control folders (`.git`). This massively accelerates the crawler and prevents unnecessary bloat in the graph database.

---

## 🔍 Initial Indexing

After you have configured your `shonkor.json`, execute the indexing:

```powershell
shonkor index .
```

The crawler will now recursively analyze all supported files, extract the syntactic structures, and save the result. At the end, you will see a detailed summary of the scanned files, generated nodes (classes, methods), and edges (dependencies, implementations).

### Incremental Updates (SHA256)
With each subsequent call to `shonkor index`, the system uses SHA256 content hashes to detect changed files. Only modified files are deleted and re-parsed – unchanged files are skipped. This saves valuable computing time in large codebases. Binary files are detected based on NUL bytes in the header and are skipped.

Files are parsed in parallel, and stale/changed files are cleared in a **single batched transaction** (instead of one transaction per file), so the write path stays constant-cost regardless of how many files changed — fast even on a first index or a branch switch.

Each graph also records the **node-id scheme version** it was built under (SQLite `PRAGMA user_version`). When a Shonkor upgrade changes the id format (e.g. arity-discriminated method ids), the file content is unchanged — so the next `shonkor index` **force-reparses** every file to migrate the ids, then re-stamps the version. `get_stats` (and the MCP `get_stats` tool) report `SchemeVersion`/`CurrentSchemeVersion` and a `ReindexRecommended` hint if a graph is still on an older scheme.

### Exact C# resolution (default)
C# type references are resolved **exactly** via a Roslyn `SemanticModel` — disambiguating same-named types across namespaces and additionally producing method-level `CALLS` edges. This is what makes impact/rename analysis precise for C#, and it is now the **default**. It is **non-lossy**: references a partial or non-compiling checkout can't resolve fall back to name matching, so it is never worse than the old syntactic resolver — only more precise.

Trade-off: it builds a Roslyn compilation per scan. On this repo's `src` (168 files) that is ~+3.6 s (~2.9×) for ~50 % more, more-precise edges; the cost scales with the amount of C# source. To force the faster name-based resolver:

* **Per project:** set `"SemanticCSharp": false` on a project entry in `projects.json` (wins over the global setting) — e.g. keep one very large project on the fast name path while the rest run semantic.
* **Web / SaaS global:** set `Indexing:SemanticCSharp=false` (e.g. `Indexing__SemanticCSharp=false` as an env var).
* **CLI:** `SHONKOR_SEMANTIC_CSHARP=false shonkor index .`

It needs no project build: intra-codebase symbols resolve from the source itself; references into un-referenced third-party types are simply skipped.

### Embedding source & semantic search
Semantic (vector) search embeds a **structured code document** per node — `type + name + signature + summary + bounded body` — not just the AI summary, which measurably improves natural-language ("intent") retrieval. Configure via `Embedding:Source` (`code` (default) | `summary`). Query and index embeddings are kind-aware; optional nomic task prefixes are available via `EmbeddingService:QueryPrefix` / `EmbeddingService:DocumentPrefix` (default off). Each stored vector records its **dimension and model**, so changing the embedding model (even to another of the same dimension) re-embeds affected nodes on the next enrichment cycle instead of silently mixing vector spaces in search.

### AI & tool settings (dashboard or config)
The AI/tool settings can be set two ways:

* **In the dashboard** — Settings → **AI** tab: Ollama URL + generation model, embedding URL + model, embedding source (`code`/`summary`), semantic-C# default, answer streaming, and the enrichment batch size / parallelism. Saving writes them and they take effect on the next request/enrichment cycle (the drift-worker interval needs a restart).
* **In config / env** — the same keys in `appsettings.json` or as environment variables (`SemanticAnalyzer:OllamaUrl`, `EmbeddingService:OllamaModel`, `Embedding:Source`, `Indexing:SemanticCSharp`, `Features:StreamingAnswers`, `SemanticEnrichment:*`, `Drift:ReconcileIntervalSeconds`).

Precedence & safety:
* Dashboard writes go to a machine-local, **gitignored `appsettings.Local.json`** overlay (loaded with `reloadOnChange`). It overrides `appsettings.json` but sits **below environment variables**, so a Docker/k8s env config still wins over a local dashboard edit.
* Writing settings changes server behaviour, so `POST /api/settings` is **loopback-only** and disabled outside Development unless you set `Security:AllowSettingsWrite=true`. **Secrets** (API keys, webhook secret) are never exposed or written here — keep them in user-secrets / env.

---

## 🖥️ Web Dashboard

For visual exploration, start the dashboard:
```powershell
cd src/Shonkor.Web
dotnet run
# -> http://localhost:5290
```
The dashboard offers graph visualization, search, capsule creation, as well as the management of multiple projects and (optional) plugins.

---

## 🗂️ Multi-Project Registry (`projects.json`)

Shonkor can manage multiple codebases in parallel. The registry is located in the workspace root as `projects.json`:
```json
{
  "Projects": [
    { "Name": "MyProject", "Path": "C:\\Projects\\MyProject", "DatabasePath": "C:\\Projects\\MyProject\\shonkor.db", "ApiKey": "" }
  ],
  "ActiveProjectName": "MyProject"
}
```
> [!IMPORTANT]
> `projects.json` can contain API keys and is therefore **gitignored**. Never commit it.

* **Web Dashboard**: uses `ActiveProjectName` as the displayed project (switchable in the UI).
* **MCP Server**: ignores `ActiveProjectName` and derives the project **from the working directory**. Both are decoupled – the dashboard does not affect which project the AI assistant sees.

---

## 🔐 Security & Secrets

Shonkor is primarily a **local** tool. For proxy/SaaS operation, please note:

* **Tokens are stored hashed**: project API keys and user tokens are persisted as **SHA-256 hashes**, not plaintext. Comparison is constant-time, and any legacy plaintext in `projects.json` is migrated to a hash automatically on load. A newly created user's token is returned **once** — store it then; it cannot be recovered later.
* **Never put secrets in files**: API keys and webhook secrets belong in user secrets or environment variables, not in `appsettings.json`/`projects.json`:
  ```text
  ApiKeys__sk-your-key=ProjectName
  GitHub__WebhookSecret=<your-secret>
  SaaS__TenantRoot=C:\Projects\SaaS   # optional
  ```
* **Loopback Bypass**: The local dashboard is only allowed to bypass the API key in `Development`. In production (behind a proxy), a valid key is always required. Override: `Security:AllowLocalBypass`.
* **Dynamic Plugins (RCE Risk)**: The runtime compilation of C# plugins is **disabled by default**. Only activate consciously via `Security:EnablePlugins=true`; the plugin wizard endpoint is additionally restricted to local/Development access.
* **File System Browser**: `/api/browse` is only accessible locally/in Development (`Security:AllowFilesystemBrowse`).
* **Webhooks**: `/api/webhooks/github/*` verify `X-Hub-Signature-256` (HMAC-SHA256) against `GitHub:WebhookSecret` and fail without a secret (fail-closed).

---

## 🤖 Registering the MCP Server

So that AI assistants (Claude, Antigravity) can query the graph live:
```powershell
dotnet run --project src/Shonkor.CLI -- mcp install
```
Then restart the client. Details: [LLM Integration Manual](llm_integration.md).
