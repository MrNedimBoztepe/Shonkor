# Changelog

All notable changes to Shonkor are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/), and the project uses semantic versioning.

## [Unreleased]

### Added — Eval re-contamination is now a loud failure, not a silent filter (#136)
- `IsEvalMetaNode` silently drops index-excluded meta files from the ranked results (#132). That keeps the
  numbers correct **today**, but if the `shonkor.json` exclude regresses and those files get indexed again,
  the filter quietly patches over it — the contamination returns invisibly, the exact failure this session
  keeps closing.
- The benchmark now **records** any index-excluded meta file (`bench/golden`/`tickets`/`review`) that surfaces
  in *any* retriever's results and **exits 2**, naming the offenders — like the `--baseline` and
  `--check-circularity` gates. On a correctly-indexed graph the set is empty (those dirs aren't in it), so the
  gate is silent in normal operation and only fires on real re-contamination.
- **Live-verified:** a `tickets/*.md` indexed because its exclude was forgotten surfaces in retrieval and the
  bench exits 2 naming the file. Unit-tested end-to-end through `RetrievalBenchmark.RunAsync` plus the
  classifier's boundary (index-excluded vs. the guard-only `bench/*.md`).
- Noted while building it: the *acute* #110 case — a `bench/golden/*.json` fixture ranking as a circular hit —
  is structurally **unreachable via real indexing**, because the indexer has no `.json` parser, so those files
  never become graph nodes. The reachable re-contamination path is `tickets/`/`review/` **markdown**. The gate
  is generic over `MetaDirectories`, so it covers both.

### Changed — The eval's two meta-exclusion layers can no longer drift apart (#140)
- De-contamination (#132/#133) put the meta-doc exclusion in **two** places that must agree: `shonkor.json`
  `ExcludePatterns` keeps `bench/golden`/`tickets`/`review` out of the graph at **index time**, and
  `RetrievalBenchmark.IsEvalMetaNode` ignores them at **measurement time** (defence in depth). Add a new meta
  dir to one and forget the other, and the eval quietly re-contaminates (missing from the config) or the
  config over-excludes — silently, with no failing signal.
- The guard's directory list is now a single named constant (`RetrievalBenchmark.MetaDirectories`), and
  **`EvalExcludeSyncTests` bridges it to the config**: every directory the guard treats as meta must also be
  excluded from indexing in `shonkor.json`. The test does not hardcode the list (that would be a third copy to
  drift); it reads the constant. The intentional guard-only case (`bench/*.md` — indexed for agents, ignored
  only by the eval) is explicitly whitelisted. **Mutation-verified:** dropping `review` from the config fails
  the test with an actionable message.

### Fixed — get_subgraph's size cap no longer rests on an accidental row order (#170)
- The verbose size cap (#117) drops a **tail prefix** of the node list to stay under `maxChars`, which is only
  correct if the list is ordered **nearest-first** — otherwise it silently evicts the *seeds* and keeps distant
  hub nodes, valid JSON and all, with no failing signal (the #157 class).
- The comment claimed `GetSubgraphAsync` returns nodes breadth-first, but the query had **no `ORDER BY` at
  all** — the order was an accident of SQLite's row output, not a guarantee. TICKET-215's CTE rework happened
  to preserve it; the next change might not.
- The CTE now sorts explicitly (`ORDER BY Depth, Id` — the depth it already computed), so nearest-first is a
  **property of the query**. The ordering contract is documented on `IGraphSearch.GetSubgraphAsync`, and
  `SubgraphOrderingContractTests` enforces it: seeds first, then by hop distance, ties by id. **Mutation-
  verified** — removing the `ORDER BY` fails all three tests, so a future query change that breaks the order
  fails loudly instead of degrading the cap in silence.

### Changed — The RAG head-to-head is now a clean 2×2; the graph's contribution is isolated (#166)
- The published win (**+6,1 pp** vs. chunked-RAG) compared **Shonkor-hybrid against baseline-vector-only** —
  one side had a keyword arm, the other didn't. So it could not tell whether the gap was **the graph** or
  merely **hybrid retrieval**, a technique the baseline could adopt with no graph at all.
- `--compare-rag` now gives the baseline the **same retrieval Shonkor gets, minus the graph**: BM25 over the
  chunk texts (an in-memory SQLite **FTS5** index — the *same* engine the product uses, not a hand-rolled
  scorer) RRF-fused with the vector ranking. The report is a **2×2** (retrieval strategy × graph), and the
  graph's contribution reads off the **like-for-like hybrid diagonal**.
- **Measured, and it refutes the ticket's own fear:** the graph's isolated contribution is **+9,1 pp**
  (93,9 % vs 84,8 %, hybrid on both sides), not the ≤0 a skeptic might have expected. But the honest caveat is
  published next to it: adding the keyword arm to the *baseline* changed nothing (it fired on only **10 of 33**
  queries — a raw 40-line source chunk doesn't keyword-match plain-English intent), while Shonkor's **nodes**
  do, because they carry a **name** and an **AI summary** that read like intent. So part of the gain is the
  graph's *indexed unit* being keyword-matchable, not pure topology — named, not hidden.
- README §3, the sales deck and `bench/metrics-agent-queries.json` re-pinned to the 2×2. The #156 guard now
  checks all **four** cells (including the ones where Shonkor does *not* win) plus the keyword-fired caveat, so
  the flattering number can't be published without the confound beside it.
- All README numbers re-measured on a freshly re-indexed graph (2.225 nodes) for consistency — exact-name and
  intent retrieval, token reduction (73,8 %), and the 2×2 now all come from one reproducible run.

### Changed — Topology audit + safety net for nested Markdown CONTAINS (#175)
- #112 nested Markdown sections (`File → h1 → h2 → h3`); `outline` had silently broken on it and was fixed,
  but **no test would have caught it**. This audits every other consumer of `CONTAINS` topology and
  file-seeded traversal, and adds the failing-test surface that was missing.
- **Data integrity is safe** — verified statically *and empirically* (re-indexing a doc shrunk from 6 to 4
  sections leaves exactly 4 nodes, no orphans). Cleanup is **`FilePath`-scoped, not `CONTAINS`-walk-scoped**,
  and every section carries its file's path at every nesting depth, so re-index and delete remove all nested
  sections. `PruneOrphanConcepts` is `Concept`/`RELATES_TO`-only; `GraphAnalytics` and every MCP analysis tool
  (`architecture`, `audit`, `hotspots`, `clusters`, `references`, `blast_radius`, …) exclude `CONTAINS`
  entirely — all **unaffected**.
- **One behaviour-change class, not a bug:** a `###` is now 3+ hops from its file, so file-seeded traversals
  at the default 1–2 hops (`generate_capsule`, `/api/capsule`, `/api/rag/query`, dashboard graph expansion,
  the references panel) reach fewer deep sections than under the flat shape. Each still returns a *correct*
  N-hop subgraph and the caller can raise `hops`; #112 measured net-positive on retrieval. Left as-is here and
  filed for a deliberate, measured decision rather than reflexively "fixed" inside an audit.
- **New `MarkdownTopologyContractTests`** pins the load-bearing invariants: cleanup leaves no orphan sections,
  delete removes every depth, a full `CONTAINS` walk reaches every section, and — pinned *deliberately* — a
  fixed 2-hop file seed under-reaches `###`. The next change to the topology, the hop defaults, or the cleanup
  scoping now fails a test **visibly**, instead of degrading in silence the way `outline` did.

### Fixed — Symlink-aware path containment, enforced centrally (#104, #105)
- **The path guard was decorative against symlinks (#104).** `TryResolveContainedPath` compared paths with
  `Path.GetFullPath`, which normalizes **lexically** (collapses `..`) but does **not** follow symlinks. A link
  inside the indexed tree pointing outside it is therefore lexically contained, passes the gate, and is read
  straight through — defeating the control TICKET-209 exists to provide. In multi-tenant / SaaS mode, a tenant
  who can commit a symlink could read the host filesystem via `get_source` / `outline` / `check_edit`.
  Containment now resolves symlinks **component by component** first (the escape hides in a linked *directory*,
  where the leaf file is not itself a link), and hands back the **real** resolved path so a caller opens the
  file the gate approved.
- **Containment is enforced once, in the dispatcher (#105).** It was applied per tool — six copies, and
  nothing stopping the seventh from forgetting. A tool now declares `IMcpTool.PathArguments`; the dispatcher
  resolves + contains each (arrays element by element) and aborts with `path_outside_root` before the tool
  runs. Crucially it **cannot be forgotten**: a test cross-checks every tool's *schema* against its declared
  path arguments, so a tool advertising a `path`/`paths`/`file` it forgot to declare **fails the build**
  rather than silently bypassing the guard. Aliases count.
- Verified **without silently-skipped tests**: Windows blocks unprivileged symlink creation, so the
  directory-escape case is built with a **junction** (needs no elevation, resolves the same) and runs for real
  everywhere — plus a live junction-escape through the real stdio server, rejected `-32602` / `path_outside_root`.

### Changed — Ollama retry is Polly's job now (#116)
- `OllamaEmbeddingService` and `OllamaSemanticAnalyzer` carried **hand-written retry loops** with their own
  exponential backoff and jitter. That is exactly what `Microsoft.Extensions.Http.Resilience` (Polly) exists
  to provide, is maintained, and does better. The loops and `OllamaRetry.Backoff` are gone.
- **What stays ours is the judgement, not the mechanism.** `OllamaRetry` is now purely the *predicate* — what
  counts as transient for **this** backend. No library can know that Ollama answers `200` with an empty
  embedding while a model is still loading. Polly owns when to wait, how long, and how often.
- **Two rules the old classifier enforced are now true by construction**, not by code: an
  `OllamaResponseException` is raised *after* a successful `200`, so the retry pipeline has already returned
  and cannot retry it; and a caller-triggered cancellation aborts the pipeline instead of having to be told
  apart from an HttpClient timeout by inspecting the token.
- **The ticket's proposed design does not work, and this says why.** It asked for the policy to live in the
  typed-client registration. That would (a) have covered only `Shonkor.Web` — `Shonkor.CLI` (**the MCP stdio
  server agents actually use**) and `Shonkor.Bench` construct `new HttpClient()` directly and would have been
  left with **no retry at all**; and (b) is unsatisfiable anyway, because the two operations need *different*
  policies on the *same* client: background work retries transient failures, while the blocking RAG path must
  retry a connection failure but **never a timeout** — retrying a minutes-long generation would double a human's
  wait. So the pipelines live in `OllamaResilience` and each call site selects the one it needs.
- **The enrichment worker's circuit breaker is kept, deliberately.** It is a *polling-cadence* backoff, not an
  HTTP breaker: Polly's breaker would fail each call instantly and the worker would then spin through the
  pending queue **faster** against a dead backend. Complementary, not redundant.
- The `Backoff` unit test is replaced by tests that drive the **real pipelines** against a fake handler —
  including the one that matters most: *the blocking path never retries a timeout*. Verified live against a
  running Ollama (retrieval numbers unchanged) and against a dead one (fails fast, does not hang).

### Changed — The MCP tool contract no longer lies by omission (#117, #118, #119, #120)
Four ways the tool surface could leave an agent **confidently wrong**. Each is a different flavour of the
same fault: the tool knew something the caller could not find out.

- **Clamps announce themselves (#119).** `limit ≤ 100`, `hops ≤ 5`, `maxHops ≤ 10` were applied **silently** —
  a caller asking for `limit=100000` got 100 results and no hint its request had been reduced, so an agent
  could reasonably conclude *"only 100 nodes matched"* when 100 of thousands were returned. Every other cap in
  the codebase announces itself; this one didn't. The note appears **only when the clamp actually bites**
  (never on a default, never on a no-op), so the common path stays noise-free — the fear that kept this
  unshipped was unfounded.
- **`get_subgraph verbose` stays parseable (#117).** Its JSON was capped by **characters**, which is honest
  about truncating but returns a document the caller cannot `JSON.parse` — a dangling brace is not an answer.
  The cap is now **structural**: whole nodes/edges are dropped (tail-first, since `GetSubgraphAsync` is
  breadth-first from the seeds), edges into dropped nodes go with them so the graph stays referentially
  intact, and the payload reports `{ truncated, omitted: { nodes, edges }, reason }`. Valid JSON, explicit
  omission.
- **A negative that is a *failure* is told apart from a negative that is an *answer* (#118).** The line is
  **whether the subject exists**: `get_source` on a symbol that is not in the graph is the agent's mistake — a
  typo or a hallucination — and now fails with `isError` and a recovery hint. But *"nothing references
  `Widget3`"* or *"no path from A to B"* are **real findings** and stay ordinary answers; flagging them
  `isError` would teach the model that a correct negative is a malfunction and invite pointless retries.
- **Failures carry a stable, machine-readable identity (#120).** TICKET-209 made relay errors generic and
  TICKET-210 moved execution failures into `isError` — both right, and both left the surface **prose-only**, so
  a client wanting to branch on *why* a call failed had to string-match English. Codes now ride in
  `error.data.code` (protocol errors) and `result._meta.code` (`isError` results):
  `missing_parameter`, `path_outside_root`, `project_not_found`, `symbol_not_found`, `file_not_indexed`,
  `backend_unavailable`, `tool_failed`. The human message is unchanged and unconstrained — the code is
  additive, not a replacement.

### Fixed — Section budgets are measured in tokens, not characters (#111)
- The parser split sections at **4000 characters** and the embedder truncated bodies at **1500 characters** —
  two different constants standing in for the same thing: the backend's **token** window. The proxy holds for
  English prose (~4 chars/token) and **fails silently** elsewhere. In **CJK** a character is roughly a token,
  so a 4000-char section is ~4000 tokens — double `nomic-embed-text`'s window. It was handed over whole, the
  backend truncated it, and the tail of the section was embedded into **nothing**. No error, no signal; the
  section was simply half-searchable.
- `TokenBudget` is now the single place that answers "does this fit?", and **parser and embedder share it** —
  they can no longer disagree about the same limit. The estimate is script-aware (CJK ≈ 1 token/char,
  alphanumeric runs ≈ 4 chars/token, punctuation ≈ 1 token each, so fenced code and tables come out heavy) and
  deliberately **conservative**: over-counting splits a section early, which is cheap; under-counting hands the
  backend a document it silently truncates, which is invisible.
- **Why an estimate and not a tokenizer:** an exact tokenizer needs the vocabulary of the *actual* embedding
  model, and the model is **user-configurable** (`EmbeddingService:OllamaModel`). Shipping one model's vocab
  would produce a tokenizer that is exact for the **wrong** model the moment a user swaps it — and an
  exact-*looking* wrong answer is worse than an honest approximation, because nothing signals the mismatch.
- English prose is calibrated to land exactly where it did: `doc-sections` FTS Recall@10 stays **1,000**.

### Changed — Markdown sections nest by heading level (#112)
- `CONTAINS` followed a flat `File → section` fan-out, so a `###` was a **sibling** of the `##` it sits under.
  A query matching a child's detail could not reach the parent's framing, and vice versa — for arc42-style
  docs, where the meaningful unit is often the chapter, the answer was split across nodes nothing linked.
- Sections now nest: `File → h1 → h2 → h3`. `outline` renders the real heading tree, and `get_subgraph` on a
  chapter reaches its subsections in one hop.
- **The tension the ticket posed, resolved explicitly:** nesting the *content* would mean a parent storing its
  children's text (duplicated in FTS, double-counted by BM25) **or** a parent's line range no longer matching
  its content (breaking the exactness that makes citations trustworthy). Neither is acceptable, so **only the
  edges nest**. A section's `Content` and `StartLine`–`EndLine` are unchanged. Structure moves; text does not.
  Node ids are index-based and unchanged, so **no scheme bump**.
- `outline` now walks `CONTAINS` breadth-first instead of pulling a fixed 2-hop subgraph — a nested `###` sits
  three hops from the file and was simply **missed**, and raising the hop count on a generic subgraph would
  have dragged in the file's whole reference neighbourhood at every level.
- **Measured:** plain-English intent Recall@10 improves **0,788 → 0,818** (hybrid) and **0,697 → 0,727**
  (vector) — linking child detail to parent framing is what #112 predicted. `doc-sections` Recall@10 holds at
  **1,000**. Exact-name hybrid P@1 slips **0,945 → 0,930**, inside its confidence interval (±0,035); reported
  rather than buried. README, arc42 §1.4 and the sales deck re-pinned to the new run — the #156 guard would
  have failed the build otherwise, which is exactly what it is for.

### Fixed — The benchmark was handicapping Shonkor against itself (#162)
- `--compare-rag` seeded Shonkor's capsule from **vector search alone**, while every shipped path
  (`search_hybrid`, `generate_capsule`, `/api/search/hybrid`, `/api/capsule`) seeds from **`HybridRetrieval`**
  — which has materially better intent recall (0,788 vs 0,697). The head-to-head was therefore measuring a
  configuration the product does not ship, and losing with it.
- With seeding parity, at a matched token budget: **93,9 %** coverage vs the no-graph baseline's **87,9 %** —
  **+6,1 pp**, where the old measurement said **−3,1 pp**.
- The vector-only arm is **still reported**, because it isolates the graph's contribution and because deleting
  the unflattering row once the flattering one appeared is exactly the behaviour these benchmarks exist to
  prevent. A test (`RagHeadToHead_StillPublishesTheArmWeLose`) fails if it is ever removed from the README.
- The diagnosis is **measured, not argued**: seed survival through the capsule budget is **100 %**, and in
  **5 of 33** vector-only misses the target was **never a seed**. The budget was not dropping the answer;
  retrieval never found it.
- **Stated limitation:** the baseline is vector-only while Shonkor's winning row is hybrid. That is the
  conventional "naive RAG" setup, but a fair comparison would give the chunk retriever a keyword arm too.
  Filed as a follow-up; the README and sales deck say so rather than quietly banking the win.

### Changed — The benchmark now scores vectors the way the product does (#163)
- `RagBaselineBenchmark` computed cosine with a **hand-rolled loop accumulating in `double`**, while the
  storage layer scores L2-normalized embeddings with a **SIMD `float` dot product** (TICKET-215/#127). Two
  summation semantics for one similarity — the exact inconsistency #127 removed from the product — meant the
  baseline was being ranked by arithmetic the product does not use.
- Now normalizes once via `VectorMath` and scores with `TensorPrimitives.Dot`. Verified: baseline coverage is
  **unchanged at 87,9 %** (ranking is scale-invariant), so this is a consistency fix, not a result change.

### Changed — The docs allowlist is no longer an honour system (#164)
- `docs/symbol-allowlist.txt` is the escape hatch for the #159 guard, and an escape hatch defended only by a
  comment ("NEVER add a name here to silence a stale doc") is defended by nothing: appending one line was the
  cheapest way to turn a red build green, leaving the doc stale **and** the guard reporting success.
- Three rules are now **enforced by the build**: every entry must sit under a known `[category]`, must carry a
  substantive `# reason`, and — for `[deliberately-removed]` entries — **the docs must still state that the
  type was removed**. Delete that prose and the entry stops being honest, so the build fails.
- Verified by mutation: appending a bare name fails; deleting the "has been removed" sentence about
  `PluginLoader` fails. A guard that cannot fail is not a guard.

### Fixed — The RAG head-to-head measured nothing at all (#157)
- `--compare-rag` resolved a golden case's `Expected` entries with an **exact node-id lookup**, but the
  hand-written sets contain **bare symbol names** (`"TokenHasher"`). The lookup therefore never matched, and
  the coverage metric reported **0 % for both sides** — the whole comparison was vacuous, while the report
  cheerfully printed "Shonkor covers the target +0,0 pp more often".
- One shared `GoldenMatch` now defines "this node satisfies this case" (id substring **or** exact name), and
  both `RetrievalBenchmark` and `RagBaselineBenchmark` use it. The rule was previously written out once,
  correctly, in the retrieval scorer and re-implemented wrongly in the RAG baseline.
- **The fixed metric says Shonkor loses.** At a matched token budget: chunked-RAG covers the target symbol
  **87,9 %** of the time, Shonkor's capsule **84,8 %** — a **3,1 pp deficit**, at marginally more tokens.
  Published in the README anyway, with the honest reading: both sides run the *same* embedding search, so
  "is the target's text in the blob" is a low bar raw chunks clear by brute force; the capsule's value is the
  **edges** (call graph, signatures, blast radius) that chunks cannot express at any budget. Seed survival
  through the budget is **100 %**.

### Fixed — Published numbers were not reproducible from the documented commands (#156)
- The README's numbers (pinned in #152) were measured on a **concept-enriched** graph (3.529 nodes) that only
  the *web enrichment worker* produces. `shonkor index . --embed` — the command the README tells you to run —
  produces **2.071 nodes with no concepts**. The published figures were therefore **not reproducible by the
  documented steps**, which is the same defect as being wrong.
- All figures re-measured on the graph the documented commands actually build, and the README now states that
  graph's size so a reader can confirm they are on the same footing. Hybrid Recall@10 **0,788** (was published
  0,879), keyword **0,182** (0,212), token reduction **75,9 %** (96,2 %).
- **New guard** (`ReadmeBenchmarkNumbersTests`): parses the numbers **out of `README.md`** and asserts they
  equal `bench/metrics-*.json`. Metrics-to-metrics comparison would only catch regressions; the failure mode
  this project keeps hitting is *documentation* drift, so the README itself is the input. It also cross-checks
  that the quoted before/after token counts actually imply the percentage they claim.

### Fixed — Docs named types that do not exist (#159)
- **New guard** (`DocsSymbolIntegrityTests`): every backticked PascalCase name in `docs/**` and `README.md`
  must be declared in `src/` or listed in `docs/symbol-allowlist.txt` with a reason. This is `verify_exists`,
  Shonkor's own anti-hallucination tool, finally aimed at Shonkor's own prose.
- It immediately caught a **fresh** error from #153: arc42 §5.3 listed `FindTools`/`ReadTools`/`AnalyzeTools`
  as types. Those are **file names**; the types are one `IMcpTool` class per tool (`SearchGraphTool`,
  `GetSourceTool`, …). Corrected.
- Stated limitation: it catches **dead symbols**, not wrong semantics. It would *not* have caught the docs
  claiming `Security:EnablePlugins` defaults to OFF when it defaults to ON (#153) — a symbol that exists,
  described incorrectly. That still needs a human.

### Changed — arc42 §1.4 and the sales presentation now carry measured numbers (#160)
- Both published performance figures of unknown provenance. Measured on this repo (2026-07-14): indexing
  **≈ 31 files/s** (was "> 19"), FTS seed latency **0,74 ms median / 15 ms p95** (was a flat "< 5 ms" — true
  at the median, wrong at the tail by 3×), 2-hop traversal **2,4 ms / 10,8 ms p95**, token reduction
  **75,9 %** (was "≈ 41 %").
- The database footprint was published as **352 KB**, "which can easily be placed under version control". It
  measures **20,1 MB** — **57× larger** — and `shonkor.db` is gitignored, so the advice contradicted the repo's
  own configuration.
- The sales presentation quoted retrieval figures (*Recall@10 0,37 → 0,97*) taken from the **circular**
  `doc-intent` golden set — the one whose queries are the target's own doc-comment, which the project
  explicitly disowned. Replaced with the non-circular, machine-checked set.
- New `--search-latency` mode in `Shonkor.Bench` measures FTS and traversal latency (median/p95), so these
  claims are regenerated rather than hand-maintained.

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
