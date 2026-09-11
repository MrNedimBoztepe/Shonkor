// Licensed to Shonkor under the MIT License.

using System.Globalization;
using System.Text;

namespace Shonkor.Tests;

/// <summary>
/// Reads <c>bench/golden/ap6/rg-command-cases.tsv</c> — the single case table for "is this one plain
/// ripgrep command?" (#513, #514). The same file is replayed against the real hook by
/// <c>bench/golden/ap6/rg-only-hook.test.sh</c>; here it drives
/// <see cref="Ap6RunReaderTests.IsRgCommand_MatchesTheSharedCaseTable"/>. Two implementations, one table:
/// a rule that changes on one side only fails a test instead of quietly letting a command execute that the
/// scorer then counts as clean.
/// </summary>
internal static class Ap6RgCommandCases
{
    public static string TablePath => RepoPaths.File("bench", "golden", "ap6", "rg-command-cases.tsv");

    public static IReadOnlyList<(string Command, bool Allow)> Load()
    {
        var cases = new List<(string, bool)>();
        foreach (var raw in File.ReadAllLines(TablePath))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0) throw new FormatException($"rg-command-cases.tsv: no tab in '{line}'");
            var command = Decode(line[..tab]);
            var verdict = line[(tab + 1)..].Split('\t')[0];
            cases.Add(verdict switch
            {
                "allow" => (command, true),
                "deny" => (command, false),
                _ => throw new FormatException($"rg-command-cases.tsv: verdict '{verdict}' is neither allow nor deny"),
            });
        }
        return cases;
    }

    /// <summary>
    /// The table's escape alphabet, and deliberately only that: bash's <c>printf %b</c> passes an escape it
    /// does not know through unchanged, so an unknown one here must throw rather than be decoded differently
    /// on the two sides.
    /// </summary>
    private static string Decode(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\') { sb.Append(text[i]); continue; }
            if (i + 1 >= text.Length) throw new FormatException("rg-command-cases.tsv: a line ends in a lone backslash");
            var c = text[++i];
            switch (c)
            {
                case '\\': sb.Append('\\'); break;
                case 't': sb.Append('\t'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 'v': sb.Append('\v'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    if (i + 4 >= text.Length) throw new FormatException("rg-command-cases.tsv: \\u needs four hex digits");
                    sb.Append((char)int.Parse(text.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default: throw new FormatException($"rg-command-cases.tsv: unknown escape '\\{c}' — the table's alphabet is \\\\ \\t \\n \\r \\v \\f \\uXXXX");
            }
        }
        return sb.ToString();
    }
}
