# Shonkor – Improvement Proposals

Format per proposal: **Problem → Solution → optional Alternative → Benefit → Risk (Probability × Impact) → Effort (S/M/L)**. Numbering V1…V15, sorted by leverage on answer precision. References: findings in [shonkor-review.md](shonkor-review.md), tickets in `tickets/`.

---

## V1 — Restore the answer-groundedness eval (K1) → TICKET-201

**Problem:** Nothing measures whether answers are faithful to the context; the earlier eval (citation validity, abstention) was removed in `009b8d7`.
**Solution:** Reactivate the `--answers` mode in `Shonkor.Bench`: golden set of (question, context node ids, expected behavior: answerable/abstain, must-cite ids); metrics: citation-validity rate (labels ⊆ delivered set), must-cite recall, abstention precision/recall. Details: [eval-plan.md](eval-plan.md).
**Alternative:** External framework (RAGAS/DeepEval via Python sidecar) — more metrics (faithfulness via LLM judge) out of the box, but a foreign stack, fiddly Ollama binding, breaks the "one harness, one report" line of `Shonkor.Bench`. **Recommendation:** own lean `--answers` first (the infrastructure already existed); LLM-as-judge faithfulness optional on top later.
**Benefit:** Turns "precise" from an assertion into a measurable quantity; a prerequisite for all grounding changes (V4–V6) — without the eval their effects are unprovable.
**Risk:** low × low (pure measurement infrastructure; the only risk: poorly curated cases measure the wrong thing — catchable by reviewing the cases).
**Effort:** M

## V2 — De-circularize the retrieval benchmark + benchmark `search_hybrid` + measure coverage symmetrically (K1) → TICKET-202

**Problem:** doc-intent queries are near-substrings of the embedding documents; hybrid (the default!) unmeasured; +21 pp from asymmetric coverage; gate on a meaningless P@k.
**Solution:** (a) paraphrase queries via LLM (break lexical overlap), plus ~30 hand-collected real agent queries from MCP logs, including negative cases ("is there payment code here?" → expected answer: no); (b) `search_hybrid` as a third retriever row; (c) `RagBaselineBenchmark`: check coverage against the **capsule text** (node headers are citable — string check), not against the pre-budget subgraph; (d) gate on P@1/MRR/Recall@10 relative instead of P@k absolute.
**Benefit:** Only afterwards are the README numbers dependable; hybrid regressions (e.g. from the H1 fix) become visible.
**Risk:** low × medium (paraphrased numbers will come out visibly worse than 0.88 — that is honesty, not damage; the README must follow suit).
**Effort:** M

## V3 — Switch the upsert to `ON CONFLICT DO UPDATE` + FTS query sanitizing (K3, H1) → TICKET-203

**Problem:** REPLACE corrupts the FTS index silently; normal code queries throw FTS syntax errors and degrade to unordered LIKE.
**Solution:** (a) `INSERT … ON CONFLICT(Id) DO UPDATE` (fires the UPDATE trigger, preserves rowid; additionally reset `NeedsSemanticAnalysis`/`Embedding` only on content change); (b) query sanitizer: whitespace tokens into double quotes (with `""` escaping), optional prefix `*`; LIKE only as a genuine last fallback with `ESCAPE` and a defined ordering; (c) signal the fallback case to `search_hybrid` so RRF downweights the pseudo-ranks.
**Alternative to (a):** only `PRAGMA recursive_triggers=ON` — one line, fixes the corruption, but leaves rowid churn and the embedding-wipe behavior in place. **Recommendation:** ON CONFLICT (strictly better); the pragma additionally as belt-and-suspenders does no harm.
**Benefit:** Eliminates the largest silent correctness time bomb; BM25 ranking applies again for the most common query forms.
**Risk:** low × medium (the upsert semantics change is central — cover with existing storage tests + one new FTS consistency test).
**Effort:** S

## V4 — Prompt token budget + `num_ctx` + instruction position (K4) → TICKET-205

**Problem:** Silent Ollama truncation discards the grounding rules first.
**Solution:** Configure `options.num_ctx` per model; estimate prompt tokens (chars/3.5 is enough) and shrink node count/per-node budget accordingly; after the call check `prompt_eval_count` against the estimate and report truncation as a warning to UI/log; move the instruction block to the **end** of the prompt (tail retention).
**Benefit:** Grounding rules are guaranteed to survive; silent degradation becomes visible.
**Risk:** low × low.
**Effort:** S

## V5 — Citation validation + relevance threshold + history fence (H2, H3, H14, M13) → TICKET-206

**Problem:** Citations are unvalidated model text; weak retrieval still leads to an answer; chat history bypasses the injection fence; answer language hardcoded.
**Solution:** (a) buffer the answer or post-process at stream end: extract `[… @ …]` labels via regex, check against the exact label set from `BuildRagPrompt`, flag unknown ones visibly, render valid ones as node links, mark citation-less paragraphs; (b) score threshold before context selection (relative or absolute, configurable), below it a deterministic "there is no evidence for this in the graph" **without** an LLM call; retrieval strength per node into the prompt; (c) transcript into its own prompt section declared as data, `NUTZERFRAGE` = only the last message; (d) answer language from UI locale or the question.
**Alternative to (a):** claim-level entailment (NLI/LLM judge per sentence) — a substantially stronger guarantee, but expensive, slow, unreliable on local models. **Recommendation:** label-set validation now (catches the worst class: citation-laundered hallucination); entailment later as an eval metric (V1), not as a runtime gate.
**Benefit:** The three biggest enforcement gaps of the answer path closed; H14 closes a real injection channel.
**Risk:** medium × low (threshold too aggressive → legitimate questions rejected; make it configurable + calibrate via the V1 eval).
**Effort:** M

## V6 — Enforce provenance integrity (K5, M5) → TICKET-207

**Problem:** Linker fallback and LLM edges claim Extracted; regex parsers without an override; `INSERT OR IGNORE` freezes provenance; `Properties` is never persisted.
**Solution:** Provenance into the linker's edge tuple (fallback: `Inferred` when unambiguous, `Ambiguous` when multiple — as `CrossTechLinker` demonstrates); `RELATES_TO` insert explicitly `Inferred`; `DefaultProvenance` override in `GraphQLParser`/`SitecoreXmCloudPlugin`; edge upsert `ON CONFLICT … DO UPDATE SET Provenance = MIN(excluded.Provenance, Provenance)` (exact resolution may upgrade trust); storage guard test: no write path persists Extracted except deterministic parsers/linkers. Either persist `GraphEdge.Properties` as a JSON column or remove it from the model (end the lie) — recommendation: persist, the plugin metadata (confidence, placeholder) is useful for ranking.
**Benefit:** The trust model — Shonkor's stated unique selling point — matches reality again; `provenance=extracted` filters deliver real compiler facts.
**Risk:** low × low (existing graphs keep incorrect stamps until re-scan — document a one-time `--reindex` after the upgrade).
**Effort:** S

## V7 — Full method bodies + `signature` property (K2, M9) → TICKET-204

**Problem:** The 500-character truncation amputates the core corpus; `signature` is read but never written; classes without content.
**Solution:** Store full bodies on Method/Constructor (bounding belongs in `EmbeddingTextBuilder`, not in the parser); set `EndLine`; fall `get_source` back to the file slice when content ends with a marker; parsers emit `signature` (modifiers + name + parameter list); class nodes get a member-signature skeleton as content.
**Alternative:** only raise the cap (e.g. to 10k) — a smaller DB footprint, but the same problem one order of magnitude later and `get_source` stays incomplete. **Recommendation:** store in full; SQLite handles source-code volumes with ease (the file node already stores up to 100k today).
**Benefit:** FTS/embeddings/get_source see whole methods for the first time; the head+tail design of TICKET-105 becomes effective for C# in the first place.
**Risk:** low × low (DB grows; re-index required).
**Effort:** S

## V8 — Normalize line numbers (H7) → TICKET-208

**Problem:** C# 0-based, plugins 1-based, docs say 1-based — citations off-by-one, slices wrong.
**Solution:** Parser-side `+1` (Roslyn), switch `TryReadSourceSlice` and `CSharpDiagnostics` consistently, a convention test across all parsers (each parser test case checks: line 1 = first line).
**Benefit:** Every citation the system produces is correct — a fundamental prerequisite for "grounded with source reference".
**Risk:** medium × low (off-by-one fixes readily create new off-by-ones — the convention test is a mandatory part).
**Effort:** S

## V9 — Markdown section chunking + Summary in FTS (H6, M8) → TICKET-211

**Problem:** Docs are only file-granular retrievable; sections without content/lines; `Summary` not in FTS; concepts never embedded.
**Solution:** Content between header matches into the section (with Start/EndLine from match offsets), leave code fences/tables intact, split oversized ones at paragraph boundaries; `Summary` as an FTS column (rebuild + trigger); embed concept names.
**Benefit:** Documentation questions ("how do I configure X?") hit the right section with a citable line range instead of the file head.
**Risk:** low × low (FTS rebuild one-time).
**Effort:** M

## V10 — Fix the embedding lifecycle in the edit loop (H11, M1–M3) → TICKET-212

**Problem:** A file edit discards all node embeddings of the file; stdio MCP never re-embeds; CLI re-embeds everything; enrichment race and jam; missing transaction atomicity; plugin file-node conflicts.
**Solution:** per-node content hash: summary/embedding of unchanged siblings survive the re-parse; the CLI embed pass filters `Embedding IS NULL` (or hash change); after `reindex_file` embed the fresh nodes synchronously (the MCP host already has the embedding service); `UpdateNodeSemanticDataAsync` with a content-fingerprint guard; an `AnalysisAttempts` column + parking after N failed attempts; `ReplaceFileGraphAsync(nodes, edges)` in **one** transaction (hash last); parsers must not emit `Type="File"` nodes (scanner filters, Docker/Python plugins fix).
**Benefit:** The agentic edit loop — Shonkor's main scenario — keeps the semantic index up to date instead of dismantling it; CLI embed costs drop by orders of magnitude.
**Risk:** medium × medium (the carry-over logic is the most invasive change on this list; needs targeted tests: edit a method → only its embedding invalidated).
**Effort:** L

## V11 — Edge canonicalization: implementations_of, phantom hubs, JS imports, id scheme (H4, H5, M4, M15) → TICKET-213

**Problem:** Two IMPLEMENTS representations, relink destroys the one used by the tool; phantom name hubs pollute traversal; JS import edges never connect; id collisions/churn.
**Solution:** Id-target as the canonical form, `implementations_of` first resolves the interface node; reduce parser base-type edges to the simple name and tag them `Inferred` (suppress entirely in semantic mode); the CTE does not expand over non-existent endpoints (JOIN in the recursive step) and gets an optional relationship/provenance filter + fan-out cap; JS import resolution with extension/index probing, namespace package imports (`npm:react`); type ids with generics arity, overload ordinal instead of span, partial canonicalization; XmCloud components file-keyed.
**Alternative (partial aspect hubs):** only a blocklist of known hub relations in traversal — cheaper, but cures symptoms; phantom endpoints would remain. **Recommendation:** canonicalization; the blocklist (M6) additionally as a configurable filter.
**Benefit:** Multi-hop precision — the argument for graph over vector — becomes real: `find_path`/`get_subgraph` deliver structural instead of random neighborhoods; the JS/TS side of the cross-tech promise works for the first time.
**Risk:** medium × medium (id-scheme change = `SchemeVersion` bump + full reparse; the mechanism exists for exactly this).
**Effort:** L

## V12 — MCP hardening: session, containment, protocol, clamps (H8, H9, H13, M7, M11, M12) → TICKET-209/210

**Problem:** `set_project` false-success over HTTP; path traversal; tool errors as protocol errors; ping missing; unbounded outputs; AllowLocalBypass; record injection channel.
**Solution:** (a) session store per `Mcp-Session-Id` **or** an honest error "not supported over the relay — use X-Project-Name"; (b) a shared `ResolveContainedPath(raw, basePath)` with `GetFullPath`+StartsWith check in all five file tools; (c) `isError:true` results, `ping`, `-32700`, validate the protocol version; (d) clamps: limit≤100, hops≤5, maxHops≤10, default output cap 20–40 KB for get_source/get_subgraph-verbose/generate_capsule; (e) bypass only with a flag **and** `IsDevelopment()`; (f) record: length cap, check `connectedNodeIds` against existence, fence content as data.
**Alternative to (a):** switch to the official MCP C# SDK with streamable HTTP — solves session + protocol conformance structurally, but migration effort L and the hand-rolled server is otherwise solid. **Recommendation:** (a)–(f) pointwise now; the SDK switch as a deliberate later decision if remote MCP becomes strategic.
**Benefit:** Eliminates confident-lie semantics, the biggest local security vector, and the client compatibility problems in one go.
**Risk:** low × medium (containment can break legitimate out-of-root workflows — the error message names the root; opt-out config if needed).
**Effort:** M

## V13 — Resilience/performance polish (M10, M14, low-severity collection) → in the course of TICKET-210/215

**Problem:** Retry on cancellation/4xx, 3× full-generation retry, webhook CT capture, GetAllNodes on request paths, architecture N+1.
**Solution:** `when (ex is not OperationCanceledException …)` filter; retry only transient errors (or `Microsoft.Extensions.Http.Resilience` on the typed clients); blocking RAG max. 1 retry only on connect errors + a small answer cache `hash(model+prompt)`; webhook task with an `ApplicationStopping` token or a queue BackgroundService; Insights/Stats on projection queries; `architecture` with a batch edge query.
**Benefit:** Latency/cost down, push indexing reliable, dashboard scales.
**Risk:** low × low. **Effort:** M (distributed, independently parallelizable)

## V14 — Vector scaling (H12, M6-SQL) → TICKET-215

**Problem:** Brute-force blob scan per query; CTE OR-join without index usage.
**Solution (incremental):** (1) L2 normalization on write + dot product; `MemoryMarshal.Cast` instead of per-row alloc; drop the overscan factor (the heap is exact); (2) in-memory matrix cache with a generation counter (~300 MB at 100k×768 float32, fp16 halves it); (3) CTE as two UNION branches (uses `idx_edges_source`/`_target`).
**Alternative:** `sqlite-vec` (stays in the SQLite line, ANN optional) or an external vector store (Qdrant) — the latter breaks "100% offline & self-contained" and only pays off well beyond 1M nodes. **Recommendation:** stages 1+3 immediately (small), stage 2 above 20k nodes, `sqlite-vec` as an observation post — the status quo (exact search, SQLite-only) is right at the current target size.
**Benefit:** Query latency stays in two-digit ms as graphs grow; memory churn gone.
**Risk:** low × low (stage 1/3), medium × low (cache invalidation, stage 2). **Effort:** S (1+3) / M (2)

## V15 — A/B-test the nomic prefixes correctly (M1) → part of TICKET-202

**Problem:** Prefixes default off, justified by a measurement that embedded queries with the document prefix; a prefix change triggers no re-embed.
**Solution:** Fix the benchmark on `EmbeddingKind.Query`, re-measure on the paraphrased NL set (V2); include the effective prefix pair in the `EmbeddingModel` stamp (`nomic-embed-text|dp=…|qp=…`) so the existing reconcile logic detects a prefix change.
**Benefit:** nomic is explicitly trained with task prefixes — plausibly several points of NL retrieval precision, practically for free.
**Risk:** low × low (if the measurement confirms "off": keep the status quo, but this time backed by evidence). **Effort:** S

---

### Deliberately NOT recommended

- **Switch to pure hybrid vector+BM25 search without a graph:** The bench data (despite the K1 weaknesses) and the architecture suggest the graph is *not* decorative — capsule structure, blast radius, `implementations_of`, freshness are not reproducible without the graph. The right step is V11 (fix graph precision), not a rollback.
- **Cross-encoder reranking now:** At top-K≤10 from FTS+vector+RRF over code symbols the expected gain is small relative to V2/V5/V7, and a local reranker model adds latency + operational complexity. Decide only after a clean eval (V1/V2) — then you measure the effect instead of guessing it.
- **External graph store (Neo4j etc.):** SQLite + recursive CTE is correctly implemented, deterministic, offline, and performant for the target size (after V14). Migration = high risk, no measured benefit.
