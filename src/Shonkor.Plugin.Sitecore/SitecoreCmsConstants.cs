using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Shonkor.Plugin.Sitecore;

/// <summary>
/// Shared Sitecore identifiers and field-type knowledge used by the phase-2 post-processors. Kept in one
/// place so the GUID normalisation here matches <c>SitecoreUnicornPlugin.NormalizeGuid</c> exactly (item
/// ids and edge targets must canonicalise identically) and the reference/text field-type sets have a single
/// auditable source.
/// </summary>
internal static class SitecoreCmsConstants
{
    private static readonly Regex GuidRegex = new(
        @"[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    /// <summary>The Sitecore "Template field" template — items based on it are field definitions carrying a "Type".</summary>
    public const string TemplateFieldTemplateId = "455a3e98-a627-4b40-8035-e683a0331ac7";

    /// <summary>The field name (Hint) on a template-field item that holds its data type; stored by the parser as Field_Type.</summary>
    public const string FieldTypePropertyKey = "Field_Type";

    /// <summary>
    /// Field types whose stored value is one or more item GUIDs — i.e. a REAL item reference. Lowercased.
    /// (Droplist and Name Value List store the item's NAME/text, not its id, so they are NOT here.)
    /// </summary>
    public static readonly IReadOnlySet<string> ReferenceFieldTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "droplink", "droptree", "grouped droplink", "reference",
        "multilist", "multilist with search", "treelist", "treelist with search", "treelistex",
        "multiroot treelist", "checklist", "tag", "taglist",
        "general link", "general link with search", "internal link",
        "image", "file", "rules", "datasource"
    };

    /// <summary>
    /// Field types whose value is free text/number — a GUID-looking substring here is NOT an item link, so a
    /// generic REFERENCES edge minted from it is spurious. Lowercased. Kept conservative (only positively
    /// non-reference types) so the spurious-reference diagnostic has near-zero false positives.
    /// </summary>
    public static readonly IReadOnlySet<string> TextFieldTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "single-line text", "multi-line text", "rich text", "memo",
        "integer", "number", "checkbox", "date", "datetime", "password", "tristate", "droplist"
    };

    /// <summary>
    /// Well-known Sitecore standard items that live in the core/master DB and are intentionally NOT serialized
    /// in a project repo. Used to suppress coverage noise when a referenced template/rendering is one of these.
    /// Small and curated on purpose — coverage diagnostics are advisory (Info), so partial coverage is fine.
    /// </summary>
    public static readonly IReadOnlySet<string> SystemItemDenylist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "1930bbeb-7805-471a-a3be-4858ac7cf696", // Standard template
        "ab86861a-6030-46c5-b394-e8f99e8b87db", // Template
        "e269fbb5-3750-427a-9149-7aa950b49301", // Template section
        TemplateFieldTemplateId,                // Template field
        "a87a00b1-e6db-45ab-8b54-636fec3b5523", // Folder
        "0437fee2-44c9-46a6-abe9-28858d9fee8c", // Template folder
        "c97ba923-8009-4858-bdd5-d8be5fccecf7", // Main section (common base)
        "00000000-0000-0000-0000-000000000000"  // null id
    };

    /// <summary>Canonicalises a Sitecore GUID to lowercase, dashed, brace-less form (matches the Unicorn parser).</summary>
    public static string NormalizeGuid(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw ?? string.Empty;
        var m = GuidRegex.Match(raw);
        if (!m.Success) return raw;
        return Guid.TryParse(m.Value, out var g) ? g.ToString("D") : m.Value.ToLowerInvariant();
    }

    /// <summary>All distinct normalised GUIDs found in a field value (e.g. a pipe-separated multilist).</summary>
    public static IEnumerable<string> GuidsIn(string value)
    {
        if (string.IsNullOrEmpty(value)) yield break;
        foreach (Match m in GuidRegex.Matches(value))
        {
            yield return NormalizeGuid(m.Value);
        }
    }
}
