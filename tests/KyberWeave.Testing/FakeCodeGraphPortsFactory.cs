using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Domain.CodeGraph;

namespace KyberWeave.Tests;

/// <summary>
/// Answers every CodeGraph port over one resolver, so a caller that takes
/// <see cref="ICodeGraphPortsFactory"/> by injection can be tested without an index.
/// </summary>
/// <remarks>
/// A port the supplied resolver does not implement answers empty or current, and the factory records each
/// repository root it was asked for, so a test can prove the caller used it.
/// </remarks>
internal sealed class FakeCodeGraphPortsFactory(ICodeGraphResolver resolver) : ICodeGraphPortsFactory
{
    private readonly ICodeGraphResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    private readonly List<string> _roots = [];

    /// <summary>The repository roots the caller asked for, in order.</summary>
    public IReadOnlyList<string> Roots => _roots;

    /// <inheritdoc />
    public CodeGraphPorts ForRepository(string repositoryRoot)
    {
        _roots.Add(repositoryRoot);
        return new CodeGraphPorts(
            _resolver,
            _resolver as ICodeGraphNeighborhoodProvider ?? NoNeighborhood.Instance,
            _resolver as ICodeGraphSymbolEnumerator ?? EmptyEnumerator.Instance,
            _resolver as ICodeGraphIndexFreshness ?? CurrentIndex.Instance);
    }

    /// <inheritdoc />
    public Task<CodeGraphPorts> ForRepositoryAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(ForRepository(repositoryRoot));

    private sealed class NoNeighborhood : ICodeGraphNeighborhoodProvider
    {
        public static NoNeighborhood Instance { get; } = new();

        public IReadOnlyList<CodeGraphEdge> GetEdges(IReadOnlyCollection<string> nodeIds, int maxDegree) => [];
    }

    private sealed class EmptyEnumerator : ICodeGraphSymbolEnumerator
    {
        public static EmptyEnumerator Instance { get; } = new();

        public IReadOnlyList<CodeGraphNode> NodesOfKind(string kind) => [];
    }

    private sealed class CurrentIndex : ICodeGraphIndexFreshness
    {
        public static CurrentIndex Instance { get; } = new();

        public string? DescribeStaleness() => null;

        public Task<string?> DescribeStalenessAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }
}
