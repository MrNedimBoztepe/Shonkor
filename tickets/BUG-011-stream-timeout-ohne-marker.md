# BUG-011 — Streaming responses are hard-aborted after 2 minutes, without an incompleteness marker

**Severity:** High · **Status:** Confirmed · **Area:** LLM integration / RAG

## Context

`_httpClient.Timeout = TimeSpan.FromMinutes(2)` ([OllamaSemanticAnalyzer.cs:34](../src/Shonkor.Infrastructure/Services/OllamaSemanticAnalyzer.cs)) also applies to reading the response body — even with `ResponseHeadersRead`. A RAG generation > 120 s (large capsule context window + 7B model on CPU) is aborted mid-stream. The abort surfaces as a `TaskCanceledException` from `ReadLineAsync` (line 282); the graceful-truncation marker `[Antwort unvollständig]` (lines 317-322) only fires on `line == null`. In `SearchEndpoints.cs:186-189` partial tokens have already been flushed to the client → the response ends mid-sentence, without a marker, with an aborted response.

## Reproduction

Query with a large capsule against a slow local model (>120 s generation) → response aborts mid-sentence.

## Fix

`Timeout = Timeout.InfiniteTimeSpan` on the typed client; instead set the connect/first-byte timeout via `SocketsHttpHandler.ConnectTimeout` or a CTS up to the first token. Structure the read loop so that the exception path can also yield the truncation marker (try around the read, yield after the catch).

## Acceptance Criteria

- [ ] Generations > 2 min run to completion (no artificial abort from the client timeout).
- [ ] If the stream is interrupted anyway (server gone, real timeout), the output ends with the `[Antwort unvollständig]` marker.
- [ ] Establishing a connection to an unreachable Ollama still fails fast (connect timeout).

## DoD

- Fix + test (simulated stream abort) merged.
