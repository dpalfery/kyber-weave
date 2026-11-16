using KyberWeave.Data.Review;

namespace KyberWeave.Core.Review.Catalogue;

/// <summary>Forwards the project-root detection the Docs scaffolding still calls statically to the layered detector and file lister.</summary>
/// <remarks>Delete once the scaffolding takes <c>IRepositoryFileLister</c> from the container.</remarks>
internal static class ProjectRootDetector
{
    /// <summary>A family with more roots than this has its gates written commented out by the caller.</summary>
    internal const int MaxRootsPerFamily = ProjectRootDetection.MaxRootsPerFamily;

    /// <inheritdoc cref="ProjectRootDetection.Detect" />
    public static ProjectRootSet Detect(
        string repositoryRoot,
        string[] markers,
        ProjectRootNesting nesting,
        bool pair = false,
        string[]? fallback = null) =>
        ProjectRootDetection.Detect(
            new FileRepositoryFileLister(new ProcessProgramRunner()).List(repositoryRoot), markers, nesting, pair, fallback);
}
