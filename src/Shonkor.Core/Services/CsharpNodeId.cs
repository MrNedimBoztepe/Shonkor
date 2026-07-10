// Licensed to Shonkor under the MIT License.

namespace Shonkor.Core.Services;

/// <summary>
/// The single source of truth for C# graph node ids, shared by the syntactic <see cref="RoslynAstParser"/>
/// and (future) the semantic linker, so the parser and the linker can never disagree on the id of a symbol.
/// </summary>
/// <remarks>
/// The scheme is name-based and namespace-free: a type is <c>{filePath}::{TypeName}</c>, a member is
/// <c>{filePath}::{TypeName}::{MemberName}</c>. Methods and constructors append <c>#{arity}</c> (the
/// parameter count) so different-arity overloads get distinct ids; when a same-arity overload sibling
/// exists they additionally append <c>@{declarationSpanStart}</c> — the source offset of the declaration,
/// which the syntactic parser and the semantic linker compute identically from the same source text — so
/// same-arity overloads (e.g. <c>Foo(int)</c> vs <c>Foo(string)</c>) also get distinct ids. The only
/// residual ambiguity is same-name/same-arity overloads of a <b>partial</b> type split across files (the
/// parser sees one part, the symbol sees all). See docs/projects/method-node-id-overloads.md.
/// </remarks>
public static class CsharpNodeId
{
    /// <summary>
    /// Version of the node-id scheme. Persisted with each graph (SQLite <c>PRAGMA user_version</c>) so a
    /// graph built under an older scheme can be detected and a full re-index recommended/forced. Because a
    /// scheme change (e.g. adding <c>#{arity}</c>) does NOT change file content, the incremental hash check
    /// would otherwise skip every file and leave old-scheme ids in place. <b>Bump this whenever the id
    /// format changes.</b> History: v1 = name-only members (overloads collided); v2 = arity-discriminated
    /// methods/constructors; v3 = same-arity overloads further disambiguated by declaration span;
    /// v4 = types identified by their full chain (namespace + nesting + generic arity) instead of the bare
    /// simple name, so same-named types in one file no longer merge into one node;
    /// v5 = JS/GraphQL node ids no longer lowercased (they matched nothing on Windows paths and collided
    /// with the File node on all-lowercase paths) — JSComponent moves to <c>{file}::{name}</c>, GraphQL
    /// operations keep their original-case names;
    /// v6 = MarkdownSection nodes carry their body and 1-based line range; a header inside a fenced code
    /// block no longer opens a section (which shifts the section index, hence the id), and an oversized
    /// section splits into <c>::part::N</c> sub-nodes. A full reparse is required to populate section bodies.
    /// Unstamped legacy graphs read as 0 (&lt; current → stale).
    /// </summary>
    public const int SchemeVersion = 6;

    /// <summary>
    /// Node id for a type declaration in <paramref name="filePath"/>. <paramref name="typeChain"/> is the
    /// type's full chain within the file — <c>{Namespace}.{Outer}+{Nested}</c> with a CLR-style backtick
    /// arity suffix on generic types (e.g. <c>My.App.Outer+Builder`1</c>) — so distinct same-named types
    /// (different namespace, different arity, or nested under different parents) get distinct ids.
    /// Both the syntactic parser and the semantic linker derive the chain from the same declaration
    /// structure, so the ids match.
    /// </summary>
    public static string ForType(string filePath, string typeChain) => $"{filePath}::{typeChain}";

    /// <summary>Node id for a (non-overloadable) member — e.g. a property — of a type.</summary>
    public static string ForMember(string filePath, string typeName, string memberName) =>
        $"{filePath}::{typeName}::{memberName}";

    /// <summary>
    /// Node id for a method or constructor, discriminated by parameter count so different-arity overloads
    /// don't collide: <c>{filePath}::{TypeName}::{MethodName}#{arity}</c>. When a same-arity overload
    /// sibling exists, pass <paramref name="overloadSpanStart"/> (the declaration's source offset) to
    /// further disambiguate: <c>…#{arity}@{spanStart}</c>. Both the syntactic parser and the semantic
    /// linker derive the same span from the same source text, so the ids match. Method names never contain
    /// <c>#</c> or <c>@</c>, so the suffixes are unambiguous.
    /// </summary>
    public static string ForMethod(string filePath, string typeName, string methodName, int arity, int? overloadSpanStart = null) =>
        overloadSpanStart is int span
            ? $"{filePath}::{typeName}::{methodName}#{arity}@{span}"
            : $"{filePath}::{typeName}::{methodName}#{arity}";
}
