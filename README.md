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

Every figure below comes from one reproducible harness (`src/Shonkor.Bench`). **Reproduce them exactly** — this is the graph the numbers are measured on, no hidden enrichment step:

```powershell
shonkor index . --embed                                                  # → 231 files, 2.071 nodes, 5.152 edges
dotnet run --project src/Shonkor.Bench -- shonkor.db                     # token reduction + exact-name retrieval
dotnet run --project src/Shonkor.Bench -- shonkor.db --set bench/golden/agent-queries.json --compare-rag
```

<sub>Measured **2026-07-14**, Ollama **`nomic-embed-text`** (768-dim) — swap the model and the numbers move. Raw harness output is checked in: [`metrics-exactname.json`](bench/metrics-exactname.json), [`metrics-agent-queries.json`](bench/metrics-agent-queries.json). A test asserts these tables still match those files, so they cannot silently rot.</sub>

### 1. Does it find the right thing?

Two metrics, both simple:
* **Top hit correct** = how often the *very first* result is the one you wanted. *(Precision@1)*
* **Found in top 10** = how often the right answer is *somewhere* in the first ten. *(Recall@10)*

**You already know the name** — `SqliteGraphStorageProvider` *(200 cases)*

| Search | Top hit correct | Found in top 10 |
|---|--:|--:|
| Keyword (FTS5) | 89,0 % | 99,1 % |
| Vector only | 90,5 % | 98,8 % |
| **Hybrid** (keyword + vector) | **94,5 %** | **99,8 %** |

**You describe what you mean** — *"where do we hash API tokens?"* *(33 hand-labeled queries)*

| Search | Top hit correct | Found in top 10 |
|---|--:|--:|
| Keyword (FTS5) | 9,1 % | 18,2 % |
| Vector only | 48,5 % | 69,7 % |
| **Hybrid** (keyword + vector) | **48,5 %** | **78,8 %** |

**What this means, in one line:** keyword search is excellent when you know the name and **falls apart when you don't** — it finds the answer in the top ten less than **1 time in 5**. Fuse it with vector similarity (Reciprocal Rank Fusion) and that becomes **4 times in 5**.

**18 % → 79 % recall** on the questions people actually ask. And hybrid doesn't trade that off: it is *also* the best on exact names (**94,5 %** top-hit vs. 89,0 % for keyword alone). You never pick a mode — you get the better one.

*(Both sets are machine-checked for circularity, so a query can never secretly contain its own answer — see `--check-circularity`.)*

### 2. How much context does it save?

**481.539 → 115.978 tokens across 7 queries — 75,9 % fewer.**

**What this means:** Shonkor finds the relevant part of your graph, then instead of dumping every file it touched, it sends a **budgeted capsule** — the direct hits in full, the surrounding context ranked by structural closeness, and hub nodes capped so one popular class can't blow up the prompt.

> **Measured honestly.** That 75,9 % is against dumping *the same retrieved subgraph* in full — **not** against your whole repo. A "we save 95 % vs. your entire codebase" claim compares against a prompt nobody would ever send, and we won't make it. Every number here is DB-dependent and will differ on your codebase — which is exactly why the harness ships with the tool instead of only the results.

### 3. Does it beat plain vector RAG?

Head-to-head against **chunked RAG with no graph**, at a **matched token budget** — the baseline takes as many top text chunks as fit into Shonkor's token count, so this compares *coverage at equal cost*:

| At ~equal tokens | Tokens delivered | Covers the target symbol |
|---|--:|--:|
| chunked-RAG (no graph) | 8.660 | 87,9 % |
| Shonkor capsule — *vector-only seeds* | 8.940 | 84,8 % |
| **Shonkor capsule — as shipped** | 8.940 | **93,9 %** |

**+6,1 pp** over the no-graph baseline — and there's a story in the middle row worth telling.

That row is Shonkor seeded by **vector search alone**, and it *loses* by 3,1 pp. It is in the table because it isolates the graph's contribution — both sides then start from identical retrieval. But it is **not what the product does**: the shipped path seeds from **hybrid** retrieval (the same RRF you saw in section 1). Our own benchmark had been handicapping Shonkor against itself, and it took repairing the metric to notice.

The diagnosis is measured, not argued: **100 %** of seeds survive the capsule budget, and in **5 of 33** vector-only misses the target was **never a seed at all**. The budget wasn't dropping the answer — retrieval never found it. Better seeding fixed it.

> **The asymmetry we're not hiding:** the baseline is vector-only, and Shonkor's winning row is hybrid. That's the conventional "naive RAG" setup, but a fair critic would say: give the chunks a keyword arm too. They'd be right, and it's [an open ticket](https://github.com/MrNedimBoztepe/Shonkor/issues). Until it's measured, read this as *"the graph capsule beats naive chunked RAG"* — not as *"vector retrieval is beaten"*.

And coverage is the *low* bar anyway — it only asks whether the target's text is somewhere in the blob. What it cannot see is what surrounds it: the capsule ships the **call graph, exact signatures, and the edges**. So the real reason isn't the 6 points. It's the question chunks **cannot answer at any budget**: *"what breaks if I change this?"* A chunk retriever has no edges. It doesn't know.

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
