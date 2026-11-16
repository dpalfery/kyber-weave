using System.ComponentModel;
using KyberWeave.Core.Processes;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Review.Catalogue;

namespace KyberWeave.Data.Review;

/// <summary>Lists a repository's files from git when it can answer, otherwise by walking the file system.</summary>
/// <param name="programs">Starts git.</param>
public sealed class FileRepositoryFileLister(IProgramRunner programs) : IRepositoryFileLister
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    public IReadOnlyList<string> List(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        if (!Directory.Exists(repositoryRoot))
            throw new DirectoryNotFoundException($"Repository root '{repositoryRoot}' does not exist.");

        return ListFiles(repositoryRoot)
            .Where(file => IsRegularFile(repositoryRoot, file))
            .ToArray();
    }

    /// <summary>Repository-relative files, from git when the directory is a work tree, otherwise from a walk.</summary>
    private IReadOnlyList<string> ListFiles(string repositoryRoot)
    {
        IReadOnlyList<string>? fromGit = ListGitFiles(repositoryRoot);
        if (fromGit is not null)
            return fromGit.Where(file => !ProjectRootDetection.IsUnderExcludedDirectory(file)).ToArray();

        return WalkFiles(repositoryRoot);
    }

    /// <summary>
    /// Lists tracked and untracked-but-not-ignored files. Returns <see langword="null"/> when git is missing
    /// or the directory is not a work tree, so the caller walks the file system instead.
    /// </summary>
    private IReadOnlyList<string>? ListGitFiles(string repositoryRoot)
    {
        ProcessResult result;
        try
        {
            result = programs.Run(
                "git", ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], repositoryRoot, GitTimeout);
        }
        catch (Win32Exception)
        {
            return null;
        }

        if (result.ExitCode != 0)
            return null;

        return result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Walks one directory level at a time. Links are skipped rather than followed, so the walk cannot
    /// leave the repository, and excluded directories are never entered.
    /// </summary>
    private static IReadOnlyList<string> WalkFiles(string repositoryRoot)
    {
        List<string> files = [];
        Stack<string> pending = new();
        pending.Push(string.Empty);

        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            IgnoreInaccessible = true
        };

        while (pending.Count > 0)
        {
            string relative = pending.Pop();
            DirectoryInfo directory =
                new(relative.Length == 0 ? repositoryRoot : Path.Combine(repositoryRoot, relative));

            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos("*", options))
            {
                if (entry.LinkTarget is not null)
                    continue;

                string child = relative.Length == 0 ? entry.Name : $"{relative}/{entry.Name}";
                if (entry is DirectoryInfo)
                {
                    if (!ProjectRootDetection.ExcludedDirectories.Contains(entry.Name))
                        pending.Push(child);
                }
                else
                {
                    files.Add(child);
                }
            }
        }

        return files;
    }

    /// <summary>A regular file that still exists. A deleted-but-tracked file and a link are not markers.</summary>
    private static bool IsRegularFile(string repositoryRoot, string file)
    {
        FileInfo info = new(Path.Combine(repositoryRoot, file));
        return info.Exists && info.LinkTarget is null;
    }
}
