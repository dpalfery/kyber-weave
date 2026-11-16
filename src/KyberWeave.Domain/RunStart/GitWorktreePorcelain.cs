namespace KyberWeave.Core.RunStart;

/// <summary>Reads the output of <c>git worktree list --porcelain</c>.</summary>
public static class GitWorktreePorcelain
{
    private const string WorktreePrefix = "worktree ";

    /// <summary>Whether the porcelain output reports more than one worktree.</summary>
    /// <param name="porcelain">The output of <c>git worktree list --porcelain</c>.</param>
    public static bool HasMultiple(string porcelain)
    {
        ArgumentNullException.ThrowIfNull(porcelain);
        return porcelain
            .Split('\n')
            .Count(line => line.StartsWith(WorktreePrefix, StringComparison.Ordinal)) > 1;
    }

    /// <summary>The worktree that has <paramref name="branch" /> checked out, or null when none does.</summary>
    /// <param name="porcelain">The output of <c>git worktree list --porcelain</c>.</param>
    /// <param name="branch">The branch to look for.</param>
    public static string? Holding(string porcelain, string branch)
    {
        ArgumentNullException.ThrowIfNull(porcelain);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        string branchLine = "branch refs/heads/" + branch;
        string? current = null;
        foreach (string raw in porcelain.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith(WorktreePrefix, StringComparison.Ordinal))
                current = line[WorktreePrefix.Length..];
            else if (string.Equals(line, branchLine, StringComparison.Ordinal))
                return current;
        }

        return null;
    }
}
