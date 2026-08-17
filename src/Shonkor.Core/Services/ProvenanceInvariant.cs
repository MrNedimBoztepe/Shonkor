// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Models;

namespace Shonkor.Core.Services;

/// <summary>
/// Which trust tiers a relationship is allowed to carry, and which producer puts it there.
///
/// <para>
/// Edges record no producer — the <c>Properties</c> column is <c>NULL</c> on every edge of every graph
/// measured — so the producing code path has to be recovered from the pair
/// <c>(RelationType, Provenance)</c>. For all but two families that pair is unambiguous, and this table is
/// that mapping written down as data instead of prose.
/// </para>
///
/// <para>
/// It exists because the guard that was supposed to catch over-claiming ran against a five-file synthetic
/// fixture, and 699 violations sat in a real graph unnoticed. A check that never meets a real producer
/// cannot see a real producer's mistake.
/// </para>
/// </summary>
public static class ProvenanceInvariant
{
    /// <summary>
    /// A relationship, the tiers it may legitimately carry, and — for the families a repair can correct —
    /// the tier an illegitimate edge must be moved to.
    /// </summary>
    /// <param name="Relationship">The <c>RelationType</c> as stored.</param>
    /// <param name="Legitimate">Every tier this relationship may hold. An edge outside it is a violation.</param>
    /// <param name="RepairTo">
    /// Where a violating edge belongs. <c>null</c> means "not repairable from the pair alone" — the two
    /// producer-ambiguous families, which cannot be corrected until the producers are distinguishable.
    /// </param>
    /// <param name="Producer">The code path that emits it, for the failure message.</param>
    public sealed record Rule(
        string Relationship,
        IReadOnlySet<Provenance> Legitimate,
        Provenance? RepairTo,
        string Producer);

    /// <summary>A relationship at a tier it may not hold, with how many edges and one example.</summary>
    public sealed record Violation(
        string Relationship,
        Provenance Actual,
        Provenance? RepairTo,
        string Producer,
        int Count,
        string SampleSourceId,
        string SampleTargetId);

    /// <summary>A relationship this table says nothing about — information, not a verdict.</summary>
    public sealed record Unclassified(string Relationship, Provenance Tier, int Count, string SampleSourceId);

    private static IReadOnlySet<Provenance> Tiers(params Provenance[] tiers) => new HashSet<Provenance>(tiers);

    private static readonly Provenance E = Provenance.Extracted;
    private static readonly Provenance I = Provenance.Inferred;
    private static readonly Provenance A = Provenance.Ambiguous;

    /// <summary>
    /// The table. Grouped by why a family sits where it does, because the reason is what gets re-litigated
    /// when a new family arrives — and the reason is always the same one:
    /// <b>the tier describes the certainty of the asserted relationship, not the determinism of the parse.</b>
    /// Reading a <c>type</c> attribute out of XML is perfectly deterministic; whether that processor is
    /// actually registered at runtime is not.
    /// </summary>
    public static IReadOnlyList<Rule> Rules { get; } = new Rule[]
    {
        // -- Structural: "this node IS in this file". Deterministic by construction, and exempt from the
        //    parser baseline stamp for exactly that reason.
        new("CONTAINS",           Tiers(E),       E, "any parser (structural containment)"),
        new("DEFINED_IN",         Tiers(E),       E, "GraphQLParser (structural)"),

        // -- Roslyn semantic resolution: a resolved symbol, not a name match.
        new("CALLS",              Tiers(E),       E, "SemanticCsharpLinker / TypeScriptSemanticLinker"),
        new("INSTANTIATES",       Tiers(E),       E, "SemanticCsharpLinker"),
        new("OVERRIDES",          Tiers(E),       E, "SemanticCsharpLinker / TypeScriptSemanticLinker"),
        new("IMPLEMENTS_MEMBER",  Tiers(E),       E, "SemanticCsharpLinker / TypeScriptSemanticLinker"),

        // -- Multi-tier by design: the tier IS the resolution quality. Extracted = resolved symbol,
        //    Inferred = unique name match, Ambiguous = several candidates.
        new("REFERENCES_TYPE",    Tiers(E, I, A), null, "SemanticCsharpLinker (E) / CrossTechLinker (I) / AmbiguousCSharpTypePostProcessor (A)"),

        // -- Producer-ambiguous, deliberately NOT repairable here. RoslynAstParser emits these
        //    syntactically (the IMPLEMENTS-vs-EXTENDS split is a name heuristic: leading 'I' plus an
        //    uppercase second character) while SemanticCsharpLinker emits them resolved, and both currently
        //    land at Extracted — so the pair does not identify the producer. #402 creates the distinction;
        //    until then a repair cannot tell a proven edge from a guessed one.
        new("IMPLEMENTS",         Tiers(E, I),    null, "RoslynAstParser (syntactic) / SemanticCsharpLinker (resolved) -- see #402"),
        new("EXTENDS",            Tiers(E, I),    null, "RoslynAstParser (syntactic) / SemanticCsharpLinker (resolved) -- see #402"),

        // -- Model- and agent-authored. One population on purpose: all three are the AP7 target set, so a
        //    single tier gives that work a selection predicate instead of a case distinction.
        new("RELATES_TO",         Tiers(I),       I, "LLM concept promotion / MCP record tool"),
        new("INFLUENCES",         Tiers(I),       I, "MCP record tool (agent-authored)"),
        new("AFFECTS",            Tiers(I),       I, "MCP record tool (agent-authored)"),

        // -- Convention and path based.
        new("BELONGS_TO_MODULE",  Tiers(I),       I, "CrossTechLinker (path-based Helix module)"),
        new("BELONGS_TO_CONCEPT", Tiers(I),       I, "HelixSemanticPlugin (path-based Helix layer)"),
        new("IMPORTS",            Tiers(I),       I, "TypeScriptParser / Esprima fallback (name-based)"),
        new("REFERENCES",         Tiers(I),       I, "MarkdownHierarchyParser links / SitecoreUnicornPlugin"),
        new("OVERRIDES_BLOCK",    Tiers(I),       I, "PhpModuleParser"),
        new("BINDS_TO",           Tiers(I),       I, "CrossTechLinker"),

        // -- CMS configuration. Deterministic to READ, undecidable to ASSERT: Sitecore configs are patched,
        //    so a <processor> entry can be replaced or removed by patch:instead, by role-based config, or by
        //    a later file. "This config registers this processor" depends on the merged runtime
        //    configuration and is not decidable from a single file.
        new("REGISTERS_PROCESSOR",    Tiers(I), I, "SitecoreConfigPlugin"),
        new("REGISTERS_SERVICE",      Tiers(I), I, "SitecoreConfigPlugin"),
        new("REGISTERS_CONFIGURATOR", Tiers(I), I, "SitecoreConfigPlugin"),
        new("HANDLES_EVENT",          Tiers(I), I, "SitecoreConfigPlugin"),
        new("DEFINES_COMPONENT",      Tiers(I), I, "SitecoreXmCloudPlugin"),
        new("REFERENCES_ITEM",        Tiers(I), I, "FieldTypeReferencePostProcessor (GUID-shaped field value read as an item link)"),

        // -- Resolver output, two tiers by candidate count. A repair cannot recompute which, so it moves a
        //    violating edge to the WEAKER of the two: it can then only understate, it stays visible, and the
        //    next full scan raises it correctly through the normal merge.
        new("RESOLVES_TO",        Tiers(I, A),    A, "ClrTypeResolverPostProcessor (I = single candidate, A = several)"),
    };

    private static readonly Dictionary<string, Rule> ByRelationship =
        Rules.ToDictionary(r => r.Relationship, StringComparer.Ordinal);

    /// <summary>The rule for a relationship, or <c>null</c> when the table says nothing about it.</summary>
    public static Rule? RuleFor(string relationship) =>
        ByRelationship.TryGetValue(relationship, out var rule) ? rule : null;

    /// <summary>
    /// Splits a set of edges into those holding a tier their relationship may not hold, and those whose
    /// relationship this table does not cover. The two are returned apart on purpose: a wrong tier is a
    /// defect, an unknown relationship is only a gap in this table — most likely a third-party plugin — and
    /// treating them the same would either hide the first or make every new plugin a build failure.
    /// </summary>
    public static (IReadOnlyList<Violation> Violations, IReadOnlyList<Unclassified> Unclassified) Check(
        IEnumerable<GraphEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);

        var violations = new Dictionary<(string, Provenance), (Rule Rule, int Count, GraphEdge Sample)>();
        var unknown = new Dictionary<(string, Provenance), (int Count, GraphEdge Sample)>();

        foreach (var edge in edges)
        {
            var rule = RuleFor(edge.Relationship);
            if (rule == null)
            {
                var key = (edge.Relationship, edge.Provenance);
                unknown[key] = unknown.TryGetValue(key, out var u) ? (u.Count + 1, u.Sample) : (1, edge);
                continue;
            }

            if (rule.Legitimate.Contains(edge.Provenance)) continue;

            var vkey = (edge.Relationship, edge.Provenance);
            violations[vkey] = violations.TryGetValue(vkey, out var v)
                ? (v.Rule, v.Count + 1, v.Sample)
                : (rule, 1, edge);
        }

        return (
            violations
                .Select(kv => new Violation(kv.Key.Item1, kv.Key.Item2, kv.Value.Rule.RepairTo, kv.Value.Rule.Producer,
                    kv.Value.Count, kv.Value.Sample.SourceId, kv.Value.Sample.TargetId))
                .OrderByDescending(v => v.Count).ThenBy(v => v.Relationship, StringComparer.Ordinal)
                .ToList(),
            unknown
                .Select(kv => new Unclassified(kv.Key.Item1, kv.Key.Item2, kv.Value.Count, kv.Value.Sample.SourceId))
                .OrderByDescending(u => u.Count).ThenBy(u => u.Relationship, StringComparer.Ordinal)
                .ToList());
    }

    /// <summary>
    /// A human-readable report of a <see cref="Check"/> result — the "report mode" the repair migration
    /// prints before and after, and the failure message the guard test uses. Empty string when clean, so a
    /// caller can treat "no output" as "nothing to say".
    /// </summary>
    public static string Report(
        IReadOnlyList<Violation> violations,
        IReadOnlyList<Unclassified> unclassified)
    {
        if (violations.Count == 0 && unclassified.Count == 0) return string.Empty;

        var sb = new System.Text.StringBuilder();
        if (violations.Count > 0)
        {
            sb.AppendLine($"{violations.Sum(v => v.Count)} edge(s) hold a trust tier their relationship may not hold:");
            foreach (var v in violations)
            {
                var target = v.RepairTo is { } r ? r.ToString().ToLowerInvariant() : "not repairable from (relation, tier) alone";
                sb.AppendLine($"  {v.Relationship} at {v.Actual.ToString().ToLowerInvariant()} x{v.Count} -> {target}");
                sb.AppendLine($"      producer: {v.Producer}");
                sb.AppendLine($"      example:  {v.SampleSourceId} -> {v.SampleTargetId}");
            }
        }
        if (unclassified.Count > 0)
        {
            sb.AppendLine($"{unclassified.Sum(u => u.Count)} edge(s) use a relationship this table does not cover (likely a third-party plugin):");
            foreach (var u in unclassified)
            {
                sb.AppendLine($"  {u.Relationship} at {u.Tier.ToString().ToLowerInvariant()} x{u.Count}  e.g. {u.SampleSourceId}");
            }
        }
        return sb.ToString().TrimEnd();
    }
}
