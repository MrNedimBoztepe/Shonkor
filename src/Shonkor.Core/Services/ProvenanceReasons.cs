// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;

namespace Shonkor.Core.Services;

/// <summary>
/// The one place that turns a <see cref="ProvenanceReason"/> into a <see cref="Provenance"/> tier, and
/// the one place that recovers a reason for an edge written before reasons existed (AP1, #428).
///
/// <para>
/// The derivation is total and exceptionless by construction: every reason maps to exactly one tier.
/// That was the point of splitting <see cref="ProvenanceReason.TypeResolutionUnique"/> from
/// <see cref="ProvenanceReason.TypeResolutionAmbiguous"/> — one producer emitting two tiers would
/// otherwise have forced an exception into the single rule everything else rests on.
/// </para>
/// </summary>
public static class ProvenanceReasons
{
    /// <summary>
    /// The tier a reason implies, or <c>null</c> for <see cref="ProvenanceReason.Unspecified"/> — which
    /// implies nothing and must not be allowed to imply <c>Extracted</c>. "No reason recorded" is a third
    /// state, and every time this codebase collapsed a third state into an optimistic one it cost a real
    /// defect: 699 wrongly-Extracted edges, four stale plugins, a cached restore layer read as clean.
    /// </summary>
    public static Provenance? TierOf(ProvenanceReason reason) => reason switch
    {
        ProvenanceReason.Unspecified => null,

        ProvenanceReason.Structural => Provenance.Extracted,
        ProvenanceReason.SemanticSymbol => Provenance.Extracted,

        ProvenanceReason.AmbiguousNameMatch => Provenance.Ambiguous,
        ProvenanceReason.TypeResolutionAmbiguous => Provenance.Ambiguous,

        ProvenanceReason.SyntacticHeritage => Provenance.Inferred,
        ProvenanceReason.UniqueNameMatch => Provenance.Inferred,
        ProvenanceReason.PathConvention => Provenance.Inferred,
        ProvenanceReason.ImportSpecifier => Provenance.Inferred,
        ProvenanceReason.DocumentLink => Provenance.Inferred,
        ProvenanceReason.CmsConfiguration => Provenance.Inferred,
        ProvenanceReason.FieldValueReference => Provenance.Inferred,
        ProvenanceReason.TypeResolutionUnique => Provenance.Inferred,
        ProvenanceReason.ModelAssertion => Provenance.Inferred,
        ProvenanceReason.LanguageOverride => Provenance.Inferred,
        ProvenanceReason.CrossTechBinding => Provenance.Inferred,

        // A value added to the enum without a tier is a compile-time-invisible gap, so it fails loudly
        // here rather than silently resolving to something plausible.
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "No tier is defined for this reason."),
    };

    /// <summary>
    /// The reason an existing edge must have had, recovered from <c>(RelationType, Provenance)</c> — the
    /// same predicate #399's repair used, and it works for the same reason: edges record no producer, but
    /// for most families that pair identifies one unambiguously.
    ///
    /// <para>
    /// Returns <see cref="ProvenanceReason.Unspecified"/> where it does not. The important case is
    /// <c>IMPLEMENTS</c>/<c>EXTENDS</c> at <c>Extracted</c> in a graph indexed before #402: the syntactic
    /// parser and the semantic linker both wrote exactly that, so assigning either reason would be a
    /// guess dressed as a migration. Those edges stay unspecified until a full scan re-derives them.
    /// </para>
    /// </summary>
    public static ProvenanceReason Recover(string relationship, Provenance tier) => (relationship, tier) switch
    {
        ("CONTAINS", _) => ProvenanceReason.Structural,
        ("DEFINED_IN", _) => ProvenanceReason.Structural,

        ("CALLS", Provenance.Extracted) => ProvenanceReason.SemanticSymbol,
        ("INSTANTIATES", Provenance.Extracted) => ProvenanceReason.SemanticSymbol,
        ("OVERRIDES", Provenance.Extracted) => ProvenanceReason.SemanticSymbol,
        ("IMPLEMENTS_MEMBER", Provenance.Extracted) => ProvenanceReason.SemanticSymbol,

        // Post-#402 these are distinguishable by tier; at Extracted in an older graph they are not.
        ("IMPLEMENTS", Provenance.Inferred) => ProvenanceReason.SyntacticHeritage,
        ("EXTENDS", Provenance.Inferred) => ProvenanceReason.SyntacticHeritage,

        ("REFERENCES_TYPE", Provenance.Extracted) => ProvenanceReason.SemanticSymbol,
        ("REFERENCES_TYPE", Provenance.Inferred) => ProvenanceReason.UniqueNameMatch,
        ("REFERENCES_TYPE", Provenance.Ambiguous) => ProvenanceReason.AmbiguousNameMatch,

        ("BELONGS_TO_MODULE", _) => ProvenanceReason.PathConvention,
        ("BELONGS_TO_CONCEPT", _) => ProvenanceReason.PathConvention,
        ("IMPORTS", _) => ProvenanceReason.ImportSpecifier,
        ("REFERENCES", _) => ProvenanceReason.DocumentLink,
        ("OVERRIDES_BLOCK", _) => ProvenanceReason.LanguageOverride,
        ("BINDS_TO", _) => ProvenanceReason.CrossTechBinding,

        ("REGISTERS_PROCESSOR", _) => ProvenanceReason.CmsConfiguration,
        ("REGISTERS_SERVICE", _) => ProvenanceReason.CmsConfiguration,
        ("REGISTERS_CONFIGURATOR", _) => ProvenanceReason.CmsConfiguration,
        ("HANDLES_EVENT", _) => ProvenanceReason.CmsConfiguration,
        ("DEFINES_COMPONENT", _) => ProvenanceReason.CmsConfiguration,
        ("REFERENCES_ITEM", _) => ProvenanceReason.FieldValueReference,

        ("RESOLVES_TO", Provenance.Inferred) => ProvenanceReason.TypeResolutionUnique,
        ("RESOLVES_TO", Provenance.Ambiguous) => ProvenanceReason.TypeResolutionAmbiguous,

        ("RELATES_TO", _) => ProvenanceReason.ModelAssertion,
        ("INFLUENCES", _) => ProvenanceReason.ModelAssertion,
        ("AFFECTS", _) => ProvenanceReason.ModelAssertion,

        _ => ProvenanceReason.Unspecified,
    };

    /// <summary>
    /// The reason for an edge emitted by the semantic C# linker, from the tier it assigned.
    ///
    /// <para>
    /// Separate from <see cref="Recover"/> on purpose. <c>Recover</c> answers "which producer wrote this,
    /// judging only by the pair" — and for <c>IMPLEMENTS</c>/<c>EXTENDS</c> at <c>Extracted</c> it
    /// correctly answers "cannot tell", because in a stored graph both producers wrote that. Inside the
    /// linker there is nothing to tell: it knows it is the linker. Using the migration's heuristic where
    /// direct knowledge exists left 102 edges unattributed on a real graph after a full scan.
    /// </para>
    /// </summary>
    public static ProvenanceReason ForSemanticLink(Provenance tier) => tier switch
    {
        Provenance.Extracted => ProvenanceReason.SemanticSymbol,      // a resolved symbol
        Provenance.Inferred => ProvenanceReason.UniqueNameMatch,      // the name-based fallback, one hit
        Provenance.Ambiguous => ProvenanceReason.AmbiguousNameMatch,  // the fallback, several hits
        _ => ProvenanceReason.Unspecified,
    };
}
