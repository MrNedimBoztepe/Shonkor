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
shonkor index . --embed                                                  # → 241 files, 2.225 nodes, 5.546 edges
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
| Keyword (FTS5) | 90,0 % | 99,2 % |
| Vector only | 89,0 % | 99,3 % |
| **Hybrid** (keyword + vector) | **93,5 %** | **99,8 %** |

**You describe what you mean** — *"where do we hash API tokens?"* *(33 hand-labeled queries)*

| Search | Top hit correct | Found in top 10 |
|---|--:|--:|
| Keyword (FTS5) | 9,1 % | 18,2 % |
| Vector only | 48,5 % | 72,7 % |
| **Hybrid** (keyword + vector) | **45,5 %** | **81,8 %** |

**What this means, in one line:** keyword search is excellent when you know the name and **falls apart when you don't** — it finds the answer in the top ten less than **1 time in 5**. Fuse it with vector similarity (Reciprocal Rank Fusion) and that becomes **4 times in 5**.

**18 % → 82 % recall** on the questions people actually ask. And hybrid doesn't trade that off: it is *also* the best on exact names (**93,5 %** top-hit vs. 90,0 % for keyword alone). You never pick a mode — you get the better one.

*(Both sets are machine-checked for circularity, so a query can never secretly contain its own answer — see `--check-circularity`.)*

### 2. How much context does it save?

**458.972 → 120.126 tokens across 7 queries — 73,8 % fewer.**

**What this means:** Shonkor finds the relevant part of your graph, then instead of dumping every file it touched, it sends a **budgeted capsule** — the direct hits in full, the surrounding context ranked by structural closeness, and hub nodes capped so one popular class can't blow up the prompt.

> **Measured honestly.** That 73,8 % is against dumping *the same retrieved subgraph* in full — **not** against your whole repo. A "we save 95 % vs. your entire codebase" claim compares against a prompt nobody would ever send, and we won't make it. Every number here is DB-dependent and will differ on your codebase — which is exactly why the harness ships with the tool instead of only the results.

### 3. Does it beat plain vector RAG?

Head-to-head against **chunked RAG with no graph**, at a **matched token budget**, as a clean **2×2** — retrieval strategy (vector-only vs. hybrid) × graph (no / yes). The baseline's hybrid arm gets *exactly Shonkor's retrieval, minus the graph*, so the graph's contribution reads off the **like-for-like diagonal**, not a mixed comparison:

| Covers the target symbol | chunked-RAG (no graph) | Shonkor capsule (graph) |
|---|--:|--:|
| **vector-only seeds** | 84,8 % | 81,8 % |
| **hybrid seeds** | 84,8 % | **93,9 %** |

**The graph's isolated contribution is +9,1 pp** (93,9 % vs 84,8 %, same retrieval on both sides). Not the +6,1 we could have banked from the mixed comparison, and not the ≤0 a skeptic might have feared — a real, measured, like-for-like gain. (Tokens: baseline 9.067, Shonkor 9.372 — matched.)

> **The honest caveat, because a benchmark you only trust when it flatters you isn't one:** adding the keyword arm to the *baseline* changed nothing (84,8 → 84,8), because it returned any hit on only **10 of 33** queries. A raw 40-line source chunk doesn't keyword-match plain-English intent — *"how are api tokens hashed"* matches no chunk containing all those words. Shonkor's **nodes** do: each carries a **name** and an **AI summary** that read like intent. So part of the +9,1 is that the graph's *indexed unit* is keyword-matchable where a source chunk is not — a real advantage of the representation, named rather than hidden inside a topology claim.

And coverage is the *low* bar anyway — it only asks whether the target's text is somewhere in the blob. What it can't see is the **edges** the capsule ships: the call graph, exact signatures, the blast radius. That's the question chunks cannot answer **at any budget**: *"what breaks if I change this?"* A chunk retriever has none. It doesn't know.

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
