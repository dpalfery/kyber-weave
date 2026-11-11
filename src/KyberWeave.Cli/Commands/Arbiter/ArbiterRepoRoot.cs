namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Finds the repository root a file-reading Arbiter command contains its paths under.</summary>
internal static class ArbiterRepoRoot
{
    /// <summary>
    /// The nearest directory at or above <paramref name="start"/> that holds a <c>.git</c> entry (a directory in a
    /// clone, a file in a worktree), or <paramref name="start"/> itself when there is none.
    /// </summary>
    /// <remarks>
    /// The walk stops at the first match rather than following any link, so the root is the one git itself would name.
    /// A command run outside any repository contains its paths under the directory it was run in.
    /// </remarks>
    public static string Find(string start)
    {
        string full = System.IO.Path.GetFullPath(start);
        for (DirectoryInfo? directory = new(full); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(System.IO.Path.Combine(directory.FullName, ".git"))
                || File.Exists(System.IO.Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
        }

        return full;
    }
}
