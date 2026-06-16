// Licensed to Shonkor under the MIT License.

namespace Shonkor.Core.Services;

/// <summary>
/// The single source of truth for C# graph node ids, shared by the syntactic <see cref="RoslynAstParser"/>
/// and (future) the semantic linker, so the parser and the linker can never disagree on the id of a symbol.
/// </summary>
/// <remarks>
/// The scheme is name-based and namespace-free: a type is <c>{filePath}::{TypeName}</c>, a member is
/// <c>{filePath}::{TypeName}::{MemberName}</c>. Methods and constructors append <c>#{arity}</c> (the
/// parameter count) — the only discriminator the syntactic parser and the semantic linker can produce
/// identically — so different-arity overloads get distinct ids. Same-arity overloads (e.g.
/// <c>Foo(int)</c> vs <c>Foo(string)</c>) still collide; see docs/projects/method-node-id-overloads.md.
/// </remarks>
public static class CsharpNodeId
{
    /// <summary>Node id for a type declaration in <paramref name="filePath"/>.</summary>
    public static string ForType(string filePath, string typeName) => $"{filePath}::{typeName}";

    /// <summary>Node id for a (non-overloadable) member — e.g. a property — of a type.</summary>
    public static string ForMember(string filePath, string typeName, string memberName) =>
        $"{filePath}::{typeName}::{memberName}";

    /// <summary>
    /// Node id for a method or constructor, discriminated by parameter count so different-arity overloads
    /// don't collide: <c>{filePath}::{TypeName}::{MethodName}#{arity}</c>. Method names never contain
    /// <c>#</c>, so the suffix is unambiguous.
    /// </summary>
    public static string ForMethod(string filePath, string typeName, string methodName, int arity) =>
        $"{filePath}::{typeName}::{methodName}#{arity}";
}
