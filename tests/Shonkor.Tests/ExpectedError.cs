// Licensed to Shonkor under the MIT License.

namespace Shonkor.Tests;

/// <summary>
/// Marks a deliberate, asserted failure in the CI build log so a reader — or a grep gate — can tell it
/// apart from a real one (#236).
///
/// <para>
/// A negative test calls <see cref="Emit"/> immediately before the action that provokes an expected
/// server-side error. The marker is written to the <b>same stream</b> (<c>stderr</c>) the production error
/// paths log to, so in the build output the one tidy marker line sits directly ahead of the stack trace it
/// explains. The rule the marker buys us: <i>anything loud in a green run that is NOT preceded by
/// <see cref="Marker"/> is a real signal.</i>
/// </para>
/// <para>
/// This deliberately does not silence the failure. The point of #236 is not to hide the noise but to label
/// it, so the CI log stays honest — the error still prints, it just arrives wearing a tag that says it was
/// asked for. If we ever want to gate on it, grep the build log for stack traces not preceded by the marker.
/// </para>
/// </summary>
internal static class ExpectedError
{
    /// <summary>The greppable token that prefixes every expected-error line.</summary>
    public const string Marker = "[EXPECTED ERROR]";

    /// <summary>
    /// Emits a single marker line ahead of an expected failure.
    /// </summary>
    /// <param name="what">
    /// A short reason, e.g. <c>"get_stats over an unopenable DB — asserted below as isError, not thrown"</c>.
    /// </param>
    public static void Emit(string what) => Console.Error.WriteLine($"{Marker} {what}");
}
