# TICKET-205 – Overall prompt budget + `num_ctx` + truncation detection in the RAG path

**Severity ref:** K4 · **Effort:** S · **Risk:** low × low

## Context
`BuildRagPrompt` (`OllamaSemanticAnalyzer.cs:158-196`) only caps per node (2,000 characters), without an overall budget; the UI sends up to 10 nodes + 6 chat turns. `num_ctx` is never set (Ollama default often 2048–4096 tokens). On overflow, Ollama truncates silently and first discards the instruction block (grounding, abstention, citation obligation, injection fence) at the start of the prompt — the model then generates freely over raw code, precisely in the context-rich case.

## Acceptance Criteria
- [ ] `options.num_ctx` is set per model from config (default e.g. 8192, documented).
- [ ] Prompt assembly estimates tokens (chars/3.5) and reduces the node count or per-node budget until estimate + answer reserve < `num_ctx`.
- [ ] After the call, `prompt_eval_count` from the Ollama response is compared against the estimate; detected truncation is logged and passed to the UI as a warning in the answer metadata.
- [ ] The instruction block (rules + abstention + citation obligation) sits at the **end** of the prompt, after the context.
- [ ] Answer metadata contains `nodesUsed` including a `truncated` flag per node (closes M2 of the grounding review); UI shows "Context: N nodes, M truncated".
- [ ] `/api/ask` caps and deduplicates `NodeIds` (e.g. ≤ 20).

## Affected Areas
`OllamaSemanticAnalyzer.cs` (blocking + streaming), `SearchEndpoints.cs`, `app.js` (metadata display), config/settings tab.

## Dependencies
Before TICKET-206 (citation validation presupposes a stable, non-truncated prompt). Effect measured via TICKET-201.

## Definition of Done
Test with an artificially large context: truncation is detected and reported instead of silently swallowed; groundedness metrics (TICKET-201) documented before/after.
