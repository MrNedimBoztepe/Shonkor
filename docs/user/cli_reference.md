# Shonkor CLI Reference Manual 💻

This manual describes the syntax and usage of all Shonkor command-line commands.

---

## ⌨️ Global Syntax

```powershell
shonkor <command> [arguments] [options]
```

---

## 🛠️ Command Reference

### 1. `init`
Initializes a default configuration file in the current working directory.

* **Syntax:** `shonkor init`
* **Description:** Checks if a `shonkor.json` already exists. If not, a new file with default values (ignoring `bin`, `obj`, `.git`, `node_modules` and storing the database in `shonkor.db`) is created.
* **Example:**
  ```powershell
  shonkor init
  ```

---

### 2. `index`
Scans the specified directory and builds the semantic knowledge graph.

* **Syntax:** `shonkor index [directory] [options]`
* **Arguments:**
  * `[directory]` *(Optional)*: The path to the directory to scan. Defaults to the current directory (`.`).
* **Options:**
  * `-c, --config <file>`: Path to the configuration file (Default: `shonkor.json`).
* **Example:**
  ```powershell
  # Indexes the current directory with default configuration
  shonkor index
  
  # Indexes a different directory with a specific configuration
  shonkor index C:\Projects\MyProject --config MyProjectConfig.json
  ```

---

### 3. `search`
Executes lightning-fast full-text search (FTS5) on the content and names of all indexed nodes.

* **Syntax:** `shonkor search <query> [options]`
* **Arguments:**
  * `<query>`: The search term. Supports SQLite FTS5 syntax (e.g. wildcards with `*`).
* **Options:**
  * `-l, --limit <number>`: Maximum number of returned results (Default: `10`).
  * `-c, --config <file>`: Path to the configuration file (Default: `shonkor.json`).
* **Example:**
  ```powershell
  # Searches for definitions containing 'Roslyn'
  shonkor search "Roslyn"
  
  # Searches for classes ending with 'Parser' and limits the output to 5
  shonkor search "Parser*" --limit 5
  ```

---

### 4. `capsule`
Generates a highly precise, token-optimized Markdown context capsule for LLMs.

* **Syntax:** `shonkor capsule <query> [options]`
* **Arguments:**
  * `<query>`: The search term to identify the starting nodes (seeds) for the graph query.
* **Options:**
  * `-h, --hops <number>`: The depth of the graph expansion (Default: `2`). Higher values include indirect dependencies.
  * `-o, --out <path>`: Path to the Markdown file to be generated (Default: `shonkor-capsule.md`).
  * `-c, --config <file>`: Path to the configuration file (Default: `shonkor.json`).
* **Example:**
  ```powershell
  # Generates a 2-hop capsule for 'SqliteGraphStorage'
  shonkor capsule "SqliteGraphStorage" --hops 2 --out SqliteCapsule.md
  ```

---

### 5. `mcp`
Starts the **Model Context Protocol (MCP)** server via stdio (JSON-RPC). AI assistants like **Claude** and **Antigravity** use this to directly integrate the knowledge graph.

* **Syntax:** `shonkor mcp [options]`
* **Options:**
  * `-c, --config <file>`: Path to the configuration file (Default: `shonkor.json`).
* **Behavior:** The server runs until the input stream is closed (EOF). Normally, it is **not started manually**, but automatically as a subprocess by the MCP client (Claude/Antigravity).
* **Project Resolution:** The active project is derived **from the working directory** (the directory where the client starts the server) – not from a global flag. Override via the `SHONKOR_PROJECT` environment variable.

#### `mcp install`
Automatically registers Shonkor in the MCP configuration files of detected clients (Claude Desktop, Antigravity).

* **Syntax:** `shonkor mcp install`
* **Example:**
  ```powershell
  dotnet run -- mcp install
  ```

> [!TIP]
> For reproducible operation, it is recommended to run `dotnet publish` and point to the published `.exe` in the client configuration, rather than pointing to `bin/Debug`.

#### `mcp-proxy`
Bridges a local AI assistant to a **remote** Shonkor (SaaS) graph: it forwards stdio JSON-RPC to HTTP POSTs against the server's `/api/mcp/relay` endpoint, so the assistant talks to the hosted graph as if it were local.

* **Syntax:** `shonkor mcp-proxy --url <relayUrl> [--project <name>]`
* **Options:**
  * `--url <url>`: The remote relay endpoint, e.g. `https://shonkor.yourdomain.com/api/mcp/relay`.
  * `--project <name>`: Tenant/project to bind to (optional if `SHONKOR_PROJECT` is set; ignored when the server authenticates the tenant from the API key).
* **Example:**
  ```powershell
  shonkor mcp-proxy --url https://shonkor.example.com/api/mcp/relay --project MyProject
  ```

---

## 📊 Interpreting the Output

During indexing (`index`), Shonkor outputs detailed metrics:
* **Files Scanned**: Number of physical files analyzed by the registered parsers (binary files are detected via NUL bytes and skipped).
* **Nodes Created**: Number of generated code and document signatures (e.g. classes, methods).
* **Edges Created**: Number of logical relationships, e.g. `CONTAINS` (file→type→member), `IMPLEMENTS`/`EXTENDS` (inheritance), `REFERENCES_TYPE` (type usage), `IMPORTS`, `BINDS_TO`, `BELONGS_TO_MODULE`.
* **Composition by Type**: Overview of all node types in your database (important for validating coverage).
