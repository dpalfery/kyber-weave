using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// The plan path seam in a unit test: a repository-relative plan resolves inside the root it is given, and a plan
/// that leaves that root — an absolute path elsewhere, a <c>..</c> escape, a rejected root — resolves to null, as
/// the production <c>PlanPathResolver</c> answers.
/// </summary>
public sealed class ContainedPlanFileResolver : IPlanFileResolver
{
    /// <inheritdoc />
    public string? Resolve(string? planFile, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(planFile))
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        string candidate = Path.GetFullPath(Path.IsPathRooted(planFile)
            ? planFile
            : Path.Combine(root, planFile));
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? candidate
            : null;
    }
}
