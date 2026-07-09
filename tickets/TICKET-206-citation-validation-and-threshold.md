# TICKET-206 – Grounding enforcement: citation validation, relevance threshold, history fence, answer language

**Status:** ✅ Implemented — `CitationValidator`, relevance floor (`MatchStrength`) + history fence in `RagPromptBuilder` (PR #89, commit `8557609`); answer-language default handled in the English-language migration. Tests: `GroundingTests`.

**Severity ref:** H2, H3, H14, M13 (+M1 grounding review) · **Effort:** M · **Risk:** medium × low (too aggressive a threshold rejects legitimate questions → make it configurable, calibrate via eval)

## Context
The entire grounding story is a prompt request today: citations (`[Name @ file:lines]`) are never validated against the delivered label set (`OllamaSemanticAnalyzer.cs:163-168,184` — model text goes out verbatim); there is no score threshold — context is the top 10 of the last search, score discarded (`app.js:255-257`), the LLM answers even to noise; earlier assistant answers land in the trusted `NUTZERFRAGE` slot (`app.js:312-316`) and bypass the "context is data" fence; the index-time injection detector (`SuspiciousContentPostProcessor`) is decoupled from the answer path; the answer language is hardcoded German (`:183,194`).

## Acceptance Criteria
- [ ] Post-processing (at stream end or buffered): `[… @ …]` labels extracted via regex, checked against the exact label set from `BuildRagPrompt`; unknown labels visibly flagged ("⚠ unsubstantiated source"), valid ones rendered as clickable node links; citation-less paragraphs marked (subtly in the UI).
- [ ] Configurable relevance threshold (relative to the top score and absolute) at context selection; if all candidates fall below it, the server responds deterministically "there is no evidence for this in the graph" **without** an LLM call. Score/"match strength" per node into the prompt.
- [ ] Chat transcript in its own prompt section declared as data; `NUTZERFRAGE` contains only the last user message.
- [ ] `/api/ask*` checks context node IDs against `security.suspicious-instruction-in-content` diagnostics; flagged sources are annotated in the prompt and surfaced in the UI as a warning.
- [ ] Answer language follows the UI locale or the language of the question (config override possible).
- [ ] Known error markers are stripped before the push into `aiChatHistory`.

## Affected Areas
`OllamaSemanticAnalyzer.cs`, `SearchEndpoints.cs`, `app.js`, settings.

## Dependencies
After TICKET-205. Threshold calibration and sign-off via TICKET-201 metrics (abstention precision/recall) and the injection suite.

## Definition of Done
Injection suite ≥ 90 % passed; citation validity ≥ 0.98; abstention precision ≥ 0.9 on the answers set; threshold default activated after calibration.
