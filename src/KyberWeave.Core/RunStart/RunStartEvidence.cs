using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Core.RunStart;

/// <summary>
/// Whether a run has been started before an orchestrator hands a specialist work that writes files. Read
/// by <c>KW-ARB-RUN-001</c> and by the Copilot conductor guard, from machine facts only: the record
/// <c>run start</c> wrote and git's answer for the repository and the run's worktree.
/// </summary>
public static class RunStartEvidence
{
    /// <summary>The fix a refused dispatch names.</summary>
    public const string Remedy =
        "start the run first: kyber-weave run start --harness <harness> --type <type> --slug <slug> " +
        "(or dispatch github-devops with RUN-START: true and that command), then send this dispatch again.";

    private static readonly HashSet<string> WriterProfiles = new(StringComparer.Ordinal) { "worker", "documentation" };

    /// <summary>Whether a specialist with <paramref name="capabilityProfile" /> writes project files.</summary>
    /// <remarks>
    /// <c>publishing-worker</c> (github-devops) is left out on purpose: it is the route that runs
    /// <c>run start</c> for a harness with no shell, so gating it on a started run would refuse its own fix.
    /// </remarks>
    public static bool IsWriterProfile(string? capabilityProfile) =>
        capabilityProfile is not null && WriterProfiles.Contains(capabilityProfile);

    /// <summary>Whether a run is live for the repository <paramref name="workingDirectory" /> belongs to.</summary>
    /// <returns>True or false from the records; null when git cannot name the repository or the records cannot be read.</returns>
    public static bool? Started(string workingDirectory, string homeDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || string.IsNullOrWhiteSpace(homeDirectory))
        {
            return null;
        }

        string? repository = GitFacts.GetCommonDirectory(workingDirectory);
        return repository is null ? null : RunBaseState.HasLiveRun(homeDirectory, repository);
    }
}
