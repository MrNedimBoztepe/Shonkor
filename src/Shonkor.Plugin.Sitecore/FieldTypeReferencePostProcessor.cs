using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Shonkor.Core.Interfaces;
using Shonkor.Core.Models;

namespace Shonkor.Plugin.Sitecore;

/// <summary>
/// Phase-2 enrichment + diagnostic (F3): field-type awareness — distinguishes REAL item references from
/// incidental GUID-looking text. Phase 1 mints a generic <c>REFERENCES</c> edge for any GUID in any
/// non-standard field, which is noisy because a field's <i>type</i> (defined on its template, a different
/// file) decides whether its value is actually an item link. This pass learns each field's type from the
/// serialized "Template field" items, then:
/// <list type="bullet">
/// <item>emits a high-confidence <c>REFERENCES_ITEM</c> edge for GUIDs held in a known reference-type field
/// (Droplink, Multilist, General Link, …) — a clean layer impact analysis can trust over raw REFERENCES;</item>
/// <item>raises a <c>sitecore.spurious-reference</c> Info diagnostic when a GUID sits in a positively
/// non-reference field (Single-Line Text, Integer, …), where the generic REFERENCES edge is likely bogus.</item>
/// </list>
/// Reads persisted node field values (edge metadata isn't persisted). Conservative: fields of unknown or
/// ambiguous type are left untouched.
/// </summary>
public sealed class FieldTypeReferencePostProcessor : IGraphPostProcessor
{
    public string Name => "sitecore.field-type-references";

    // Field property keys are "Field_{Hint}" (shared) or "Field_{lang}_{version}_{Hint}" (versioned); recover the Hint.
    private static readonly Regex FieldKeyPattern = new(
        @"^Field_(?:[A-Za-z]{2}(?:-[A-Za-z]{2})?_\d+_)?(?<hint>.+)$", RegexOptions.Compiled);

    public async Task<GraphEnrichment> ProcessAsync(IGraphView graph)
    {
        var items = await graph.NodesByTypeAsync("SitecoreItem").ConfigureAwait(false);

        // 1. Identify the field-definition items (based on the "Template field" template) so we can read each
        //    field's declared Type, and so we can skip them when scanning content for references.
        var fieldItemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var basedOn = await graph.EdgesByRelationshipAsync("BASED_ON_TEMPLATE").ConfigureAwait(false);
        foreach (var e in basedOn)
        {
            if (string.Equals(e.TargetId, SitecoreCmsConstants.TemplateFieldTemplateId, StringComparison.OrdinalIgnoreCase))
                fieldItemIds.Add(e.SourceId);
        }

        // 2. Build a field-name -> type-classification map from those field items (Name = field name, Field_Type = type).
        var refNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var textNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!fieldItemIds.Contains(item.Id)) continue;
            if (!item.Properties.TryGetValue(SitecoreCmsConstants.FieldTypePropertyKey, out var type) || string.IsNullOrWhiteSpace(type))
                continue;

            var name = item.Name;
            var isRef = SitecoreCmsConstants.ReferenceFieldTypes.Contains(type.Trim());
            var isText = SitecoreCmsConstants.TextFieldTypes.Contains(type.Trim());
            if (isRef) refNames.Add(name);
            if (isText) textNames.Add(name);
            // A field name typed as BOTH a reference and a text type across templates is ambiguous → trust neither.
            if (refNames.Contains(name) && textNames.Contains(name)) ambiguous.Add(name);
        }

        var edges = new List<GraphEdge>();
        var diagnostics = new List<GraphDiagnostic>();
        var emittedEdges = new HashSet<string>(StringComparer.Ordinal);
        var reportedDiags = new HashSet<string>(StringComparer.Ordinal);

        // 3. Scan content items' fields and classify the GUIDs they hold by the field's known type.
        foreach (var item in items)
        {
            if (fieldItemIds.Contains(item.Id)) continue; // field-definition metadata, not content

            foreach (var (key, value) in item.Properties)
            {
                var m = FieldKeyPattern.Match(key);
                if (!m.Success) continue;
                var hint = m.Groups["hint"].Value;
                if (hint.StartsWith("__", StringComparison.Ordinal)) continue; // standard fields handled elsewhere
                if (ambiguous.Contains(hint)) continue;

                var isRef = refNames.Contains(hint);
                var isText = textNames.Contains(hint);
                if (!isRef && !isText) continue; // unknown type — leave the raw REFERENCES edge as-is

                foreach (var target in SitecoreCmsConstants.GuidsIn(value))
                {
                    if (string.Equals(target, item.Id, StringComparison.OrdinalIgnoreCase)) continue;

                    if (isRef)
                    {
                        if (emittedEdges.Add($"{item.Id}->{target}"))
                        {
                            edges.Add(new GraphEdge { SourceId = item.Id, TargetId = target, Relationship = "REFERENCES_ITEM" });
                        }
                    }
                    else // positively a text/number field — a GUID here is not an item link
                    {
                        if (reportedDiags.Add($"{item.Id}|{hint}|{target}"))
                        {
                            diagnostics.Add(new GraphDiagnostic(
                                "sitecore.spurious-reference", DiagnosticSeverity.Info,
                                $"'{item.Name}' field '{hint}' is a non-reference field but holds an item GUID '{target}' — the generic reference is likely spurious.",
                                item.Id, item.FilePath));
                        }
                    }
                }
            }
        }

        return new GraphEnrichment(Array.Empty<GraphNode>(), edges, diagnostics);
    }
}
