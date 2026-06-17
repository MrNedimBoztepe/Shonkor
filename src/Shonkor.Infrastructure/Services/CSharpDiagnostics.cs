// Licensed to Shonkor under the MIT License.

using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Shonkor.Infrastructure.Services;

/// <summary>
/// Turns a C# file (+ optional project compilation) into an agent-friendly diagnostics report: did this
/// edit break the file? Syntax errors are always reliable; semantic errors are best-effort, since the
/// self-contained compilation resolves only in-codebase symbols (R1 ref-assemblies, no NuGet) — so
/// "type/namespace not found" style diagnostics (which would otherwise flood NuGet-heavy projects) are
/// suppressed as reference noise rather than reported as the edit's fault.
/// </summary>
public static class CSharpDiagnostics
{
    /// <summary>
    /// Diagnostic IDs that indicate an UN-referenced (e.g. NuGet) type/assembly under the R1 compilation,
    /// not a fault in the edit. Suppressed from the semantic section so it stays signal, not noise.
    /// </summary>
    private static readonly HashSet<string> UnresolvedReferenceCodes = new(StringComparer.Ordinal)
    {
        "CS0246", // type or namespace not found
        "CS0234", // namespace member not found
        "CS0518", // predefined type not defined
        "CS1069", // type not found in namespace (forwarded)
        "CS0012", // type defined in an un-referenced assembly
        "CS0006", // metadata file not found
        "CS0400", // global namespace type not found
    };

    /// <summary>
    /// Builds the report. <paramref name="compilation"/> is the project compilation (current tree for the
    /// file already swapped in); pass <c>null</c> for syntax-only (non-semantic project / no cache).
    /// </summary>
    public static string Report(string filePath, string content, CSharpCompilation? compilation)
    {
        var fileName = Path.GetFileName(filePath);

        var tree = CSharpSyntaxTree.ParseText(content, path: filePath);
        var syntax = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        var semantic = new List<Diagnostic>();
        var semanticChecked = false;
        if (compilation is not null)
        {
            var compTree = compilation.SyntaxTrees
                .FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (compTree is not null)
            {
                semanticChecked = true;
                // GetSemanticModel(tree).GetDiagnostics() is scoped to THIS tree — bounded, not whole-repo.
                semantic = compilation.GetSemanticModel(compTree).GetDiagnostics()
                    .Where(d => d.Severity == DiagnosticSeverity.Error && !UnresolvedReferenceCodes.Contains(d.Id))
                    .ToList();
            }
        }

        if (syntax.Count == 0 && semantic.Count == 0)
        {
            return semanticChecked
                ? $"OK — no syntax or semantic errors in {fileName} (semantic checks cover in-codebase symbols; external-package types aren't analyzed)."
                : $"OK — no syntax errors in {fileName}. (Semantic checks need a semantic project; enable SemanticCSharp for type/call validation.)";
        }

        var sb = new StringBuilder();
        sb.Append($"{fileName}: {syntax.Count} syntax error(s), {semantic.Count} semantic error(s).\n");

        if (syntax.Count > 0)
        {
            sb.Append("\nSyntax errors (reliable):\n");
            foreach (var d in syntax.OrderBy(LineOf)) sb.Append(Format(d));
        }
        if (semantic.Count > 0)
        {
            sb.Append("\nSemantic errors (best-effort; external-package types not analyzed):\n");
            foreach (var d in semantic.OrderBy(LineOf)) sb.Append(Format(d));
        }
        return sb.ToString().TrimEnd();
    }

    private static int LineOf(Diagnostic d) => d.Location.GetLineSpan().StartLinePosition.Line;

    private static string Format(Diagnostic d)
    {
        var line = d.Location.GetLineSpan().StartLinePosition.Line + 1; // 1-based for humans/agents
        return $"  line {line}: {d.Id}: {d.GetMessage()}\n";
    }
}
