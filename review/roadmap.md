# Shonkor – Prioritized Roadmap

Four phases, each with dependencies and a rollback strategy. Principle: **Phase 0 measures before Phases 1–3 change anything** — every fix gets a before/after. Tickets in `tickets/`, details in [improvements.md](improvements.md).

```
Phase 0 (Measure)  ──►  Phase 1 (Quick Wins)  ──►  Phase 2 (Structure)  ──►  Phase 3 (Scaling/Optional)
TICKET-201, 202         203, 204, 205, 206,        211, 212, 213           215, SDK question, Reranking question
                        207, 208, 209, 210, 214
```

---

## Phase 0 — Eval foundation (first; ~1 week)

| Ticket | Content | Why first |
|---|---|---|
| TICKET-201 | Restore the `--answers` groundedness eval | Without it every grounding fix is unprovable |
| TICKET-202 | De-circularize the golden set, benchmark hybrid, coverage symmetry, gate on P@1/MRR, CI wiring | Baseline for all Phase-1/2 deltas |

**Dependencies:** none. **Rollback:** trivial — pure measurement infrastructure, touches no production path. The only irreversible effect: more honest numbers in the README (intended).

## Phase 1 — Quick wins (parallelizable; ~2–3 weeks)

All points are small, local, and independent of one another; order within the phase by precision leverage:

| Ticket | Content | Effort |
|---|---|---|
| TICKET-203 | `ON CONFLICT DO UPDATE` upsert + FTS query sanitizer (K3/H1) | S |
| TICKET-205 | Prompt budget + `num_ctx` + truncation detection (K4) | S |
| TICKET-204 | Full method bodies + `signature` (K2/M9) | S |
| TICKET-207 | Provenance integrity (K5/M5) | S |
| TICKET-208 | Line-number normalization (H7) | S |
| TICKET-206 | Citation validation + relevance threshold + history fence (H2/H3/H14) | M |
| TICKET-214 | `generate_capsule` on the budget synthesizer (H10) | S |
| TICKET-209 | MCP security: containment, set_project, bypass, record (H8/H9/M11/M12) | M |
| TICKET-210 | MCP protocol: isError, ping, clamps, retry hygiene (H13/M7/M10) | M |

**Dependencies:** 204, 207, 208 subsequently require a **full re-index** of the existing databases (one-time; bundle 208 ideally with a `SchemeVersion` bump, then the existing mechanism forces the reparse automatically). 206 should land after 205 (validation presupposes a stable prompt).
**Rollback:** Each ticket is its own PR with its own feature behavior; 203 is the only central semantics change — rollback = revert of the upsert statement (the FTS rebuild at the next start heals the remnants). Roll out the 206 threshold as config with default "off", calibrate via eval, then default "on".

**Gate at end of phase:** Run the Phase-0 suite again; expected are measurable improvements in must-cite recall (205/206), NL retrieval (204), and citation accuracy (208). A regression anywhere → roll back the ticket in question.

## Phase 2 — Structural rework (sequential; ~4–6 weeks)

| Ticket | Content | Dependency |
|---|---|---|
| TICKET-211 | Markdown section chunking + Summary-in-FTS + concept embeddings (H6/M8) | Eval Phase 0 (doc cases into the golden set) |
| TICKET-212 | Embedding lifecycle: per-node hash carry-over, CLI filter, stdio re-embed, race guard, queue attempts, single-transaction replace, plugin file-node prohibition (H11/M1–M3) | 203 (upsert semantics stable first) |
| TICKET-213 | Edge canonicalization: implementations_of, phantom hubs, JS import resolution, id scheme (arity/ordinal/partials), traversal filter (H4/H5/M4/M6/M15) | 208 (bundles the second SchemeVersion bump — **both id-relevant changes in one bump**, otherwise two full reparses for the user) |

**Rollback:** 212 behind a feature flag (`Indexing:PerNodeHashCarryover`) — on misbehavior, flag off = today's wipe behavior (correct, just expensive). 213 is by definition not hot-rollbackable (id scheme); the safeguard instead: a graph-diff test before merge (full index before/after on Shonkor itself; explicitly list the expected edge deltas) + the Phase-0 suite. 211 purely additive, rollback = revert + FTS rebuild.

**Gate:** Seed survival, `implementations_of` recall (new golden cases!), edit-loop scenario test (edit → reindex_file → search_semantic finds the new code).

## Phase 3 — Scaling & deliberate decisions (as needed)

| Topic | Trigger | Content |
|---|---|---|
| TICKET-215 | Graphs > ~20k nodes or latency complaints | Vector: normalization + MemoryMarshal immediately, matrix cache, possibly sqlite-vec; CTE on UNION branches |
| MCP C# SDK / Streamable HTTP | Remote MCP becomes strategic (multiple external clients) | Replaces the hand-rolled handler + solves the session problem structurally; otherwise not needed |
| Cross-encoder reranking | Only if the Phase-0 eval still shows precision gaps in the top-K after Phases 1/2 | Introduce measurably, not on suspicion |
| nomic prefix switch | Result of the corrected A/B (part of 202) | If a gain: prefix pair into the model stamp, controlled re-embed |

**Rollback:** everything here is opt-in or behind config; the vector cache degrades to today's brute-force path on bugs.

---

## Deliberately deferred / rejected

- **Graph-store switch (Neo4j or similar):** no measured need; the SQLite CTE is correct and offline. Rejected.
- **External vector store:** breaks the offline promise; discuss only well beyond 1M nodes. Rejected.
- **LLM-judge faithfulness as a CI gate:** too flaky on local models; only as a reporting line (eval-plan §1C). Deferred.
- **Consolidation of the MCP tool palette** (31 tools, overlaps `locate`/`search_graph`, `edit_plan`/`rename_plan`): nice-to-have, but the descriptions differentiate well; only tackle once telemetry shows agents mis-picking. Deferred.
