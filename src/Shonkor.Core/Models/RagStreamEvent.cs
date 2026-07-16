// Licensed to Shonkor under the MIT License.

using System.Collections.Generic;

namespace Shonkor.Core.Models;

/// <summary>What a <see cref="RagStreamEvent"/> is — the only three things a streamed answer can say.</summary>
public enum RagStreamEventKind
{
    /// <summary>A piece of the model's own answer. The ONLY kind that carries model-authored text.</summary>
    Token,

    /// <summary>
    /// The answer cited sources that were not in the supplied context. Data, not prose — the consumer decides
    /// how to show it.
    /// </summary>
    UnsupportedCitations,

    /// <summary>The answer ended. <see cref="RagStreamEvent.Complete"/> says whether it ended properly.</summary>
    Done
}

/// <summary>
/// One event in a streamed RAG answer (#231).
///
/// <para>
/// <b>Why this exists.</b> The stream used to be <c>IAsyncEnumerable&lt;string&gt;</c>, and everything that was
/// not a token — "answer incomplete", "unsupported sources" — was appended to it as <b>English prose</b>. That
/// put signal and content in the same channel, with three consequences: a consumer could only string-match on
/// wording the code happened to emit; the markers were English-only in a system that otherwise honours
/// <see cref="RagPromptOptions.Language"/>; and, worst, <b>the model could forge them</b> — ask it what
/// <c>[Answer incomplete …]</c> means and it may emit that exact string, which the UI would then believe.
/// String-matching on content is not a protocol.
/// </para>
/// <para>
/// So control signals leave the text channel entirely. A <see cref="RagStreamEventKind.Token"/> is the only
/// kind that carries model-authored text; everything the model cannot influence travels as a typed event that
/// it has no way to emit. The endpoint renders these as NDJSON frames, and a consumer switches on the kind
/// instead of reading English.
/// </para>
/// </summary>
public sealed record RagStreamEvent
{
    /// <summary>Which of the three things this event is.</summary>
    public required RagStreamEventKind Kind { get; init; }

    /// <summary>The model's text. Set only when <see cref="Kind"/> is <see cref="RagStreamEventKind.Token"/>.</summary>
    public string? Token { get; init; }

    /// <summary>
    /// Cited source names that were NOT in the supplied context. Set only when <see cref="Kind"/> is
    /// <see cref="RagStreamEventKind.UnsupportedCitations"/>.
    /// </summary>
    public IReadOnlyList<string>? UnsupportedCitations { get; init; }

    /// <summary>
    /// On <see cref="RagStreamEventKind.Done"/>: whether the backend actually finished the answer. <c>false</c>
    /// means the stream ended without the terminal marker — the tokens already emitted are real, but the answer
    /// is truncated and must not be presented as whole.
    /// </summary>
    public bool Complete { get; init; }

    public static RagStreamEvent OfToken(string text) => new() { Kind = RagStreamEventKind.Token, Token = text };

    public static RagStreamEvent OfUnsupportedCitations(IReadOnlyList<string> names) =>
        new() { Kind = RagStreamEventKind.UnsupportedCitations, UnsupportedCitations = names };

    public static RagStreamEvent OfDone(bool complete) => new() { Kind = RagStreamEventKind.Done, Complete = complete };
}
