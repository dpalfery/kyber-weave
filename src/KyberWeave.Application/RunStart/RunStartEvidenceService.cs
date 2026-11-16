using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;

namespace KyberWeave.Application.RunStart;

/// <summary>Answers whether a run is live for a repository, from the run records and git's answer for the repository.</summary>
/// <param name="git">Names the repository's common git directory.</param>
/// <param name="runs">Reads the run records.</param>
public sealed class RunStartEvidenceService(IGitRepositoryFacts git, IRunStateStore runs) : IRunStartEvidence
{
    /// <inheritdoc />
    public bool? Started(string workingDirectory, string homeDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || string.IsNullOrWhiteSpace(homeDirectory))
        {
            return null;
        }

        string? repository = git.GetCommonDirectory(workingDirectory);
        return repository is null ? null : runs.HasLiveRun(homeDirectory, repository);
    }
}
