// Licensed to Shonkor under the MIT License.

using Xunit;

namespace Shonkor.Tests;

/// <summary>
/// Groups the negative tests whose expected failure is logged <b>server-side, asynchronously</b> (inside a
/// <c>WebApplicationFactory</c> host, during the awaited HTTP request) into a single collection that does not
/// run in parallel with anything else (#236).
///
/// <para>
/// Why this exists: <see cref="ExpectedError.Emit"/> writes its marker on the test thread just before the
/// request; the production error is written later, on a thread-pool thread, from inside the endpoint. With the
/// default cross-class parallelism, other collections' output interleaves between the two, so the marker and
/// the stack trace it explains end up hundreds of lines apart in the CI log — defeating the point. Serializing
/// these classes keeps each marker immediately ahead of its error, so the log reads at a glance.
/// </para>
/// <para>
/// Only the async/server-logged cases need this. A synchronous in-process failure (e.g. the MCP tool-error
/// test, which logs on the same thread between the marker and the assertion) is already adjacent and stays
/// fully parallel.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExpectedServerErrorsCollection
{
    public const string Name = "ExpectedServerErrors";
}
