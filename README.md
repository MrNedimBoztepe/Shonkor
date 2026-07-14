# Shonkor 🧠

**Give your AI assistant a map of your codebase — not a pile of files.**

Shonkor indexes your code and docs into a local knowledge graph and hands your LLM exactly the context it needs: the right symbols, their real callers, and nothing else. **100 % offline. Compiler-accurate. Deterministic.**

```powershell
# Windows (macOS/Linux: scripts/install.sh)
irm https://raw.githubusercontent.com/MrNedimBoztepe/Shonkor/main/scripts/install.ps1 | iex

shonkor index .          # build the graph
shonkor mcp install      # Claude / Antigravity can now see it
```

---

## Why not just vector search?

Vector-only RAG **guesses**. It retrieves chunks that *look* similar and hopes the answer is in there.

Shonkor **knows**. It parses your code with **Roslyn** — plus JS/TS, PHP, GraphQL, Sitecore YAML and Markdown — and stores what the compiler actually sees: which class implements which interface, which method really calls which, what breaks if you touch this line. Then it fuses that hard structure with meaning-based vector search.

The result: ask *"how are API tokens hashed?"* and you get `TokenHasher.cs`. Ask *"what breaks if I change it?"* and you get the **true** blast radius — from real edges, not a similarity score.

> Your assistant stops inventing symbols that don't exist. And you stop paying for context you never needed.

---

## 📊 The numbers, in plain English

Every figure below comes from one reproducible harness (`src/Shonkor.Bench`) run on Shonkor's own graph. Don't take our word for it — run it on yours:

```powershell
dotnet run --project src/Shonkor.Bench -- shonkor.db                     # token reduction + exact-name retrieval
dotnet run --project src/Shonkor.Bench -- shonkor.db --set bench/golden/agent-queries.json   # plain-English queries
```

It writes `bench/report.md` (for you) and `bench/metrics.json` (for CI).

### 1. Does it find the right thing?

Two metrics, both simple:
* **Top hit correct** = how often the *very first* result is the one you wanted. *(Precision@1)*
* **Found in top 10** = how often the right answer is *somewhere* in the first ten. *(Recall@10)*

| You search by… | Top hit correct | Found in top 10 |
|---|--:|--:|
| **Exact symbol name** (`SqliteGraphStorageProvider`) — keyword search | **90 %** | **99 %** |
| **Plain-English intent** ("where do we hash tokens?") — keyword search alone | **0 %** | 12 % |

**What this means:** if you already know the name, plain keyword search nails it 9 times out of 10. If you describe what you *mean*, keyword search is **useless** — 0 % top-hit.

That gap is exactly why Shonkor doesn't stop at keywords. It fuses keyword + **vector similarity** (Reciprocal Rank Fusion) so intent-phrased questions land on the right symbol. Those rows need a local embedding backend and are measured in a nightly CI gate, so we don't pin a flattering number here we can't reproduce on your machine.

*(200 auto-generated exact-name cases + 33 hand-labeled English queries, machine-checked for circularity so a query can never secretly contain its own answer.)*

### 2. How much context does it save?

**931.030 → 133.423 tokens across 7 queries — 85,7 % fewer.**

**What this means:** Shonkor finds the relevant part of your graph, then instead of dumping every file it touched, it sends a **budgeted capsule** — the direct hits in full, the surrounding context ranked by structural closeness, and hub nodes capped so one popular class can't blow up the prompt.

Same question answered, roughly **one seventh** of the tokens.

> **Measured honestly.** That 85,7 % is against dumping *the same retrieved subgraph* in full — **not** against your whole repo. A "we save 95 % vs. your entire codebase" claim compares against a prompt nobody would ever send. We don't do that. Every number is DB-dependent — it will differ on your codebase, which is why the harness ships with the tool.

---

## ✨ What you get

* **A real graph, not a blob.** Namespaces, classes, interfaces, records, methods, properties — with `IMPLEMENTS` / `EXTENDS` / `CALLS` / `REFERENCES_TYPE` edges. Semantic C# resolution is **on by default**, so same-named types across namespaces are told apart. Exactly-resolved edges are tagged `EXTRACTED`; heuristic guesses are honestly downgraded to `INFERRED` — your assistant can see which is which.
* **Multi-language:** C# (Roslyn), JavaScript/TypeScript, PHP, GraphQL, Sitecore SCS (YAML), Markdown. A cross-tech linker wires Next.js components ↔ Sitecore renderings ↔ C# controllers.
* **MCP server** — the graph plugs straight into **Claude** and **Antigravity**. Your assistant can *find* (`search_hybrid`, `locate`), *read* (`get_source`, `outline`, `generate_capsule`), *analyze* (`references`, `call_hierarchy`, `find_path`, `verify_exists`), *plan & apply* (`edit_plan`, `rename_plan`, `related_tests`, `check_edit`), and *stay fresh* (`freshness` auto-flags results whose file changed since indexing). Full list: [LLM Integration Manual](docs/user/llm_integration.md).
* **Visual dashboard** — an interactive force-directed graph, code preview, hybrid search, and **grounded AI answers** streamed token-by-token with per-claim source citations.
* **100 % offline.** One local SQLite file. No API keys, no data leaving your machine. Optional local **Ollama** adds embeddings and summaries.
* **Multi-project.** Manage several codebases side by side, each with its own graph.

---

## 🚀 Get started

**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (or use the prebuilt binary above — no SDK needed).

```powershell
shonkor init                          # create shonkor.json
shonkor index . --embed               # build the graph (--embed adds semantic search, needs Ollama)
shonkor search "RoslynAstParser"      # find a symbol
shonkor capsule "RoslynAstParser" --hops 2 --out capsule.md   # prompt-ready context
shonkor mcp install                   # register with Claude Desktop / Claude Code / Antigravity
```

**Web dashboard:**
```powershell
cd src/Shonkor.Web && dotnet run      # → http://localhost:5290
```

---

## 🔐 Security in one breath

Local-first by design. If you do put it behind a proxy: API tokens are stored **SHA-256 hashed** (never plaintext, constant-time comparison), the loopback auth bypass is **Development-only**, plugins are **pre-built assemblies that are inert until you explicitly activate them** (there is no runtime source compilation — that RCE surface was removed), webhooks verify HMAC signatures and **fail closed**, and the filesystem browser is loopback-only.

---

## 📚 Documentation

| | |
|---|---|
| **[Setup Guide](docs/user/setup_guide.md)** | Onboarding, configuration, security, multi-project |
| **[CLI Reference](docs/user/cli_reference.md)** | Every command, with examples |
| **[LLM Integration](docs/user/llm_integration.md)** | MCP for Claude / Antigravity / Cursor — the full tool reference |
| **[Architecture (arc42)](docs/developer/arc42/README.md)** | For contributors |

---

## ⚖️ License

MIT — see [LICENSE](LICENSE).
