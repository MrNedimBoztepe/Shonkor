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

### Step 3: Node Runtime (for JS/TS Analysis)
JS/TS analysis is not part of the host: it lives in the first-party `shonkor-typescript` plugin, which `StandardPluginSeeder` installs and activates automatically in a fresh workspace. The plugin drives a **Node sidecar running the real TypeScript Compiler API**, which is what makes `.ts/.tsx/.js/.jsx` analysis semantic rather than syntactic.

**Prerequisite: Node ≥ v18** (`NodeDiscovery.RequiredMajorVersion`; a current LTS is recommended). Node is **bring-your-own** — Shonkor does not bundle it. You do *not* need `npm` at use time: the pinned `typescript` package travels inside the plugin package.

1. Install Node from [nodejs.org](https://nodejs.org/). v18/20/22/24 are all admitted; older lines (14/16) are rejected by the version gate.
2. Verify with `node --version`.

**How Shonkor finds it.** Candidates are tried in this order, each validated by a single, bounded `node --version` probe; the first one that answers with a high-enough major version wins. Discovery is never per file, but it is also not once per scan: the parser resolves Node once for the whole scan, and `TypeScriptSemanticLinker` resolves it again independently for its own pass — so a full scan of a JS/TS repo probes the candidate list twice.

1. The configured `NodePath` (see below). When set it is **authoritative**: if it does not resolve to a usable Node, Shonkor degrades rather than silently using a different Node from `PATH` — falling through would mask the misconfiguration.
2. `node` / `node.exe` resolved via `PATH`.
3. Common install locations — Windows: `%ProgramFiles%\nodejs\node.exe`, `%ProgramFiles(x86)%\nodejs\node.exe`, `%APPDATA%\npm\node.exe`; Linux/macOS: `/usr/local/bin/node`, `/usr/bin/node`, `/opt/homebrew/bin/node`, `$HOME/.volta/bin/node`, `$HOME/.local/bin/node`.

**Pinning the path.** If your Node lives elsewhere (a version-manager shim, a portable install), set it in the plugin's own config file, `plugins/shonkor-typescript/sidecar.settings.json` inside your workspace:
```json
{
  "NodePath": "C:\\tools\\node-v22\\node.exe",
  "TimeoutSeconds": 30
}
```
`NodePath: null` (the shipped default) means auto-discover; `TimeoutSeconds` is the per-file parse budget in seconds (default 30). A missing or malformed file is not fatal — the defaults apply.

#### What happens without Node
Indexing **does not stop and does not fail**. JS/TS files are still parsed, by the plugin's private Esprima fallback (`EsprimaFallbackParser`) — the same tolerant parse Shonkor ran in-process before the plugin existed. The same degradation applies to a Node that is present but **older than v18** (the gate rejects it instead of starting a sidecar that would fail cryptically) and to a sidecar parse that exceeds `TimeoutSeconds`.

What the fallback costs you, concretely:
* Only the coarse JSComponent + IMPORTS shape from a syntactic parse — no resolved symbols, so no class/interface/function/method nodes from the real TS AST.
* No cross-file semantic edges (CALLS, REFERENCES_TYPE, OVERRIDES, IMPLEMENTS_MEMBER): `TypeScriptSemanticLinker` needs the type checker and skips its pass entirely.
* A file whose advanced TS syntax Esprima cannot tolerate still yields its component node, but its imports are dropped.

**How you notice — the two channels are not equally visible, so read this precisely:**

* **As data (one diagnostic per index).** `TypeScriptSemanticLinker` records a diagnostic with code `typescript.semantic-linker` and severity `Info`, stating that Node was unavailable and the cross-file semantic edges were skipped. Read it with the `get_diagnostics` MCP tool (which applies no severity filter unless you pass one) or via `GET /api/diagnostics?minSeverity=info`. Two caveats: it is only emitted when the scan actually found typed TS (`.ts`/`.tsx`) files — a pure `.js`/`.jsx` codebase produces none — and the dashboard's Diagnostics panel defaults to `warning+`, so you must switch its severity filter to `info` to see it there.
* **In the log only (the per-file degradation).** That *each* JS/TS file was parsed with the fallback instead of real TS semantics is a log warning and nothing more — `TypeScriptParser` implements `IFileParser`, whose `ParseAsync` returns nodes and edges and has no diagnostics channel, so there is nowhere for it to become data. On the CLI it appears on stderr, once per affected file (`TypeScript parser: Node sidecar unavailable; using Esprima fallback. (<path>)`), alongside the single scan-level line that names the cause (`TypeScript Node sidecar unavailable: <reason> JS/TS files will be parsed with the Esprima fallback.`). Neither appears in the dashboard.

The unavailable message itself is actionable rather than generic: it names the required version, links `https://nodejs.org`, and distinguishes "no Node found at all" from "found, but too old" and from "your configured `NodePath` is not usable".

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

> [!NOTE]
> **Node in the container.** The repository's Dockerfile ships no Node runtime in the **final image** — its runtime stage adds only `curl` on top of the .NET base image. (The *build* stage does carry Node, but only so `npm ci` can materialise the plugin's sidecar deps at build time; that Node never reaches the runtime image.) If you index JS/TS from inside the container, check with `docker compose exec shonkor-web node --version` (`shonkor-web` is the service name in `docker-compose.yml`) and add Node ≥ v18 to the image if it is missing; otherwise JS/TS analysis runs on the Esprima fallback described in Step 3.

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

**Request timeouts.** `SemanticAnalyzer:TimeoutSeconds` (default **120**) bounds a single generation — an "Ask AI" answer or one enrichment summary — and `EmbeddingService:TimeoutSeconds` (default **60**) bounds one embedding call. Raise the first if a large model on slow hardware is being cut off mid-answer; lower it if you would rather Ask AI failed fast than made you wait. Note the asymmetry, which is deliberate: a timed-out **Ask AI** request is *not* retried (retrying would just double a wait you are already sitting through), while a timed-out **background enrichment** call is.

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
* **Plugins**: A plugin is a **pre-built assembly** installed from a ZIP, and it is **inert until you explicitly activate it** — installing one runs nothing. There is **no runtime compilation of plugin source** (that path, an arbitrary-code-execution surface, has been removed). The trust gate is **per-plugin activation** (`shonkor plugin activate <id>`), so treat activating a plugin like running its code — because you are. `Security:EnablePlugins` is a **kill switch that defaults to ON**: set `Security:EnablePlugins=false` to hard-disable loading *every* plugin regardless of its activation state. Plugin state changes over the web API are loopback-only.
* **File System Browser**: `/api/browse` is only accessible locally/in Development (`Security:AllowFilesystemBrowse`).
* **Webhooks**: `/api/webhooks/github/*` verify `X-Hub-Signature-256` (HMAC-SHA256) against `GitHub:WebhookSecret` and fail without a secret (fail-closed).

---

## 🤖 Registering the MCP Server

So that AI assistants (Claude, Antigravity) can query the graph live:
```powershell
dotnet run --project src/Shonkor.CLI -- mcp install
```
Then restart the client. Details: [LLM Integration Manual](llm_integration.md).
