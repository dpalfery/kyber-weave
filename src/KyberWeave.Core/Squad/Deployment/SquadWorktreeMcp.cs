using JetBrains.Annotations;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
///     Provides helper functions for worktree MCP canonical binding and environment isolation.
/// </summary>
/// <remarks>
///     <para>
///         When concurrent tasks execute in isolated git worktrees, MCP servers such as
///         <c>kyber-weave-mcp</c> and <c>codegraph</c> must be bound to the canonical repository root
///         rather than the ephemeral worktree directory.
///     </para>
///     <para>
///         This helper splices command-line arguments to resolve <c>--repo-root</c> and <c>--path</c>
///         to absolute canonical paths, and verifies that no symbolic links exist for <c>.codegraph</c>.
///     </para>
/// </remarks>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public static class SquadWorktreeMcp
{
    /// <summary>
    ///     Slices and replaces argument values for <c>--repo-root</c> and <c>--path</c> so that
    ///     relative roots are resolved to absolute canonical paths.
    /// </summary>
    /// <param name="args">The original process command-line arguments.</param>
    /// <param name="canonicalRoot">The canonical repository root path.</param>
    /// <returns>A new argument list with canonical paths spliced into <c>--repo-root</c> and <c>--path</c>.</returns>
    [UsedImplicitly]
    public static IReadOnlyList<string> SpliceArguments(IReadOnlyList<string> args, string canonicalRoot)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);

        string absoluteCanonicalRoot = Path.GetFullPath(canonicalRoot);
        List<string> spliced = new(args.Count);

        for (int i = 0; i < args.Count; i++)
        {
            string current = args[i];

            if (string.Equals(current, "--repo-root", StringComparison.Ordinal) && i + 1 < args.Count)
            {
                spliced.Add(current);
                spliced.Add(absoluteCanonicalRoot);
                i++;
            }
            else if (current.StartsWith("--repo-root=", StringComparison.Ordinal))
            {
                spliced.Add($"--repo-root={absoluteCanonicalRoot}");
            }
            else if (string.Equals(current, "--path", StringComparison.Ordinal) && i + 1 < args.Count)
            {
                spliced.Add(current);
                string pathVal = args[i + 1];
                string resolvedPath = ResolveAndValidateContainedPath(pathVal, absoluteCanonicalRoot);
                spliced.Add(resolvedPath);
                i++;
            }
            else if (current.StartsWith("--path=", StringComparison.Ordinal))
            {
                string pathVal = current["--path=".Length..];
                string resolvedPath = ResolveAndValidateContainedPath(pathVal, absoluteCanonicalRoot);
                spliced.Add($"--path={resolvedPath}");
            }
            else
            {
                spliced.Add(current);
            }
        }

        return spliced;
    }

    private static string ResolveAndValidateContainedPath(string pathVal, string absoluteCanonicalRoot)
    {
        if (string.IsNullOrWhiteSpace(pathVal))
        {
            throw new ArgumentException("Path traversal outside canonical repository root is prohibited.");
        }

        string resolvedPath = Path.IsPathRooted(pathVal)
            ? Path.GetFullPath(pathVal)
            : Path.GetFullPath(pathVal, absoluteCanonicalRoot);

        string prefix = absoluteCanonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? absoluteCanonicalRoot
            : absoluteCanonicalRoot + Path.DirectorySeparatorChar;

        if (!string.Equals(resolvedPath, absoluteCanonicalRoot, StringComparison.Ordinal) &&
            !resolvedPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Path traversal outside canonical repository root is prohibited.");
        }

        return resolvedPath;
    }

    /// <summary>
    ///     Verifies that <c>.codegraph</c> within the specified <paramref name="worktreeDirectory" />
    ///     is not a symbolic link or reparse point, failing closed if one is detected.
    /// </summary>
    /// <param name="worktreeDirectory">The path to the worktree directory.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="worktreeDirectory" /> is null or whitespace.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when <paramref name="worktreeDirectory" /> does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <c>.codegraph</c> is a symbolic link or reparse point.</exception>
    /// <remarks>
    ///     Symlinking <c>.codegraph</c> in a worktree can lead to index corruption or cross-worktree
    ///     database lock contention. Worktrees must access CodeGraph through MCP serve against
    ///     the canonical root instead.
    /// </remarks>
    [UsedImplicitly]
    public static void EnsureNoCodegraphSymlink(string worktreeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeDirectory);

        if (!Directory.Exists(worktreeDirectory))
        {
            throw new DirectoryNotFoundException($"Worktree directory '{worktreeDirectory}' does not exist.");
        }

        string codegraphPath = Path.Combine(worktreeDirectory, ".codegraph");

        DirectoryInfo dirInfo = new(codegraphPath);
        FileInfo fileInfo = new(codegraphPath);

        if (dirInfo.LinkTarget is not null || fileInfo.LinkTarget is not null)
            throw new InvalidOperationException(
                $"Worktree directory '{worktreeDirectory}' contains a symbolic link at '{codegraphPath}'. " +
                "Worktrees must not symlink .codegraph; access CodeGraph through the canonical root binding.");

        if (dirInfo.Exists && dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException(
                $"Worktree directory '{worktreeDirectory}' contains a reparse point at '{codegraphPath}'. " +
                "Worktrees must not symlink .codegraph; access CodeGraph through the canonical root binding.");

        if (fileInfo.Exists && fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException(
                $"Worktree directory '{worktreeDirectory}' contains a reparse point at '{codegraphPath}'. " +
                "Worktrees must not symlink .codegraph; access CodeGraph through the canonical root binding.");
    }

    /// <summary>
    ///     Alias for <see cref="EnsureNoCodegraphSymlink(string)" />.
    /// </summary>
    /// <param name="worktreeDirectory">The path to the worktree directory.</param>
    [UsedImplicitly]
    public static void VerifyNoCodegraphSymlink(string worktreeDirectory)
    {
        EnsureNoCodegraphSymlink(worktreeDirectory);
    }
}
