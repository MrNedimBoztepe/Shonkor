// Licensed to Shonkor under the MIT License.

using System.Collections.Generic;

namespace Shonkor.Web;

/// <summary>Request to synthesize a context capsule for nodes matching <paramref name="Query"/>.</summary>
public record CapsuleRequest(string Query, int? Hops);

/// <summary>Request to (re)index a directory; both fields fall back to the project defaults when null.</summary>
public record IndexRequest(string? Directory, List<string>? ExcludePatterns);

/// <summary>Request to update the status of an interaction node (Task/Decision/Question/Milestone).</summary>
public record UpdateStatusRequest(string Id, string Status);

/// <summary>Request to generate a natural-language RAG answer grounded in the given node ids.</summary>
public record AskRagRequest(string Query, string[] NodeIds);
