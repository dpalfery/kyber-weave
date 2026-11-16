using KyberWeave.Data.Review;

namespace KyberWeave.Core.Review;

/// <summary>Forwards the two changed-file calls the Docs engine still makes statically to the layered git reader.</summary>
/// <remarks>Delete once the Docs schema gate takes <c>IGitChangeReader</c> from the container.</remarks>
public static class ChangedFiles
{
    /// <summary>The ref the working tree is compared with when none is named.</summary>
    public const string DefaultBase = GitChangeParsing.DefaultBase;

    /// <inheritdoc cref="GitChangeParsing.ValidateBase" />
    public static string? ValidateBase(string baseRef) => GitChangeParsing.ValidateBase(baseRef);

    /// <inheritdoc cref="KyberWeave.Contracts.Repositories.IGitChangeReader.ResolveSinceMergeBase" />
    public static IReadOnlyList<ChangedPath> ResolveSinceMergeBase(string workingDirectory, string baseRef) =>
        new GitChangeReader(new ProcessProgramRunner()).ResolveSinceMergeBase(workingDirectory, baseRef);
}
