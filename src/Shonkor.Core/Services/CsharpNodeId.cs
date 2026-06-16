// Licensed to Shonkor under the MIT License.

namespace Shonkor.Core.Services;

/// <summary>
/// The single source of truth for C# graph node ids, shared by the syntactic <see cref="RoslynAstParser"/>
/// and (future) the semantic linker, so the parser and the linker can never disagree on the id of a symbol.
/// </summary>
/// <remarks>
/// The scheme is name-based and namespace-free: a type is <c>{filePath}::{TypeName}</c>, a member is
/// <c>{filePath}::{TypeName}::{MemberName}</c>. NOTE: this does not encode method signatures, so overloads
/// (e.g. <c>Foo(int)</c> and <c>Foo(string)</c>) collide on one id — a known limitation the semantic-core
/// project must decide on (see docs/projects/semantic-csharp-core.md).
/// </remarks>
public static class CsharpNodeId
{
    /// <summary>Node id for a type declaration in <paramref name="filePath"/>.</summary>
    public static string ForType(string filePath, string typeName) => $"{filePath}::{typeName}";

    /// <summary>Node id for a member of <paramref name="typeName"/> in <paramref name="filePath"/>.</summary>
    public static string ForMember(string filePath, string typeName, string memberName) =>
        $"{filePath}::{typeName}::{memberName}";
}
