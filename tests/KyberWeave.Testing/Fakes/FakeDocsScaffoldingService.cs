using System.Collections.ObjectModel;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Docs.Scaffolding;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// The one fake for <see cref="IDocsScaffoldingService"/>: it records each scaffolding request and writes nothing, so a
/// test can assert whether a command scaffolded at all without a corpus being built.
/// </summary>
public sealed class FakeDocsScaffoldingService : IDocsScaffoldingService
{
    /// <summary>Gets the repository roots a scaffold was asked for, in call order.</summary>
    public Collection<string> ScaffoldedRoots { get; } = [];

    /// <summary>Gets or sets the failure a scaffold raises instead of answering; null answers normally.</summary>
    public Exception? Failure { get; set; }

    /// <inheritdoc />
    public ScaffoldResult Scaffold(
        string repoRoot,
        string? docsRoot = null,
        string owner = "unassigned",
        bool force = false,
        bool kyberStandards = false,
        bool reviewGates = true,
        IReadOnlyList<string>? standards = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        if (Failure is { } failure)
        {
            throw failure;
        }

        ScaffoldedRoots.Add(repoRoot);
        string root = docsRoot ?? Path.Combine(repoRoot, "docs");
        return new ScaffoldResult(root, DocsRootSource.Convention, []);
    }
}
