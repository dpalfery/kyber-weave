using KyberWeave.Data.Review;

namespace KyberWeave.Core.Configuration;

/// <summary>Merges and validates the <c>review:</c> host configuration section, for the callers that have no container.</summary>
/// <remarks>
/// The parse is <see cref="ReviewConfigParser"/> in the domain layer; this answers its questions about a gate's
/// <c>working-directory</c> from the real file system.
/// </remarks>
public static class ReviewConfigLoader
{
    private static readonly FileRepositoryTreeProbe Tree = new();

    /// <inheritdoc cref="ReviewConfigParser.Merge" />
    internal static ReviewConfig Merge(
        ReviewConfig defaults,
        ReviewYamlSection? section,
        string? repositoryRoot = null,
        IReadOnlyList<string>? warnings = null) =>
        ReviewConfigParser.Merge(
            defaults,
            section,
            repositoryRoot,
            warnings,
            new ReviewWorkingDirectoryFacts(Tree.DirectoryExists, Tree.LeadsOutsideThroughSymlink));

    /// <inheritdoc cref="ReviewConfigParser.UnknownKeyWarnings" />
    internal static IReadOnlyList<string> UnknownKeyWarnings(string yaml) => ReviewConfigParser.UnknownKeyWarnings(yaml);
}
