// Licensed to Shonkor under the MIT License.

using System.Collections.Generic;

namespace Shonkor.Web;

/// <summary>Request to synthesize a context capsule for nodes matching <paramref name="Query"/>.</summary>
public record CapsuleRequest(string Query, int? Hops);

/// <summary>Request to (re)index a directory; both fields fall back to the project defaults when null.</summary>
public record IndexRequest(string? Directory, List<string>? ExcludePatterns);

/// <summary>Request to update the status of an interaction node (Task/Decision/Question/Milestone).</summary>
public record UpdateStatusRequest(string Id, string Status);

/// <summary>One prior chat turn sent by the client so the server can fence the transcript as data (TICKET-206).</summary>
public record ChatTurnDto(string Role, string Text);

/// <summary>
/// Request to generate a natural-language RAG answer grounded in the given node ids.
/// TICKET-206 additions (all optional, backward-compatible): <paramref name="Scores"/> parallel to
/// <paramref name="NodeIds"/> (retrieval relevance, drives the abstain-without-LLM threshold and the
/// per-node match strength shown to the model), <paramref name="History"/> (prior turns, fenced as data —
/// <paramref name="Query"/> then carries only the latest question), and <paramref name="Language"/>.
/// </summary>
public record AskRagRequest(
    string Query,
    string[] NodeIds,
    double[]? Scores = null,
    ChatTurnDto[]? History = null,
    string? Language = null);
