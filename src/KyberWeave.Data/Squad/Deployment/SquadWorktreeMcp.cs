using JetBrains.Annotations;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;

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
/// <param name="files">Reads links and attributes along the paths it checks.</param>
/// <param name="paths">Turns the canonical root and each <c>--path</c> value into full paths.</param>
/// <param name="environment">Supplies the base directory a relative root resolves against before it is refused as relative.</param>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class SquadWorktreeMcp(
    IFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment) : ISquadWorktreeMcp
{
    /// <summary>The longest link chain followed before a path is reported as unresolvable, as the platform's own limit.</summary>
    private const int MaxLinkHops = 40;

    /// <summary>
    ///     Slices and replaces argument values for <c>--repo-root</c> and <c>--path</c> so that
    ///     relative roots are resolved to absolute canonical paths.
    /// </summary>
    /// <param name="args">The original process command-line arguments.</param>
    /// <param name="canonicalRoot">The canonical repository root path.</param>
    /// <returns>A new argument list with canonical paths spliced into <c>--repo-root</c> and <c>--path</c>.</returns>
    [UsedImplicitly]
    public IReadOnlyList<string> SpliceArguments(IReadOnlyList<string> args, string canonicalRoot)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);

        if (!Path.IsPathRooted(canonicalRoot))
        {
            throw new ArgumentException(
                "The canonical root must be an absolute path; a relative value would resolve against the " +
                "caller's current directory, which may be the worktree rather than the canonical checkout.",
                nameof(canonicalRoot));
        }

        string absoluteCanonicalRoot = ResolveRealPath(paths.GetFullPath(canonicalRoot, environment.CurrentDirectory));
        List<string> spliced = new(args.Count);

        for (int i = 0; i < args.Count; i++)
        {
            string current = args[i];
            if (string.IsNullOrWhiteSpace(current))
            {
                throw new ArgumentException(
                    "Command-line arguments must not contain null or whitespace entries.",
                    nameof(args));
            }

            if (string.Equals(current, "--repo-root", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Count)
                {
                    throw new ArgumentException("--repo-root requires a value.", nameof(args));
                }

                if (string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    throw new ArgumentException("--repo-root requires a non-empty value.", nameof(args));
                }

                if (args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "--repo-root requires a value, but the next token is another flag.",
                        nameof(args));
                }

                spliced.Add(current);
                spliced.Add(absoluteCanonicalRoot);
                i++;
            }
            else if (current.StartsWith("--repo-root=", StringComparison.Ordinal))
            {
                spliced.Add($"--repo-root={absoluteCanonicalRoot}");
            }
            else if (string.Equals(current, "--path", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Count)
                {
                    throw new ArgumentException("--path requires a value.", nameof(args));
                }

                if (string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    throw new ArgumentException("--path requires a non-empty value.", nameof(args));
                }

                if (args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "--path requires a value, but the next token is another flag.",
                        nameof(args));
                }

                spliced.Add(current);
                spliced.Add(ResolveAndValidateContainedPath(args[i + 1], absoluteCanonicalRoot));
                i++;
            }
            else if (current.StartsWith("--path=", StringComparison.Ordinal))
            {
                string pathVal = current["--path=".Length..];
                spliced.Add($"--path={ResolveAndValidateContainedPath(pathVal, absoluteCanonicalRoot)}");
            }
            else
            {
                spliced.Add(current);
            }
        }

        return spliced;
    }

    private string ResolveAndValidateContainedPath(string pathVal, string absoluteCanonicalRoot)
    {
        if (string.IsNullOrWhiteSpace(pathVal))
        {
            throw new ArgumentException("Path traversal outside canonical repository root is prohibited.");
        }

        string resolvedPath = ResolveRealPath(
            Path.IsPathRooted(pathVal)
                ? paths.GetFullPath(pathVal, environment.CurrentDirectory)
                : paths.GetFullPath(pathVal, absoluteCanonicalRoot));

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
    ///     Resolves every symbolic link in an absolute path so the containment check sees the path
    ///     real I/O would reach, not just its lexically normalized spelling. A link's target is resolved
    ///     the same way, so links among the target's own parent folders are followed too.
    /// </summary>
    private string ResolveRealPath(string absolutePath)
    {
        int hops = 0;
        return ResolveRealPath(absolutePath, absolutePath, ref hops);
    }

    private string ResolveRealPath(string path, string originalPath, ref int hops)
    {
        string root = Path.GetPathRoot(path)
                      ?? throw new ArgumentException("The path must be absolute.", nameof(originalPath));
        string current = root;

        foreach (string segment in path[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);

            string? target = ReadLinkTarget(current, originalPath);
            if (target is not null)
            {
                if (++hops > MaxLinkHops)
                {
                    throw new ArgumentException($"Symbolic link '{current}' cannot be resolved.", nameof(originalPath));
                }

                string directory = Path.GetDirectoryName(current) ?? current;
                current = ResolveRealPath(paths.GetFullPath(target, directory), originalPath, ref hops);
            }
            else if ((files.DirectoryExists(current) || files.FileExists(current)) &&
                     files.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ArgumentException(
                    $"Reparse point '{current}' cannot be resolved safely.",
                    nameof(originalPath));
            }
        }

        return current;
    }

    private string? ReadLinkTarget(string link, string originalPath)
    {
        try
        {
            return files.GetLinkTarget(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"Symbolic link '{link}' cannot be resolved.", nameof(originalPath), ex);
        }
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
    public void EnsureNoCodegraphSymlink(string worktreeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeDirectory);

        if (!files.DirectoryExists(worktreeDirectory))
        {
            throw new DirectoryNotFoundException($"Worktree directory '{worktreeDirectory}' does not exist.");
        }

        string codegraphPath = Path.Combine(worktreeDirectory, ".codegraph");

        if (files.GetLinkTarget(codegraphPath) is not null)
            throw new InvalidOperationException(
                $"Worktree directory '{worktreeDirectory}' contains a symbolic link at '{codegraphPath}'. " +
                "Worktrees must not symlink .codegraph; access CodeGraph through the canonical root binding.");

        if ((files.DirectoryExists(codegraphPath) || files.FileExists(codegraphPath)) &&
            files.GetAttributes(codegraphPath).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException(
                $"Worktree directory '{worktreeDirectory}' contains a reparse point at '{codegraphPath}'. " +
                "Worktrees must not symlink .codegraph; access CodeGraph through the canonical root binding.");
    }

}
