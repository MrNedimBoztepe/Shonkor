// Licensed to Shonkor under the MIT License.

using Shonkor.Core.Services;
using Shonkor.Infrastructure.Services;

namespace Shonkor.Tests;

/// <summary>
/// Tests the edit-validation diagnostics (the check_edit tool's core): syntax errors are always reported,
/// semantic errors come from the project compilation, and un-referenced-package noise is suppressed.
/// </summary>
public class CSharpDiagnosticsTests
{
    [Fact]
    public void CleanFile_ReportsOk()
    {
        var report = CSharpDiagnostics.Report("/x/C.cs", "namespace N { public class C { public void M() { } } }", null);
        Assert.StartsWith("OK", report);
    }

    [Fact]
    public void SyntaxError_IsReported_WithoutCompilation()
    {
        // Missing closing brace — a parser error, reliable with no compilation.
        var report = CSharpDiagnostics.Report("/x/C.cs", "namespace N { public class C { public void M() { ", null);
        Assert.Contains("syntax error", report, StringComparison.OrdinalIgnoreCase);
        Assert.False(report.StartsWith("OK", StringComparison.Ordinal));
    }

    [Fact]
    public void SemanticError_IsReported_ForInCodebaseSymbols()
    {
        var full = System.IO.Path.GetFullPath("/x/C.cs");
        var code = "namespace N { public class C { public void M() { DoesNotExist(); } } }";
        var comp = RoslynSemantics.BuildCompilation(new[] { (full, code) });

        var report = CSharpDiagnostics.Report(full, code, comp);
        // Calling an undefined in-codebase member is a real error the agent should see (CS0103).
        Assert.Contains("CS0103", report);
        Assert.Contains("semantic error", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnreferencedExternalType_IsSuppressedAsNoise()
    {
        var full = System.IO.Path.GetFullPath("/x/D.cs");
        // SomeExternalBase isn't in the codebase and there's no NuGet reference -> CS0246, which is
        // reference noise (not the edit's fault) and must be suppressed.
        var code = "namespace N { public class D : SomeExternalBase { } }";
        var comp = RoslynSemantics.BuildCompilation(new[] { (full, code) });

        var report = CSharpDiagnostics.Report(full, code, comp);
        Assert.DoesNotContain("CS0246", report);
        Assert.StartsWith("OK", report); // no syntax errors, the only semantic one was suppressed
    }
}
