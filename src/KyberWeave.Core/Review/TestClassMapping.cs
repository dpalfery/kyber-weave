using KyberWeave.Application.Review;
using KyberWeave.Contracts.Services;
using KyberWeave.Data.Review;

namespace KyberWeave.Core.Review;

/// <summary>Resolves the test classes that cover a change, for the callers that have no container.</summary>
/// <remarks>
/// The logic is <see cref="TaskGateSelectionService"/> in the application layer behind <see cref="ITaskGateSelectionService"/>,
/// and the pure rules are <see cref="TestClassPlanning"/>.
/// </remarks>
public static class TestClassMapping
{
    private static readonly ITaskGateSelectionService Service =
        new TaskGateSelectionService(new FileRepositoryTreeProbe(), new FileTestClassSources());

    /// <inheritdoc cref="ITaskGateSelectionService.ResolveTestClasses" />
    public static IReadOnlyList<string> Resolve(
        string? root,
        IReadOnlyList<string> changed,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? declared = null) =>
        Service.ResolveTestClasses(root, changed, declared);
}
