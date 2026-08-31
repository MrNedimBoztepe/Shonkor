// Licensed to Shonkor under the MIT License.

namespace Shonkor.Core.Models;

/// <summary>
/// <b>Why</b> an edge carries the trust tier it does. The tier says how much to believe a relationship;
/// this says what kind of evidence produced it — so a consumer can accept DI resolution and reject
/// reflection instead of accepting or rejecting "heuristic" as one lump (AP1, #428).
///
/// <para>
/// Every value here is derived from a producer that exists in this repository, not from a taxonomy of
/// what an analyser might one day emit. The mandate's examples <c>Reflection</c>, <c>DiResolution</c>,
/// <c>StringRoute</c>, <c>GenericInstantiation</c> and <c>VirtualDispatch</c> are deliberately absent:
/// nothing produces them today, and an enum value no producer can set turns
/// <see cref="Unspecified"/> into the honest answer for a large part of the graph — which is exactly the
/// state this work exists to end.
/// </para>
///
/// <para>
/// <b>The tier is derived from the reason, never maintained beside it</b> (see
/// <c>ProvenanceReasons.TierOf</c>). Two fields that must agree are two fields that will not: the
/// scanner's pessimistic <c>max()</c> and the store's optimistic <c>MIN()</c> disagreed for months
/// before #399 found 1 354 edges caught between them.
/// </para>
/// </summary>
public enum ProvenanceReason
{
    /// <summary>
    /// No reason recorded. The default on purpose, and never a tier claim: an edge written before this
    /// existed, or one whose producer cannot be recovered from <c>(RelationType, Provenance)</c> alone —
    /// pre-#402 <c>IMPLEMENTS</c>/<c>EXTENDS</c> at <c>Extracted</c>, where both producers wrote the same
    /// pair and a migration would have to guess. Defaulting these to a real reason would manufacture
    /// evidence; they stay unspecified until a full scan re-derives them.
    /// </summary>
    Unspecified = 0,

    /// <summary>This node IS inside that one. Deterministic by construction — a file contains a class.</summary>
    Structural = 1,

    /// <summary>A compiler-resolved symbol, not a name that matched. <c>SemanticCsharpLinker</c>, <c>TypeScriptSemanticLinker</c>.</summary>
    SemanticSymbol = 2,

    /// <summary>
    /// Heritage read from syntax: the base-list is a bare type name and the IMPLEMENTS-vs-EXTENDS split is
    /// a naming heuristic (<c>RoslynAstParser</c>). Separating this from <see cref="SemanticSymbol"/> is
    /// what finally makes <c>IMPLEMENTS</c>/<c>EXTENDS</c> repairable — before it, both producers wrote
    /// the identical pair and the repair table had to leave them alone (#402, #405).
    /// </summary>
    SyntacticHeritage = 3,

    /// <summary>Exactly one definition carries that name, so the match is unique but still a name match.</summary>
    UniqueNameMatch = 4,

    /// <summary>Several definitions carry the name; the edge names one of them without being able to choose.</summary>
    AmbiguousNameMatch = 5,

    /// <summary>Derived from where the file sits, not from what it says — Helix layers and modules.</summary>
    PathConvention = 6,

    /// <summary>A module specifier resolved by name (<c>IMPORTS</c>), not to a file the graph contains.</summary>
    ImportSpecifier = 7,

    /// <summary>A link written in prose or serialized content — Markdown references, Unicorn items.</summary>
    DocumentLink = 8,

    /// <summary>
    /// Read from CMS configuration. Deterministic to READ and undecidable to ASSERT: Sitecore configs are
    /// patched, so whether a registration survives into the merged runtime configuration is not decidable
    /// from one file.
    /// </summary>
    CmsConfiguration = 9,

    /// <summary>A field value that looks like an item id, read as a link to that item.</summary>
    FieldValueReference = 10,

    /// <summary>A CLR type name resolved to exactly one candidate.</summary>
    TypeResolutionUnique = 11,

    /// <summary>
    /// A CLR type name with several candidates. Split from <see cref="TypeResolutionUnique"/> rather than
    /// documented as an exception: <c>ClrTypeResolverPostProcessor</c> emits both tiers from one code
    /// path, and an exception in the single rule the whole design rests on is expensive to remember.
    /// </summary>
    TypeResolutionAmbiguous = 12,

    /// <summary>Asserted by a model or an agent about the code, not extracted from it (AP7, #445).</summary>
    ModelAssertion = 13,

    /// <summary>A language-level override relationship read syntactically — <c>PhpModuleParser</c>.</summary>
    LanguageOverride = 14,

    /// <summary>A binding between technologies inferred by convention — <c>CrossTechLinker</c>.</summary>
    CrossTechBinding = 15,
}
