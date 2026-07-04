// Licensed to Shonkor under the MIT License.

namespace Shonkor.Core.Models;

/// <summary>
/// The trust tier of a graph edge: how the relationship was established. This is Shonkor's core
/// differentiator — it lets a consumer distinguish a compiler-accurate fact from a heuristic or
/// model-generated guess, and it is non-negotiable that model/heuristic sources never claim
/// <see cref="Extracted"/> (see docs/projects/language-breadth.md).
/// </summary>
public enum Provenance
{
    /// <summary>
    /// Deterministically extracted by a language-exact parser (e.g. Roslyn syntax/semantics, or
    /// structural containment from a markup grammar). Reproducible, no inference involved.
    /// </summary>
    Extracted = 0,

    /// <summary>
    /// Derived heuristically — regex/name-based matching, cross-technology linking, Tree-sitter
    /// syntactic patterns without semantic resolution, or LLM-generated. Plausible but not proven.
    /// </summary>
    Inferred = 1,

    /// <summary>
    /// A reference that resolves to more than one candidate (e.g. a <c>clrtype:</c> name matching
    /// several C# types), surfaced as a low-confidence edge rather than a wrong hard link.
    /// </summary>
    Ambiguous = 2
}
