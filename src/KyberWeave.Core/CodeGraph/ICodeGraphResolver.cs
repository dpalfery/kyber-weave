namespace KyberWeave.Core.CodeGraph;

/// <summary>
/// Port over a CodeGraph index: symbol and route lookups used by drift, export, and retrieval.
/// </summary>
/// <remarks>
/// Hosts inject a concrete adapter (typically <see cref="CodeGraphResolverAdapter"/>).
/// Tests inject fakes so drift and join behaviour can be driven without sqlite3.
/// </remarks>
public interface ICodeGraphResolver
{
    /// <summary>True when an index was found and read.</summary>
    bool IsAvailable { get; }

    /// <summary>Why the index could not be read, when <see cref="IsAvailable"/> is false.</summary>
    string? UnavailableReason { get; }

    /// <summary>Where the index was looked for, for diagnostics.</summary>
    string DatabasePath { get; }

    /// <summary>Resolves a bare symbol name, or a fully qualified name, to every match.</summary>
    IReadOnlyList<CodeGraphNode> ResolveSymbol(string name);

    /// <summary>Resolves an exact <c>METHOD /path</c> route string.</summary>
    IReadOnlyList<CodeGraphNode> ResolveRoute(string route);

    /// <summary>True when the index contains at least one file beneath the given path prefix.</summary>
    bool HasFilesUnder(string relativePathPrefix);

    /// <summary>Candidate symbol names for a "did you mean" hint after a failed resolve.</summary>
    IReadOnlyList<string> CandidateNames(string likePattern);

    /// <summary>All indexed route strings, for a "did you mean" hint.</summary>
    IReadOnlyList<string> AllRoutes();
}
