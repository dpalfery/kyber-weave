using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Review;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="ITestClassSources"/>: opens the tree a test declares and reads the namespaces it declares.</summary>
internal sealed class FakeTestClassSources : ITestClassSources
{
    /// <summary>The repository root the tree reports.</summary>
    public string Root { get; init; } = "/repo";

    /// <summary>The source files by class name, without the extension; empty unless the test fills it.</summary>
    public IReadOnlyDictionary<string, string> SourcesByName { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The namespace each source file declares, by full path; a file not named here declares none.</summary>
    public IReadOnlyDictionary<string, string> NamespacesByPath { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Whether no tree can be opened, as for a root that does not exist.</summary>
    public bool Missing { get; init; }

    /// <inheritdoc />
    public TestSourceTree? Open(string? root) => Missing ? null : new TestSourceTree(Root, SourcesByName);

    /// <inheritdoc />
    public string? ReadDeclaredNamespace(string filePath) => NamespacesByPath.GetValueOrDefault(filePath);
}
