using KyberWeave.Contracts.Repositories;
using KyberWeave.Domain.CodeGraph;

namespace KyberWeave.Tests;

/// <summary>An <see cref="ICodeGraphResolver"/> that reports no readable index, as a checkout or runner without one does.</summary>
internal sealed class UnavailableCodeGraphResolver : ICodeGraphResolver
{
    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public string? UnavailableReason => "No CodeGraph index at .codegraph/codegraph.db.";

    /// <inheritdoc />
    public string DatabasePath => ".codegraph/codegraph.db";

    /// <inheritdoc />
    public IReadOnlyList<CodeGraphNode> ResolveSymbol(string name) => [];

    /// <inheritdoc />
    public IReadOnlyList<CodeGraphNode> ResolveRoute(string route) => [];

    /// <inheritdoc />
    public bool HasFilesUnder(string relativePathPrefix) => false;

    /// <inheritdoc />
    public IReadOnlyList<string> CandidateNames(string likePattern) => [];

    /// <inheritdoc />
    public IReadOnlyList<string> AllRoutes() => [];
}
